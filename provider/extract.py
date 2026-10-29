"""Persistent public-audio session with validated URLs and full extraction fallback."""
import json
import base64
import os
import re
import signal
import select
import sys
import time
from urllib.parse import urlparse
import requests
import yt_dlp
from yt_dlp.extractor.youtube import YoutubeIE


class QuietLogger:
    def debug(self, *_): pass
    def warning(self, *_): pass
    def error(self, *_): pass


class FastClientTimeout(Exception): pass


class SessionYoutubeIE(YoutubeIE):
    @classmethod
    def ie_key(cls): return 'Youtube'

    def __init__(self, owner):
        super().__init__()
        self.owner = owner

    def _extract_visitor_data(self, *args):
        value = super()._extract_visitor_data(*args)
        if value and not self.owner.quick:
            self.owner.visitor = value
            self.owner.visitor_at = time.monotonic()
        return value


class ExtractionSession:
    """Single-worker use only. Context/cookies and connection pools stay in RAM."""
    def __init__(self, proxy):
        self.proxy = proxy
        self.visitor = None
        self.visitor_at = 0
        self.quick = False
        self.downloader = None
        self.network = None

    def fast(self):
        if self.downloader is None:
            options = base_options(self.proxy)
            options.update({'socket_timeout':6,'retries':0,'extractor_retries':0,
                            'extractor_args':{'youtube':{'player_client':['visionos'],'skip':['hls','dash']}}})
            self.downloader = yt_dlp.YoutubeDL(options, auto_init=False)
            self.downloader.add_info_extractor(SessionYoutubeIE(self))
        return self.downloader

    def connection(self):
        if self.network is None:
            self.network = requests.Session()
            self.network.trust_env = False
            if self.proxy: self.network.proxies.update({'http':self.proxy,'https':self.proxy})
        return self.network

    def prime(self):
        old_visitor, old_at = self.visitor, self.visitor_at
        previous = signal.signal(signal.SIGALRM, expire)
        try:
            signal.setitimer(signal.ITIMER_REAL, 6)
            self.quick = False
            downloader = self.fast()
            downloader.params['extractor_args'] = {'youtube':{'player_client':['visionos'],'skip':['hls','dash']}}
            ie = downloader.get_info_extractor('Youtube')
            ie.initialize()
            cfg = ie._download_ytcfg('web', 'startup')
            ie._extract_visitor_data(cfg)
        except Exception:
            self.visitor = old_visitor if time.monotonic()-old_at < 900 else None
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous)

    def close(self):
        if self.downloader is not None: self.downloader.close()
        if self.network is not None: self.network.close()
        self.downloader = self.network = None

    def refresh_if_due(self):
        if time.monotonic()-self.visitor_at >= 600: self.prime()


def expire(*_): raise FastClientTimeout()


def base_options(proxy):
    return {'format':'bestaudio[ext=m4a]/bestaudio','quiet':True,'no_warnings':True,
            'logger':QuietLogger(),'noplaylist':True,'cachedir':'/tmp/yt-dlp-cache',
            'socket_timeout':12,'retries':1,'extractor_retries':1,'proxy':proxy}


def validate_audio(info, proxy, session=None):
    if info.get('is_live') or info.get('has_drm'):
        raise ValueError('Unsupported source')
    source = info.get('url', '')
    host = urlparse(source).hostname or ''
    if not source.startswith('https://') or not host.endswith('.googlevideo.com'):
        raise ValueError('Unexpected media host')
    headers = info.get('http_headers', {})
    own_session = session is None
    if own_session:
        session = requests.Session()
        session.trust_env = False
        if proxy: session.proxies.update({'http':proxy,'https':proxy})
    try:
        with session.get(source, headers={**headers,'Range':'bytes=0-262143'}, stream=True, timeout=(5,8)) as response:
            response.raise_for_status()
            prefix = bytearray()
            for chunk in response.iter_content(4096):
                prefix.extend(chunk[:262144-len(prefix)])
                if len(prefix) >= 262144: break
            if not prefix:
                raise ValueError('Empty audio')
            match = re.fullmatch(r'bytes 0-\d+/(\d+)', response.headers.get('Content-Range',''))
            total = int(match[1]) if response.status_code == 206 and match else int(response.headers.get('Content-Length','0')) if response.status_code == 200 else 0
            content_type = response.headers.get('Content-Type','application/octet-stream').split(';')[0]
    finally:
        if own_session: session.close()
    return {'url':source,'headers':headers,'unsupported':False,
            'codec':info.get('acodec'),'bitrateKbps':info.get('abr'),
            'sampleRate':info.get('asr'),'channels':info.get('audio_channels'),
            'prefix':base64.b64encode(prefix).decode() if total >= len(prefix) else None,
            'totalBytes':total,'mimeType':content_type}


def prepare(video_id, session=None):
    started = time.monotonic()
    proxy = os.environ.get('YOUTUBE_PROXY','').strip()
    own_session = session is None
    session = session or ExtractionSession(proxy)
    strategies = ['visionos','standard']
    if session.visitor and time.monotonic()-session.visitor_at < 900: strategies.insert(0,'visionos-session')
    try:
        for strategy in strategies:
            quick = strategy == 'visionos-session'
            fast = strategy != 'standard'
            previous_handler = None
            try:
                if fast:
                    previous_handler = signal.signal(signal.SIGALRM, expire)
                    signal.setitimer(signal.ITIMER_REAL, 8 if quick else 18)
                    session.quick = quick
                    downloader = session.fast()
                    args = {'player_client':['visionos'],'skip':['hls','dash']}
                    if quick:
                        args.update({'player_skip':['webpage','configs','js','initial_data'],'visitor_data':[session.visitor]})
                    downloader.params['extractor_args'] = {'youtube':args}
                    info = downloader.extract_info('https://www.youtube.com/watch?v=' + video_id, download=False)
                else:
                    options = base_options(proxy)
                    options['js_runtimes'] = {'node':{}}
                    with yt_dlp.YoutubeDL(options) as downloader:
                        info = downloader.extract_info('https://www.youtube.com/watch?v=' + video_id, download=False)
                extracted = time.monotonic()
                result = validate_audio(info, proxy, session.connection())
                result.update({'strategy':strategy,'extractMilliseconds':round((extracted-started)*1000),
                               'probeMilliseconds':round((time.monotonic()-extracted)*1000)})
                return result
            except Exception:
                if quick: session.visitor = None
                if not fast: raise
            finally:
                if fast:
                    signal.setitimer(signal.ITIMER_REAL, 0)
                    signal.signal(signal.SIGALRM, previous_handler)
        raise ValueError('No audio source')
    finally:
        if own_session: session.close()


def run_worker():
    """Keep imports/context warm; cancel only the forked job and its JS children."""
    session = ExtractionSession(os.environ.get('YOUTUBE_PROXY','').strip())
    child = None
    def cancel_job(*_):
        if child:
            try: os.killpg(child, signal.SIGTERM)
            except ProcessLookupError: pass
    def terminate(*_):
        cancel_job()
        raise SystemExit(0)
    signal.signal(signal.SIGUSR1, cancel_job)
    signal.signal(signal.SIGTERM, terminate)
    session.prime()
    session.close() # Never inherit live HTTP connections into a child.
    print(json.dumps({'ready':True}), flush=True)
    try:
        while True:
            readable, _, _ = select.select([sys.stdin], [], [], 30)
            if not readable:
                session.refresh_if_due(); session.close()
                continue
            line = sys.stdin.readline()
            if not line: break
            video_id=line.strip()
            if not re.fullmatch(r'[a-zA-Z0-9_-]{11}',video_id):
                print(json.dumps({'error':True}),flush=True); continue
            read_fd, write_fd = os.pipe()
            child = os.fork()
            if child == 0:
                os.setsid()
                signal.signal(signal.SIGTERM, signal.SIG_DFL)
                signal.signal(signal.SIGUSR1, signal.SIG_IGN)
                os.close(read_fd)
                try:
                    try: message={'result':prepare(video_id, session),'visitor':session.visitor,'visitorAt':session.visitor_at}
                    except Exception: message={'error':True}
                    with os.fdopen(write_fd,'w') as output: json.dump(message,output)
                finally: os._exit(0)
            os.close(write_fd)
            try:
                data=bytearray()
                while True:
                    chunk=os.read(read_fd,65536)
                    if not chunk: break
                    data.extend(chunk)
                    if len(data)>524288: raise ValueError('Oversized job result')
                message=json.loads(data)
                if isinstance(message.get('visitor'),str) and len(message['visitor'])<=4096:
                    session.visitor=message['visitor']; session.visitor_at=message['visitorAt']
                elif 'visitor' in message: session.visitor=None
                reply={'result':message['result']} if 'result' in message else {'error':True}
            except Exception: reply={'error':True}
            finally:
                os.close(read_fd)
                cancel_job()
                os.waitpid(child,0); child=None
            print(json.dumps(reply),flush=True)
    finally:
        cancel_job()
        if child: os.waitpid(child,0)
        session.close()


if __name__ == '__main__':
    if sys.argv[1] == '--worker':
        run_worker()
        raise SystemExit(0)
    video_id = sys.argv[1]
    if not re.fullmatch(r'[a-zA-Z0-9_-]{11}', video_id): raise SystemExit(2)
    try: print(json.dumps(prepare(video_id)))
    except Exception: raise SystemExit(1)
