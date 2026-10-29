"""Live integration check: searches public YouTube Music and plays one result.
Usage: python scripts/youtube-smoke.py http://127.0.0.1:8090 [search query]
Changes only this project's shared playback. No account credentials are used.
"""
import json
import sys
import time
import urllib.parse
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")
base = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://127.0.0.1:8090"
query = sys.argv[2] if len(sys.argv) > 2 else "Scott Buckley"


def api(path, data=None):
    request = urllib.request.Request(base + path,
        data=None if data is None else json.dumps(data).encode(),
        headers={} if data is None else {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=90) as response:
        return json.load(response)


tracks = api("/api/youtube/search?q=" + urllib.parse.quote(query))
assert 0 < len(tracks) <= 12
track = tracks[0]
assert track["provider"] == "youtube" and track["id"].startswith("youtube:")
print("Found:", track["title"], "—", track["artist"], flush=True)
api("/api/player/play", {"id": track["id"]})
for _ in range(90):
    status = api("/api/status")
    if status["ready"] and status["track"]["id"] == track["id"]:
        break
    assert status["state"] != "error", status
    time.sleep(.5)
else:
    raise AssertionError("YouTube audio did not become ready")
with urllib.request.urlopen(base + "/stream.mp3", timeout=20) as response:
    sample = response.read(32768)
    assert len(sample) == 32768 and response.headers.get_content_type() == "audio/mpeg"
print("PASS: live catalog -> resolve -> Pi transcoding -> HTTP MP3;", track["id"])
