"""Integration check against a running bridge; changes only its test playback.
Usage: python scripts/smoke.py http://127.0.0.1:8090
"""
import concurrent.futures
import json
import sys
import time
import urllib.error
import urllib.request

base = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:8090").rstrip("/")


def call(path, body=None, origin=None):
    headers = {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if origin:
        headers["Origin"] = origin
    request = urllib.request.Request(base + path,
        data=None if body is None else json.dumps(body).encode(), headers=headers)
    with urllib.request.urlopen(request, timeout=12) as response:
        return json.load(response)


def ready():
    for _ in range(50):
        status = call("/api/status")
        if status["ready"]:
            return status
        time.sleep(.2)
    raise AssertionError("Encoder did not become ready")


def sample():
    with urllib.request.urlopen(base + "/stream.mp3", timeout=12) as stream:
        assert stream.headers.get_content_type() == "audio/mpeg"
        data = stream.read(24000)
        assert len(data) == 24000
        assert any(data[i] == 255 and data[i + 1] & 224 == 224 for i in range(len(data) - 1)), "No MPEG frame sync"
        return len(data)


assert len(call("/api/tracks")) == 2
assert call("/api/tracks?q=melody")[0]["id"] == "melody"
assert call("/api/tracks?q=nonexistent") == []
call("/api/player/play", {"id": "melody"})
ready()
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
    sizes = list(pool.map(lambda _: sample(), range(2)))
assert sizes == [24000, 24000]
call("/api/player/next", {})
assert ready()["track"]["id"] == "pulse"
assert sample() == 24000
try:
    call("/api/player/play", {"id": "unknown"})
    raise AssertionError("Unknown track was accepted")
except urllib.error.HTTPError as error:
    assert error.code == 404
try:
    call("/api/player/stop", {}, "http://untrusted.invalid")
    raise AssertionError("Cross-origin control was accepted")
except urllib.error.HTTPError as error:
    assert error.code == 403
call("/api/player/stop", {})
assert call("/api/status")["state"] == "stopped"
try:
    sample()
    raise AssertionError("Stopped stream accepted a listener")
except urllib.error.HTTPError as error:
    assert error.code == 503
call("/api/player/play", {"id": "melody"})
ready()
print("PASS: search, 2 simultaneous MP3 listeners, next, unknown track, origin guard, stop, replay")
