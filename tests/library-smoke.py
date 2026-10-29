"""Run only against an isolated test instance: creates likes and a playlist.

python tests/library-smoke.py http://127.0.0.1:8091
# Restart that test instance, then:
python tests/library-smoke.py http://127.0.0.1:8091 --after-restart
"""
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

base = sys.argv[1].rstrip('/')
video_id = 'CBSCXIIOFSY'
track_id = 'youtube:' + video_id


def api(path, body=None, expected=200, origin=None):
    headers = {} if body is None else {'Content-Type': 'application/json'}
    if origin: headers['Origin'] = origin
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
    try: response = urllib.request.urlopen(request, timeout=90)
    except urllib.error.HTTPError as error: response = error
    with response:
        data = response.read()
        assert response.status == expected, (path, response.status, data[:200])
        return json.loads(data) if data else None


if '--after-restart' not in sys.argv:
    initial = api('/api/library')
    assert not initial['tracks'] and not initial['playlists'], 'Use a fresh, isolated library'
    api('/api/library/likes', {'id': 'youtube:unknown0000', 'liked': True}, expected=404)
    api('/api/library/playlists', {'name': 'x'}, expected=403, origin='https://example.invalid')
    api('/api/library/playlists', {'name': ' '}, expected=400)
    api('/api/library/likes', {'id': None, 'liked': True}, expected=404)
    tracks = api('/api/youtube/search?q=' + urllib.parse.quote('Scott Buckley Chasing Daylight'))
    assert any(t['id'] == track_id for t in tracks)
    liked = api('/api/library/likes', {'id': track_id, 'liked': True})
    duplicate = api('/api/library/likes', {'id': track_id, 'liked': True})
    assert duplicate['revision'] == liked['revision'] and duplicate['likedIds'] == [track_id]
    created = api('/api/library/playlists', {'name': 'В дорогу'})
    playlist_id = created['playlists'][0]['id']
    assert len(api('/api/library/playlists', {'name': 'в дорогу'})['playlists']) == 1
    path = '/api/library/playlists/' + playlist_id + '/tracks'
    added = api(path, {'id': track_id, 'added': True})
    assert api(path, {'id': track_id, 'added': True})['revision'] == added['revision']
    api('/api/library/likes', {'id': track_id, 'liked': False})
    retained = api('/api/library')
    assert not retained['likedIds'] and retained['tracks'][0]['track']['id'] == track_id
    api('/api/library/likes', {'id': track_id, 'liked': True})
    print('PASS: validation, Origin, likes/unlike, playlist creation/membership and deduplication', flush=True)
    print('Restart the isolated test container before --after-restart.', flush=True)
else:
    saved = api('/api/library')
    assert saved['likedIds'] == [track_id]
    assert saved['playlists'][0]['trackIds'] == [track_id]
    entry = saved['tracks'][0]
    assert entry['track']['title'] == 'Chasing Daylight'
    assert entry['url'] == 'https://music.youtube.com/watch?v=' + video_id
    # No search after restart: playback must resolve the persistent library entry.
    api('/api/player/play', {'id': track_id, 'requestId': 'library-restart-check'})
    for _ in range(100):
        status = api('/api/status')
        if status['ready'] and status['requestId'] == 'library-restart-check': break
        assert status['state'] != 'error', status
        time.sleep(.1)
    else: raise AssertionError('Audio not ready')
    with urllib.request.urlopen(base + '/stream?revision=' + str(status['revision']), timeout=20) as response:
        assert response.headers.get_content_type() in ('audio/mpeg','audio/aac') and len(response.read(4096)) == 4096
    with urllib.request.urlopen(base + '/api/artwork/' + video_id + '?size=small', timeout=30) as response:
        assert response.headers.get_content_type().startswith('image/') and len(response.read()) > 100
    print('PASS: likes, playlists, metadata and links survived restart; stored track plays without searching; artwork loads', flush=True)
