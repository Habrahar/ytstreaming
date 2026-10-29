"""Disposable extraction process with a validated fast client and standard fallback."""
import json
import base64
import os
import re
import signal
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


def validate_audio(info, proxy):
    if info.get('is_live') or info.get('has_drm'):
        raise ValueError('Unsupported source')
    source = info.get('url', '')
    host = urlparse(source).hostname or ''
    if not source.startswith('https://') or not host.endswith('.googlevideo.com'):
        raise ValueError('Unexpected media host')
    headers = info.get('http_headers', {})
    with requests.Session() as session:
        session.trust_env = False
        if proxy: session.proxies.update({'http':proxy,'https':proxy})
        with session.get(source, headers={**headers,'Range':'bytes=0-65535'}, stream=True, timeout=(5,8)) as response:
            response.raise_for_status()
            prefix = bytearray()
            for chunk in response.iter_content(4096):
                prefix.extend(chunk[:65536-len(prefix)])
                if len(prefix) >= 65536: break
            if not prefix:
                raise ValueError('Empty audio')
            match = re.fullmatch(r'bytes 0-\d+/(\d+)', response.headers.get('Content-Range',''))
            total = int(match[1]) if response.status_code == 206 and match else int(response.headers.get('Content-Length','0')) if response.status_code == 200 else 0
            content_type = response.headers.get('Content-Type','application/octet-stream').split(';')[0]
    return {'url':source,'headers':headers,'unsupported':False,
            'codec':info.get('acodec'),'bitrateKbps':info.get('abr'),
            'sampleRate':info.get('asr'),'channels':info.get('audio_channels'),
            'prefix':base64.b64encode(prefix).decode() if total >= len(prefix) else None,
            'totalBytes':total,'mimeType':content_type}


def prepare(video_id):
    started = time.monotonic()
    proxy = os.environ.get('YOUTUBE_PROXY','').strip()
    base = {'format':'bestaudio[ext=m4a]/bestaudio','quiet':True,'no_warnings':True,
            'logger':QuietLogger(),'noplaylist':True,'cachedir':'/tmp/yt-dlp-cache',
            'socket_timeout':12,'retries':1,'extractor_retries':1,'proxy':proxy}
    for strategy in ('visionos','standard'):
        options = dict(base)
        fast = strategy == 'visionos'
        if fast:
            options.update({'socket_timeout':6,'retries':0,'extractor_retries':0,
                            'extractor_args':{'youtube':{'player_client':['visionos'],'skip':['hls','dash']}}})
        else: options['js_runtimes'] = {'node':{}}
        previous_handler = None
        timer_set = False
        try:
            if fast:
                def expire(*_): raise FastClientTimeout()
                previous_handler = signal.signal(signal.SIGALRM, expire)
                signal.setitimer(signal.ITIMER_REAL, 18)
                timer_set = True
            with yt_dlp.YoutubeDL(options, auto_init=not fast) as downloader:
                if fast: downloader.add_info_extractor(YoutubeIE())
                info = downloader.extract_info('https://www.youtube.com/watch?v=' + video_id, download=False)
            extracted = time.monotonic()
            result = validate_audio(info, proxy)
            result.update({'strategy':strategy,'extractMilliseconds':round((extracted-started)*1000),
                           'probeMilliseconds':round((time.monotonic()-extracted)*1000)})
            return result
        except Exception:
            if not fast: raise
        finally:
            if timer_set:
                signal.setitimer(signal.ITIMER_REAL, 0)
                signal.signal(signal.SIGALRM, previous_handler)
    raise ValueError('No audio source')


if __name__ == '__main__':
    if sys.argv[1] == '--worker':
        print(json.dumps({'ready':True}), flush=True)
        for line in sys.stdin:
            video_id=line.strip()
            try:
                if not re.fullmatch(r'[a-zA-Z0-9_-]{11}',video_id): raise ValueError('Invalid ID')
                print(json.dumps({'result':prepare(video_id)}),flush=True)
            except Exception: print(json.dumps({'error':True}),flush=True)
        raise SystemExit(0)
    video_id = sys.argv[1]
    if not re.fullmatch(r'[a-zA-Z0-9_-]{11}', video_id): raise SystemExit(2)
    try: print(json.dumps(prepare(video_id)))
    except Exception: raise SystemExit(1)
