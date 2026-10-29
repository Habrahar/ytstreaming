import io
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
            radio.assert_called_once()
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
