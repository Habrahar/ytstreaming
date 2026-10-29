"""Changes playback on an isolated instance; never modifies its library."""
import json
import sys
import time
import urllib.error
import urllib.request

base = sys.argv[1].rstrip('/')


def api(path, body=None, expected=200):
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(),
                                     headers={} if body is None else {'Content-Type': 'application/json'})
    try: response = urllib.request.urlopen(request, timeout=100)
    except urllib.error.HTTPError as error: response = error
    with response:
        content = response.read()
        assert response.status == expected, (path, response.status, content[:200])
        return json.loads(content) if content else None


def playable(path, body, track_id):
    api(path, body)
    for _ in range(160):
        status = api('/api/status')
        if status['ready'] and status['requestId'] == body['requestId']: break
        assert status['state'] != 'error', status
        time.sleep(.1)
    else: raise AssertionError('No audio')
    assert status['track']['id'] == track_id
    with urllib.request.urlopen(base + '/stream?revision=' + str(status['revision']), timeout=20) as response:
        assert response.headers.get_content_type() in ('audio/mpeg','audio/aac') and len(response.read(4096)) == 4096
    print('PASS: plays ' + status['track']['title'], flush=True)
    return status


def preview(status):
    revision=status['revision']
    for _ in range(100):
        if status.get('backgroundReady',True):break
        time.sleep(.1); status=api('/api/status')
        assert status['revision']==revision,'Track changed before its preview was ready'
    else:raise AssertionError('Startup grace never released the preview')
    return api('/api/player/neighbors?revision=' + str(status['revision']) + '&version=' + str(status['navigation']['version']))


before = api('/api/library')
seed = before['tracks'][0]['track']  # Existing isolated library fixture.
a = playable('/api/player/play', {'id': seed['id'], 'requestId': 'fan-seed'}, seed['id'])
first = preview(a)
assert not first['previous'] and len(first['next']) >= 2
assert preview(a) == first and api('/api/status')['revision'] == a['revision']
with urllib.request.urlopen(base + '/api/artwork/' + first['next'][0]['id'][8:] + '?size=card', timeout=30) as response:
    assert response.headers.get_content_type().startswith('image/') and len(response.read()) > 100
b_id, c_id = (t['id'] for t in first['next'][:2])
b = playable('/api/player/next', {'requestId': 'fan-next', 'expectedId': b_id, 'expectedRevision': a['revision']}, b_id)
second = preview(b)
assert second['previous'][0]['id'] == seed['id'] and second['next'][0]['id'] == c_id
back = playable('/api/player/previous', {'requestId': 'fan-back', 'expectedId': seed['id'], 'expectedRevision': b['revision']}, seed['id'])
forward = playable('/api/player/next', {'requestId': 'fan-forward', 'expectedId': b_id, 'expectedRevision': back['revision']}, b_id)
c = playable('/api/player/next', {'requestId': 'fan-second-next', 'expectedId': c_id, 'expectedRevision': forward['revision']}, c_id)
third = preview(c)
assert [t['id'] for t in third['previous']] == [b_id, seed['id']]
api('/api/player/next', {'requestId': 'fan-stale', 'expectedId': b_id, 'expectedRevision': a['revision']}, expected=409)
assert api('/api/status')['track']['id'] == c_id
assert api('/api/library') == before, 'Read-only playback test changed the library'
print('PASS: exact preview/Next order, two previous covers, stale-click rejection and unchanged library', flush=True)
