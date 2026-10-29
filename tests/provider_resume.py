import io
import base64
import subprocess
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch
import requests
import server


class Response:
    def __init__(self, status, chunks, headers):
        self.status_code, self.chunks, self.headers = status, chunks, headers
    def __enter__(self): return self
    def __exit__(self, *_): pass
    def raise_for_status(self): pass
    def iter_content(self, _):
        for chunk in self.chunks:
            if isinstance(chunk, Exception): raise chunk
            yield chunk


class Handler:
    headers = {}
    def __init__(self): self.wfile, self.statuses = io.BytesIO(), []
    def send_response(self, code): self.statuses.append(code)
    def send_header(self, *_): pass
    def end_headers(self): pass


class ResumeCheck(unittest.TestCase):
    def test_cancellation_keeps_warm_supervisor_and_kills_only_job_group(self):
        with tempfile.TemporaryDirectory() as folder:
            marker=Path(folder)/'job-pids'
            helper=Path(folder)/'supervisor.py'
            helper.write_text('import extract,time,sys,os,subprocess\nfrom pathlib import Path\n'
                'def prime(self): self.visitor="fixture-context"; self.visitor_at=time.monotonic()\n'
                'def prepare(video_id,session):\n'
                ' if video_id=="AAAAAAAAAAA":\n'
                '  node=subprocess.Popen([sys.executable,"-c","import time; time.sleep(60)"])\n'
                f'  Path({str(marker)!r}).write_text(str(os.getpid())+" "+str(node.pid))\n'
                '  time.sleep(60)\n'
                ' return {"id":video_id,"warm":bool(session.visitor)}\n'
                'extract.ExtractionSession.prime=prime\nextract.prepare=prepare\nextract.run_worker()\n')
            try:
                with patch.object(server,'EXTRACTOR_PATH',helper):
                    with self.assertRaises(server.ResolveCancelled):
                        server.extract_source('AAAAAAAAAAA',lambda:marker.exists())
                    supervisor=server.extractor_worker
                    self.assertIsNotNone(supervisor); self.assertIsNone(supervisor.poll())
                    started=time.monotonic(); result=server.extract_source('BBBBBBBBBBB')
                    self.assertTrue(result['warm']); self.assertEqual(result['id'],'BBBBBBBBBBB')
                    self.assertIs(server.extractor_worker,supervisor)
                    self.assertLess(time.monotonic()-started,1.5)
                    for pid in map(int,marker.read_text().split()):
                        stat=Path(f'/proc/{pid}/stat')
                        until=time.monotonic()+2
                        while stat.exists() and stat.read_text().split()[2]!='Z' and time.monotonic()<until:time.sleep(.05)
                        self.assertTrue(not stat.exists() or stat.read_text().split()[2]=='Z','Cancelled descendant is still running')
            finally:server.stop_extractor()

    def test_worker_protocol_accepts_larger_prefix_response(self):
        with tempfile.TemporaryDirectory() as folder:
            helper=Path(folder)/'large-worker.py'
            helper.write_text('import json,sys\nprint(json.dumps({"ready":True}),flush=True)\nfor line in sys.stdin: print(json.dumps({"result":{"prefix":"x"*350000}}),flush=True)\n')
            try:
                with patch.object(server,'EXTRACTOR_PATH',helper):
                    result=server.extract_source('AAAAAAAAAAA')
                    self.assertEqual(len(result['prefix']),350000)
            finally:server.stop_extractor()

    def test_larger_prefix_cache_is_bounded_and_keeps_exact_resume_position(self):
        data=b'x'*262144
        info={'url':'https://media.googlevideo.com/audio','headers':{},'prefix':base64.b64encode(data).decode(),
              'totalBytes':300000,'mimeType':'audio/mp4','codec':'mp4a.40.2'}
        with patch.dict(server.audio_cache,{},clear=True), patch.dict(server.prefix_cache,{},clear=True), \
             patch.object(server,'extract_source',return_value=info):
            for n in range(9):server.resolve(str(n)*11)
            self.assertEqual(len(server.prefix_cache),8)
            self.assertEqual(sum(len(p[1]) for p in server.prefix_cache.values()),2*1024*1024)
            self.assertEqual(server.prefix_cache['88888888888'][1],data)
            info['prefix']=base64.b64encode(data+b'x').decode()
            server.resolve('AAAAAAAAAAA')
            self.assertNotIn('AAAAAAAAAAA',server.prefix_cache)

    def test_buffered_prefix_is_sent_before_upstream_and_not_repeated(self):
        handler=Handler(); ranges=[]
        class Session:
            def __enter__(self): return self
            def __exit__(self,*_): pass
            def get(self,_,headers,stream):
                self_before=handler.wfile.getvalue()
                if self_before!=b'abc': raise AssertionError('Prefix was not sent first')
                ranges.append(headers.get('Range'))
                return Response(206,[b'def'],{'Content-Range':'bytes 3-5/6','Content-Length':'3'})
        with patch.object(server,'NetworkSession',Session):
            server.forward_audio(handler,'unused',{},(b'abc','audio/mp4',6))
        self.assertEqual(handler.wfile.getvalue(),b'abcdef'); self.assertEqual(ranges,['bytes=3-'])
        self.assertEqual(handler.statuses,[200])

    def test_range_entirely_in_prefix_needs_no_network(self):
        handler=Handler(); handler.headers={'Range':'bytes=1-2'}
        with patch.object(server,'NetworkSession') as network:
            server.forward_audio(handler,'unused',{},(b'abcdef','audio/mp4',100))
            network.assert_not_called()
        self.assertEqual(handler.wfile.getvalue(),b'bc'); self.assertEqual(handler.statuses,[206])

    def test_worker_is_reused_between_successful_requests(self):
        original=server.EXTRACTOR_PATH
        with tempfile.TemporaryDirectory() as folder:
            helper=Path(folder)/'worker.py'
            helper.write_text('import json,sys\nprint(json.dumps({"ready":True}),flush=True)\nfor line in sys.stdin: print(json.dumps({"result":{"id":line.strip()}}),flush=True)\n')
            try:
                with patch.object(server,'EXTRACTOR_PATH',helper):
                    self.assertEqual(server.extract_source('AAAAAAAAAAA')['id'],'AAAAAAAAAAA')
                    process=server.extractor_worker
                    self.assertEqual(server.extract_source('BBBBBBBBBBB')['id'],'BBBBBBBBBBB')
                    self.assertIs(server.extractor_worker,process)
            finally: server.stop_extractor()

    def test_source_cache_preserves_quality_metadata(self):
        info={'url':'https://media.googlevideo.com/audio','headers':{},'codec':'mp4a.40.2',
              'bitrateKbps':129.5,'sampleRate':44100,'channels':2,'strategy':'visionos',
              'extractMilliseconds':200,'probeMilliseconds':100}
        with patch.dict(server.audio_cache,{},clear=True), patch.object(server,'extract_source',return_value=info) as extract:
            first=server.resolve('AAAAAAAAAAA'); cached=server.resolve('AAAAAAAAAAA')
            self.assertEqual(first['codec'],'mp4a.40.2')
            self.assertEqual(cached['codec'],first['codec']); self.assertEqual(cached['bitrateKbps'],129.5)
            self.assertEqual(cached['strategy'],'cache'); extract.assert_called_once()

    def test_carousel_card_has_separate_cache_and_does_not_fetch_radio(self):
        urls=[]
        class Session:
            def __enter__(self): return self
            def __exit__(self,*_): pass
            def get(self,url,**kwargs):
                urls.append(url)
                return Response(200,[b'image'],{'Content-Type':'image/jpeg'})
        with patch.dict(server.track_cache,{},clear=True), patch.dict(server.artwork_cache,{},clear=True), \
             patch.object(server,'NetworkSession',Session), patch.object(server,'radio') as radio:
            url='https://yt3.googleusercontent.com/card=w544-h544-l90-rj'
            server.artwork('AAAAAAAAAAA',True,url,320)
            server.artwork('AAAAAAAAAAA',True,url,96)
            self.assertIn('=w320-h320',urls[0]); self.assertIn('=w96-h96',urls[1])
            self.assertEqual(len(server.artwork_cache),2); radio.assert_not_called()

    def test_saved_preview_uses_validated_thumbnail_after_catalog_reset(self):
        urls=[]
        class Session:
            def __enter__(self): return self
            def __exit__(self,*_): pass
            def get(self,url,**kwargs):
                urls.append(url)
                return Response(200,[b'image'],{'Content-Type':'image/jpeg'})
        with patch.dict(server.track_cache,{},clear=True), patch.dict(server.artwork_cache,{},clear=True), \
             patch.object(server,'NetworkSession',Session), patch.object(server,'radio') as radio:
            server.artwork('AAAAAAAAAAA',preview=True,thumbnail='https://yt3.googleusercontent.com/saved=w120-h120-l90-rj')
            self.assertIn('=w96-h96',urls[0]); radio.assert_not_called()
        with patch.dict(server.track_cache,{},clear=True), patch.dict(server.artwork_cache,{},clear=True), \
             patch.object(server,'NetworkSession') as network:
            with self.assertRaises(ValueError): server.artwork('AAAAAAAAAAA',preview=True,thumbnail='https://127.0.0.1/private')
            network.assert_not_called()

    def test_search_preview_does_not_fetch_radio_or_share_fullsize_cache(self):
        fetched=[]
        class Session:
            def __enter__(self): return self
            def __exit__(self,*_): pass
            def get(self,url,**kwargs):
                fetched.append(url)
                return Response(200,[b'image'],{'Content-Type':'image/jpeg'})
        with patch.dict(server.track_cache, {'AAAAAAAAAAA':{'thumbnail':'https://yt3.googleusercontent.com/art=w544-h544-l90-rj'}},clear=True), \
             patch.dict(server.artwork_cache,{},clear=True),patch.object(server,'radio',return_value=[]) as radio, \
             patch.object(server,'NetworkSession',Session):
            self.assertEqual(server.artwork('AAAAAAAAAAA',preview=True),(b'image','image/jpeg'))
            radio.assert_not_called()
            self.assertIn('=w96-h96',fetched[0])
            server.artwork('AAAAAAAAAAA')
            radio.assert_not_called()
            self.assertEqual(len(fetched),2)
            self.assertIn('=w544-h544',fetched[1])

    def test_radio_metadata_uses_largest_artwork_and_deduplicates(self):
        tracks = server.normalize_tracks([{'videoId':'AAAAAAAAAAA','title':'Song','artists':None,
            'length':'3:10','thumbnail':[{'url':'https://i.ytimg.com/small.jpg','width':120},
                                      {'url':'https://i.ytimg.com/large.jpg','width':544}]},
            {'videoId':'AAAAAAAAAAA','title':'Duplicate'}, {'videoId':'invalid'}])
        self.assertEqual(len(tracks), 1)
        self.assertEqual(tracks[0]['thumbnail'], 'https://i.ytimg.com/large.jpg')
        self.assertEqual(tracks[0]['duration'], '3:10')

    def test_artwork_rejects_unknown_hosts_without_fetching(self):
        with patch.dict(server.track_cache, {'AAAAAAAAAAA':{'thumbnail':'https://127.0.0.1/private'}}, clear=True), \
             patch.object(server, 'radio', return_value=[]), patch.object(server, 'NetworkSession') as network:
            with self.assertRaises(ValueError): server.artwork('AAAAAAAAAAA')
            network.assert_not_called()

    def run_case(self, resume):
        responses = iter([Response(200, [b'abc', requests.ConnectionError('drop')], {'Content-Length':'6'}), resume])
        ranges = []
        class Session:
            def __enter__(self): return self
            def __exit__(self, *_): pass
            def get(self, _, headers, stream):
                ranges.append(headers.get('Range'))
                return next(responses)
        handler = Handler()
        with patch.object(server, 'NetworkSession', Session), patch.object(server.time, 'sleep'):
            try: server.forward_audio(handler, 'unused', {})
            except ValueError: pass
        return handler, ranges

    def test_resume_without_repeated_audio(self):
        handler, ranges = self.run_case(Response(206, [b'def'], {'Content-Range':'bytes 3-5/6'}))
        self.assertEqual(handler.wfile.getvalue(), b'abcdef')
        self.assertEqual(ranges, [None, 'bytes=3-'])
        self.assertEqual(handler.statuses, [200])

    def test_reject_upstream_that_restarts_from_beginning(self):
        handler, _ = self.run_case(Response(200, [b'abcdef'], {'Content-Length':'6'}))
        self.assertEqual(handler.wfile.getvalue(), b'abc')

    def test_cancel_terminates_extraction_process(self):
        created = []
        original = subprocess.Popen
        def start(*args, **kwargs):
            process = original(*args, **kwargs)
            created.append(process)
            return process
        with tempfile.TemporaryDirectory() as folder:
            helper = Path(folder) / 'slow.py'
            helper.write_text('import time; time.sleep(60)')
            started = time.monotonic()
            with patch.object(server, 'EXTRACTOR_PATH', helper), patch.object(server.subprocess, 'Popen', start):
                with self.assertRaises(server.ResolveCancelled):
                    server.extract_source('unused', lambda: time.monotonic() - started > .3)
            self.assertLess(time.monotonic() - started, 3)
            self.assertIsNotNone(created[0].poll())


if __name__ == '__main__': unittest.main()
