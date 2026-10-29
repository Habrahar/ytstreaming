"""Internal-only YouTube catalog and audio adapter; no public listening port."""
import json
import os
import re
import threading
import time
from collections import OrderedDict
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import requests
from ytmusicapi import YTMusic

PROXY = os.environ.get("YOUTUBE_PROXY", "").strip()
PROXIES = {"http": PROXY, "https": PROXY} if PROXY else {}
VIDEO_ID = re.compile(r"^[a-zA-Z0-9_-]{11}$")
slots = threading.BoundedSemaphore(2)
catalog_lock = threading.Lock()
cache_lock = threading.Lock()
audio_cache = OrderedDict()


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


def search(query):
    with catalog_lock:
        found = catalog.search(query, filter="songs", limit=12)
    return [{"id": "youtube:" + item["videoId"], "title": item.get("title", "Без названия"),
             "artist": ", ".join(a.get("name", "") for a in item.get("artists", [])),
             "provider": "youtube", "duration": item.get("duration"),
             "thumbnail": next((t["url"] for t in reversed(item.get("thumbnails", []))
                                if t.get("url", "").startswith("https://")), None)}
            for item in found[:12] if VIDEO_ID.fullmatch(item.get("videoId", ""))]


class QuietLogger:
    def debug(self, *_): pass
    def warning(self, *_): pass
    def error(self, *_): pass


def resolve(video_id):
    import yt_dlp
    options = {"format": "bestaudio[ext=m4a]/bestaudio", "quiet": True,
               "no_warnings": True, "logger": QuietLogger(), "noplaylist": True,
               "cachedir": False, "socket_timeout": 15, "retries": 1,
               "extractor_retries": 1, "js_runtimes": {"node": {}},
               "proxy": PROXY or ""}
    with yt_dlp.YoutubeDL(options) as downloader:
        info = downloader.extract_info("https://www.youtube.com/watch?v=" + video_id, download=False)
    if info.get("is_live") or info.get("has_drm"):
        raise ValueError("Unsupported source")
    source = info.get("url", "")
    host = urlparse(source).hostname or ""
    if not source.startswith("https://") or not host.endswith(".googlevideo.com"):
        raise ValueError("Unexpected media host")
    headers = info.get("http_headers", {})
    # Resolve before switching the shared player; inaccessible audio leaves it running.
    with NetworkSession() as session:
        with session.get(source, headers={**headers, "Range": "bytes=0-4095"}, stream=True) as response:
            response.raise_for_status()
            if not next(response.iter_content(4096), b""):
                raise ValueError("Empty audio source")
    with cache_lock:
        audio_cache[video_id] = (time.monotonic(), source, headers)
        audio_cache.move_to_end(video_id)
        while len(audio_cache) > 24:
            audio_cache.popitem(last=False)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        # Do not log signed media URLs, proxy credentials or search history.
        pass

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
        if not slots.acquire(blocking=False):
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
                resolve(video_id)
                return self.send_json(200, {"ready": True})
            if url.path == "/audio":
                with cache_lock:
                    cached = audio_cache.get(video_id)
                if cached is None or time.monotonic() - cached[0] > 1800:
                    return self.send_json(410, {"error": "Открой трек заново."})
                _, source, headers = cached
                requested_range = self.headers.get("Range", "")
                if requested_range and re.fullmatch(r"bytes=\d+-\d*", requested_range):
                    headers = {**headers, "Range": requested_range}
                with NetworkSession() as session:
                    with session.get(source, headers=headers, stream=True) as response:
                        response.raise_for_status()
                        self.send_response(response.status_code)
                        for header in ("Content-Type", "Content-Length", "Content-Range", "Accept-Ranges"):
                            if header in response.headers:
                                self.send_header(header, response.headers[header])
                        self.send_header("Connection", "close")
                        self.end_headers()
                        self.close_connection = True
                        for chunk in response.iter_content(16384):
                            self.wfile.write(chunk)
                return
            return self.send_json(404, {"error": "Unknown endpoint"})
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception:
            # Return a safe, actionable error rather than upstream URLs or cookies.
            if url.path == "/audio":
                self.close_connection = True
            else:
                self.send_json(502, {"error": "YouTube недоступен с Raspberry Pi или не отдал аудио. Проверь подключение и прокси."})
        finally:
            slots.release()


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 8081), Handler).serve_forever()
