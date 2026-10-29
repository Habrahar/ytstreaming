"""Changes only playback on an isolated instance; measures native AAC and warm Next."""
import json
import sys
import time
import urllib.request
import urllib.parse

base=sys.argv[1].rstrip('/')
def api(path,body=None):
    request=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),
                                   headers={} if body is None else {'Content-Type':'application/json'})
    with urllib.request.urlopen(request,timeout=100) as response:return json.load(response)

def play(path,body):
    started=time.monotonic(); api(path,body)
    with urllib.request.urlopen(base+'/stream?requestId='+body['requestId'],timeout=100) as response:
        first=response.read1(4096)
        assert response.headers.get_content_type()=='audio/aac'
        assert first[0]==255 and first[1]&246==240
    elapsed=round((time.monotonic()-started)*1000)
    status=api('/api/status')
    assert status['outputMode']=='copy' and status['outputCodec']=='aac' and status['error'] is None
    print(json.dumps({'title':status['track']['title'],'firstPacketMs':elapsed,'packetBytes':len(first),'resolveMs':status['resolveMilliseconds'],
                      'startupMs':status['startupMilliseconds'],'strategy':status['sourceStrategy'],
                      'sourceBitrateKbps':status['sourceBitrateKbps'],'output':status['outputMode']}),flush=True)
    return status

library=api('/api/library')
observe=len(sys.argv)>2 and sys.argv[2]=='--observe'
if observe:
    status=api('/api/status')
    assert status['ready'] and status['outputCodec']=='aac'
else:
    seed=api('/api/youtube/search?q='+urllib.parse.quote(sys.argv[2]))[0] if len(sys.argv)>2 else library['tracks'][0]['track']
    status=play('/api/player/play',{'id':seed['id'],'requestId':'quality-cold'})
    preview=api('/api/player/neighbors?revision='+str(status['revision'])+'&version='+str(status['navigation']['version']))
    next_track=preview['next'][0]
    for _ in range(180):
        status=api('/api/status')
        if status['navigation']['preparation']['ready']==next_track['id']:break
        time.sleep(.5)
    else:raise AssertionError('Next was not prepared')
    status=play('/api/player/next',{'requestId':'quality-warm','expectedId':next_track['id'],'expectedRevision':status['revision']})
    assert status['track']['id']==next_track['id'] and status['resolveMilliseconds']<1000

gaps=[]; pending=bytearray(); decoded=0.0; playback_started=None; minimum_reserve=float('inf')
rates=(96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350)
with urllib.request.urlopen(base+'/stream?revision='+str(status['revision']),timeout=20) as response:
    started=previous=time.monotonic()
    while time.monotonic()-started<20:
        chunk=response.read1(4096); assert chunk
        now=time.monotonic(); gaps.append(now-previous); previous=now
        if playback_started is not None:
            # Check the reserve just before new data arrives, so a large packet
            # cannot conceal an underrun during the preceding network gap.
            minimum_reserve=min(minimum_reserve,decoded-(now-playback_started))
        pending.extend(chunk)
        while len(pending)>=7:
            assert pending[0]==255 and pending[1]&246==240
            size=((pending[3]&3)<<11)|(pending[4]<<3)|(pending[5]>>5)
            assert size>=7
            if len(pending)<size:break
            decoded+=1024*((pending[6]&3)+1)/rates[(pending[2]>>2)&15]
            del pending[:size]
        # Model a decoder that begins with one second available. A network gap
        # is harmless while already received AAC covers the playback clock.
        if playback_started is None and decoded>=1:playback_started=now
        if playback_started is not None:
            minimum_reserve=min(minimum_reserve,decoded-(now-playback_started))
assert playback_started is not None and minimum_reserve>0, (minimum_reserve,max(gaps))
joined=time.monotonic()
with urllib.request.urlopen(base+'/stream',timeout=10) as response:assert len(response.read(16384))==16384
join_ms=round((time.monotonic()-joined)*1000)
status=api('/api/status')
assert status['ready'] and status['slowClientDisconnects']==0 and status['bufferedSourceBytes']<=2*1024*1024+4096
assert api('/api/library')==library
print(json.dumps({'cadenceSeconds':round(sum(gaps),3),'largestGapSeconds':round(max(gaps),3),
                  'minimumDecodedReserveSeconds':round(minimum_reserve,3),
                  'join16384Ms':join_ms,'sourceBufferBytes':status['bufferedSourceBytes'],
                  'slowClientDisconnects':status['slowClientDisconnects'],'serverLargestGapMs':status['maxOutputGapMilliseconds']}),flush=True)
print('PASS: original AAC, continuous delivery, immediate rejoin and unchanged library'+('' if observe else ', cached Next'),flush=True)
