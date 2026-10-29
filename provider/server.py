"""Internal-only YouTube catalog and audio adapter; no public listening port."""
import json
import base64
import atexit
import os
import re
import select
import signal
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path
from collections import OrderedDict
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import requests
from ytmusicapi import YTMusic

PROXY = os.environ.get("YOUTUBE_PROXY", "").strip()
PROXIES = {"http": PROXY, "https": PROXY} if PROXY else {}
VIDEO_ID = re.compile(r"^[a-zA-Z0-9_-]{11}$")
slots = threading.BoundedSemaphore(2)
metadata_slots = threading.BoundedSemaphore(2)
catalog_lock = threading.Lock()
cache_lock = threading.Lock()
audio_cache = OrderedDict()
prefix_cache = OrderedDict()
track_cache = OrderedDict()
radio_cache = OrderedDict()
artwork_cache = OrderedDict()
artwork_slots = threading.BoundedSemaphore(2)


class NetworkSession(requests.Session):
    def __init__(self):
        super().__init__()
        self.trust_env = False
        self.proxies.update(PROXIES)

    def request(self, method, url, **kwargs):
        kwargs.setdefault("timeout", (5, 15))
        return super().request(method, url, **kwargs)


# Current ru response labels break filtered search in ytmusicapi 1.12.3.
# English protocol labels still accept Russian queries and retain original titles.
catalog = YTMusic(requests_session=NetworkSession(), proxies=PROXIES, language="en")


def normalize_tracks(items):
    tracks = []
    seen = set()
    for item in items:
        video_id = item.get('videoId', '')
        if not VIDEO_ID.fullmatch(video_id) or video_id in seen:
            continue
        seen.add(video_id)
        thumbnails = item.get('thumbnails') or item.get('thumbnail') or []
        thumbnails = sorted(thumbnails, key=lambda t: t.get('width', 0), reverse=True)
        tracks.append({'id':'youtube:' + video_id, 'title':item.get('title') or 'Без названия',
                       'artist':', '.join(a.get('name') or '' for a in (item.get('artists') or [])),
                       'provider':'youtube', 'duration':item.get('duration') or item.get('length'),
                       'thumbnail':next((t['url'] for t in thumbnails if t.get('url','').startswith('https://')), None)})
    with cache_lock:
        for track in tracks:
            track_cache[track['id'][8:]] = track
            track_cache.move_to_end(track['id'][8:])
        while len(track_cache) > 200:
            track_cache.popitem(last=False)
    return tracks


def search(query):
    with catalog_lock:
        found = catalog.search(query, filter='songs', limit=12)
    return normalize_tracks(found[:12])


def radio(video_id):
    with cache_lock:
        cached = radio_cache.get(video_id)
        if cached and time.monotonic() - cached[0] < 300:
            return cached[1]
    with catalog_lock:
        playlist = catalog.get_watch_playlist(videoId=video_id, radio=True, limit=12)
    tracks = normalize_tracks(playlist.get('tracks', [])[:16])
    with cache_lock:
        radio_cache[video_id] = (time.monotonic(), tracks)
        radio_cache.move_to_end(video_id)
        while len(radio_cache) > 16:
            radio_cache.popitem(last=False)
    return tracks


def artwork(video_id, preview=False, thumbnail=None, preview_size=96):
    preview_size = 320 if preview_size == 320 else 96
    key = (video_id, preview, preview_size if preview else 0)
    with cache_lock:
        cached = artwork_cache.get(key)
        if cached:
            artwork_cache.move_to_end(key)
            return cached
    # Watch metadata includes the album art at up to 544px, unlike the 120px
    # preview returned by search. Reuse this request later for the Next button.
    if not preview:
        try: radio(video_id)
        except Exception: pass
    with cache_lock:
        track = track_cache.get(video_id)
    url = (track or {}).get('thumbnail') or thumbnail or ''
    if len(url) > 2048:
        raise ValueError('Artwork URL is too long')
    host = urlparse(url).hostname or ''
    if not url.startswith('https://') or not (host == 'i.ytimg.com' or host.endswith('.googleusercontent.com')):
        raise ValueError('No supported artwork')
    if preview and host.endswith('.googleusercontent.com'):
        url = re.sub(r'=w\d+-h\d+', f'=w{preview_size}-h{preview_size}', url)
    with NetworkSession() as session:
        with session.get(url, stream=True, allow_redirects=False) as response:
            response.raise_for_status()
            content_type = response.headers.get('Content-Type','').split(';')[0].lower()
            if content_type not in ('image/jpeg', 'image/png', 'image/webp'):
                raise ValueError('Unsupported image')
            chunks, size = [], 0
            for chunk in response.iter_content(16384):
                size += len(chunk)
                if size > 2 * 1024 * 1024:
                    raise ValueError('Artwork is too large')
                chunks.append(chunk)
    result = (b''.join(chunks), content_type)
    if not result[0]: raise ValueError('Empty artwork')
    with cache_lock:
        artwork_cache[key] = result
        while sum(len(item[0]) for item in artwork_cache.values()) > 8 * 1024 * 1024:
            artwork_cache.popitem(last=False)
    return result


class ResolveCancelled(Exception): pass


EXTRACTOR_PATH = Path(__file__).with_name('extract.py')
extractor_lock = threading.Lock()
extractor_worker = None


def stop_extractor():
    global extractor_worker
    process, extractor_worker = extractor_worker, None
    if process is None: return
    if process.poll() is None:
        try: os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError: pass
        try: process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            try: os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
            process.wait()
    for pipe in (process.stdin,process.stdout):
        if pipe: pipe.close()


def worker_message(process, cancelled, deadline):
    data=bytearray()
    while True:
        if cancelled and cancelled(): raise ResolveCancelled()
        if time.monotonic()>deadline: raise TimeoutError('Source preparation timed out')
        readable,_,_=select.select([process.stdout],[],[],.1)
        if readable:
            chunk=os.read(process.stdout.fileno(),65536)
            if not chunk: raise ValueError('Extraction worker stopped')
            data.extend(chunk)
            if len(data)>196608: raise ValueError('Oversized extraction response')
            if b'\n' in data:
                line,rest=data.split(b'\n',1)
                if rest: raise ValueError('Unexpected extraction response')
                return json.loads(line)


def ready_extractor(cancelled,deadline):
    global extractor_worker
    if extractor_worker is not None and extractor_worker.poll() is not None: stop_extractor()
    if extractor_worker is None:
        extractor_worker=subprocess.Popen([sys.executable,str(EXTRACTOR_PATH),'--worker'],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,start_new_session=True,bufsize=0)
        if worker_message(extractor_worker,cancelled,deadline).get('ready') is not True:
            raise ValueError('Extraction worker not ready')
    return extractor_worker


def extract_source(video_id, cancelled=None):
    deadline = time.monotonic() + 65
    while not extractor_lock.acquire(timeout=.1):
        if cancelled and cancelled(): raise ResolveCancelled()
        if time.monotonic()>deadline: raise TimeoutError('Extraction busy')
    try:
        try:
            process=ready_extractor(cancelled,deadline)
            process.stdin.write((video_id+'\n').encode()); process.stdin.flush()
            message=worker_message(process,cancelled,deadline)
        except BaseException:
            stop_extractor() # Still kill the entire group, including Node.
            raise
        if message.get('error'): raise ValueError('Source unavailable')
        return message['result']
    finally: extractor_lock.release()


def prime_extractor():
    with extractor_lock:
        try: ready_extractor(None,time.monotonic()+30)
        except Exception: stop_extractor()


atexit.register(stop_extractor)


def resolve(video_id, cancelled=None):
    with cache_lock:
        cached = audio_cache.get(video_id)
        if cached is not None and time.monotonic() - cached[0] < 1200:
            audio_cache.move_to_end(video_id)
            return {**cached[3],'strategy':'cache','extractMilliseconds':0,'probeMilliseconds':0}
    info = extract_source(video_id, cancelled)
    if info.get("unsupported"):
        raise ValueError("Unsupported source")
    source = info.get("url", "")
    host = urlparse(source).hostname or ""
    if not source.startswith("https://") or not host.endswith(".googlevideo.com"):
        raise ValueError("Unexpected media host")
    headers = info.get("headers", {})
    # The disposable helper has already validated the first audio bytes, including
    # the fast client's URL. Do not repeat that network round trip here.
    with cache_lock:
        details = {key:info.get(key) for key in ('strategy','extractMilliseconds','probeMilliseconds','codec','bitrateKbps','sampleRate','channels')}
        audio_cache[video_id] = (time.monotonic(), source, headers, details)
        prefix_cache.pop(video_id,None)
        if info.get('prefix') and isinstance(info.get('totalBytes'),int):
            data=base64.b64decode(info['prefix'],validate=True)
            if 0<len(data)<=65536 and info['totalBytes']>=len(data):
                mime=info.get('mimeType','')
                if mime not in ('audio/mp4','video/mp4','audio/webm','video/webm','application/octet-stream'): mime='application/octet-stream'
                prefix_cache[video_id]=(source,data,mime,info['totalBytes'])
                while len(prefix_cache)>8: prefix_cache.popitem(last=False)
        audio_cache.move_to_end(video_id)
        while len(audio_cache) > 24:
            audio_cache.popitem(last=False)
    return details


def forward_audio(handler, source, headers, prefix=None):
    """Resume interrupted upstream downloads without repeating already sent bytes."""
    requested = handler.headers.get("Range", "")
    if requested and not re.fullmatch(r"bytes=\d+-\d*", requested):
        requested = ""
    position = int(requested[6:].split("-")[0]) if requested else 0
    end = requested.split("-", 1)[1] if requested else ""
    started = False
    remaining = None
    if prefix and position<len(prefix[0]):
        data,mime,total=prefix
        last=min(total-1,int(end)) if end else total-1
        if last<position: raise ValueError('Invalid audio range')
        handler.send_response(206 if requested else 200)
        handler.send_header('Content-Type',mime)
        handler.send_header('Content-Length',str(last-position+1))
        handler.send_header('Accept-Ranges','bytes')
        if requested: handler.send_header('Content-Range',f'bytes {position}-{last}/{total}')
        handler.send_header('Connection','close'); handler.end_headers(); handler.close_connection=True
        stop=min(len(data),last+1)
        handler.wfile.write(data[position:stop]); handler.wfile.flush()
        position=stop; remaining=last-position+1; started=True
        if remaining==0:return
    for attempt in range(4):
        upstream_headers = dict(headers)
        if requested or started:
            upstream_headers["Range"] = f"bytes={position}-{end}"
        try:
            with NetworkSession() as session:
                with session.get(source, headers=upstream_headers, stream=True) as response:
                    response.raise_for_status()
                    if started:
                        content_range = response.headers.get("Content-Range", "")
                        if response.status_code != 206 or not content_range.startswith(f"bytes {position}-"):
                            raise ValueError("Upstream ignored resume range")
                    else:
                        if response.status_code == 200:
                            position = 0
                        handler.send_response(response.status_code)
                        for header in ("Content-Type", "Content-Length", "Content-Range", "Accept-Ranges"):
                            if header in response.headers:
                                handler.send_header(header, response.headers[header])
                        handler.send_header("Connection", "close")
                        handler.end_headers()
                        handler.close_connection = True
                        remaining = int(response.headers["Content-Length"]) if "Content-Length" in response.headers else None
                        started = True
                    for chunk in response.iter_content(4096):
                        if not chunk:
                            continue
                        if remaining is not None: chunk=chunk[:remaining]
                        handler.wfile.write(chunk)
                        position += len(chunk)
                        if remaining is not None:
                            remaining -= len(chunk)
                            if remaining==0:return
                    if remaining is None or remaining <= 0:
                        return
                    raise requests.ConnectionError("Truncated audio")
        except requests.RequestException:
            if attempt == 3:
                raise
            time.sleep(.3 * (attempt + 1))


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        # Do not log signed media URLs, proxy credentials or search history.
        pass

    def disconnected(self):
        readable, _, _ = select.select([self.connection], [], [], 0)
        return bool(readable) and self.connection.recv(1, socket.MSG_PEEK | socket.MSG_DONTWAIT) == b''

    def send_json(self, code, value):
        data = json.dumps(value, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        params = parse_qs(url.query)
        if url.path == "/healthz":
            return self.send_json(200, {"ready": True, "proxyConfigured": bool(PROXY)})
        if url.path == '/artwork':
            video_id = params.get('id', [''])[0]
            if not VIDEO_ID.fullmatch(video_id):
                return self.send_json(400, {'error':'Invalid track'})
            # Artwork cannot occupy the two audio/extraction slots.
            if not artwork_slots.acquire(timeout=5):
                return self.send_json(503, {'error':'Artwork busy'})
            try:
                size = params.get('size',[''])[0]
                data, content_type = artwork(video_id, size in ('small','card'), params.get('thumbnail',[''])[0], 320 if size == 'card' else 96)
                self.send_response(200)
                self.send_header('Content-Type', content_type)
                self.send_header('Content-Length', str(len(data)))
                self.send_header('Cache-Control', 'private, max-age=3600')
                self.end_headers()
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError): pass
            except Exception: self.send_json(502, {'error':'Artwork unavailable'})
            finally: artwork_slots.release()
            return
        operation_slots = metadata_slots if url.path in ('/search','/radio') else slots
        if not operation_slots.acquire(blocking=False):
            return self.send_json(503, {"error": "Источник занят. Повтори запрос чуть позже."})
        try:
            if url.path == "/search":
                query = params.get("q", [""])[0].strip()
                if not 2 <= len(query) <= 128:
                    return self.send_json(400, {"error": "Введи от 2 до 128 символов."})
                return self.send_json(200, search(query))
            video_id = params.get("id", [""])[0]
            if not VIDEO_ID.fullmatch(video_id):
                return self.send_json(400, {"error": "Некорректный идентификатор трека."})
            if url.path == "/resolve":
                timings = resolve(video_id, self.disconnected)
                return self.send_json(200, {"ready": True, **timings})
            if url.path == '/radio':
                return self.send_json(200, radio(video_id))
            if url.path == "/audio":
                with cache_lock:
                    cached = audio_cache.get(video_id)
                    prefix = prefix_cache.get(video_id)
                if cached is None or time.monotonic() - cached[0] > 1800:
                    return self.send_json(410, {"error": "Открой трек заново."})
                _, source, headers, _ = cached
                forward_audio(self, source, headers, prefix[1:] if prefix and prefix[0]==source else None)
                return
            return self.send_json(404, {"error": "Unknown endpoint"})
        except (BrokenPipeError, ConnectionResetError, ResolveCancelled):
            pass
        except Exception:
            # Return a safe, actionable error rather than upstream URLs or cookies.
            if url.path == "/audio":
                with cache_lock:
                    audio_cache.pop(video_id, None)
                    prefix_cache.pop(video_id,None)
                self.close_connection = True
            else:
                self.send_json(502, {"error": "YouTube недоступен с Raspberry Pi или не отдал аудио. Проверь подключение и прокси."})
        finally:
            operation_slots.release()


if __name__ == "__main__":
    threading.Thread(target=prime_extractor,daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", 8081), Handler).serve_forever()
