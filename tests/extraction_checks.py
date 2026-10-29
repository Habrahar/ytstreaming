import unittest
import copy
import time
import base64
from unittest.mock import patch
import extract


class Downloader:
    def __init__(self, options=None): self.params=options or {}; self.calls=[]; self.closed=False
    def close(self): self.closed=True
    def add_info_extractor(self, _): pass
    def __enter__(self): return self
    def __exit__(self, *_): pass
    def extract_info(self, *args, **kwargs):
        self.calls.append(copy.deepcopy(self.params))
        return {'url':'https://media.googlevideo.com/audio','http_headers':{}}


class ExtractionChecks(unittest.TestCase):
    def test_validation_keeps_only_bounded_audio_prefix_even_if_range_is_ignored(self):
        with patch.object(extract.requests,'Session') as network:
            response=network.return_value.get.return_value.__enter__.return_value
            response.status_code=200
            response.headers={'Content-Length':'1000000','Content-Type':'audio/mp4'}
            response.iter_content.return_value=iter([b'x'*131072]*3)
            result=extract.validate_audio({'url':'https://media.googlevideo.com/audio','acodec':'mp4a.40.2'},'')
            self.assertEqual(len(base64.b64decode(result['prefix'])),262144)
            self.assertEqual(result['totalBytes'],1000000)
            self.assertEqual(network.return_value.get.call_args.kwargs['headers']['Range'],'bytes=0-262143')
            network.return_value.close.assert_called_once()

    def test_fast_client_needs_no_javascript_runtime(self):
        options = []
        def create(value, **kwargs): options.append(value); return Downloader(value)
        with patch.object(extract.yt_dlp,'YoutubeDL',side_effect=create), \
             patch.object(extract,'validate_audio',return_value={'url':'valid','headers':{},'unsupported':False}) as validate:
            result = extract.prepare('AAAAAAAAAAA')
        self.assertEqual(result['strategy'],'visionos')
        self.assertEqual(len(options),1)
        self.assertEqual(options[0]['extractor_args']['youtube']['player_client'],['visionos'])
        self.assertNotIn('js_runtimes', options[0])
        self.assertEqual(options[0]['extractor_args']['youtube']['skip'],['hls','dash'])
        validate.assert_called_once()

    def test_unplayable_fast_url_falls_back_to_validated_standard(self):
        options = []
        def create(value, **kwargs): options.append(value); return Downloader(value)
        with patch.object(extract.yt_dlp,'YoutubeDL',side_effect=create), \
             patch.object(extract,'validate_audio',side_effect=[ValueError('403'),{'url':'valid','headers':{},'unsupported':False}]) as validate:
            result=extract.prepare('AAAAAAAAAAA')
        self.assertEqual(result['strategy'],'standard')
        self.assertEqual(len(options),2)
        self.assertEqual(options[1]['js_runtimes'],{'node':{}})
        self.assertEqual(validate.call_count,2)

    def test_validation_rejects_unexpected_media_hosts(self):
        with patch.object(extract.requests,'Session') as network:
            with self.assertRaises(ValueError): extract.validate_audio({'url':'https://127.0.0.1/private'},'')
            network.assert_not_called()

    def test_session_reuses_downloader_and_probe_connection_without_changing_format(self):
        downloader=Downloader(); state=extract.ExtractionSession('')
        state.visitor='opaque-anonymous-context'; state.visitor_at=time.monotonic()
        with patch.object(extract.yt_dlp,'YoutubeDL',return_value=downloader) as create, \
             patch.object(extract,'validate_audio',return_value={'url':'valid','codec':'mp4a.40.2','bitrateKbps':129.5}) as validate, \
             patch.object(extract.requests,'Session') as network:
            first=extract.prepare('AAAAAAAAAAA',state); second=extract.prepare('BBBBBBBBBBB',state)
            self.assertEqual(first['strategy'],'visionos-session'); self.assertEqual(second['strategy'],first['strategy'])
            create.assert_called_once(); network.assert_called_once()
            self.assertEqual(create.call_args.args[0]['format'],'bestaudio[ext=m4a]/bestaudio')
            self.assertEqual(downloader.calls[0]['extractor_args']['youtube']['player_skip'],['webpage','configs','js','initial_data'])
            self.assertEqual(validate.call_args_list[0].args[2],validate.call_args_list[1].args[2])
            self.assertNotIn('visitor',first)
            state.close(); self.assertTrue(downloader.closed); network.return_value.close.assert_called_once()

    def test_rejected_session_retries_full_visionos_and_discards_context(self):
        downloader=Downloader(); state=extract.ExtractionSession('')
        state.visitor='stale-context'; state.visitor_at=time.monotonic()
        with patch.object(extract.yt_dlp,'YoutubeDL',return_value=downloader), \
             patch.object(extract,'validate_audio',side_effect=[ValueError('403'),{'url':'valid'}]):
            result=extract.prepare('AAAAAAAAAAA',state)
        self.assertEqual(result['strategy'],'visionos'); self.assertIsNone(state.visitor)
        self.assertEqual(len(downloader.calls),2)
        self.assertIn('player_skip',downloader.calls[0]['extractor_args']['youtube'])
        self.assertNotIn('player_skip',downloader.calls[1]['extractor_args']['youtube'])
        state.close()

    def test_expired_context_uses_full_extraction(self):
        downloader=Downloader(); state=extract.ExtractionSession('')
        state.visitor='expired-context'; state.visitor_at=time.monotonic()-901
        with patch.object(extract.yt_dlp,'YoutubeDL',return_value=downloader), \
             patch.object(extract,'validate_audio',return_value={'url':'valid'}):
            self.assertEqual(extract.prepare('AAAAAAAAAAA',state)['strategy'],'visionos')
        self.assertNotIn('visitor_data',downloader.calls[0]['extractor_args']['youtube'])
        state.close()

    def test_failed_bootstrap_keeps_full_fallback_available(self):
        state=extract.ExtractionSession(''); state.visitor='old-context'
        with patch.object(extract.yt_dlp,'YoutubeDL',return_value=Downloader()):state.prime()
        self.assertIsNone(state.visitor); state.close()

    def test_idle_context_refresh_has_no_work_for_a_fresh_session(self):
        state=extract.ExtractionSession(''); state.visitor='anonymous-context'; state.visitor_at=time.monotonic()
        with patch.object(state,'prime') as prime:
            state.refresh_if_due(); prime.assert_not_called()
            state.visitor_at-=601; state.refresh_if_due(); prime.assert_called_once()

    def test_failed_refresh_preserves_a_still_valid_context(self):
        state=extract.ExtractionSession(''); state.visitor='still-valid-context'; state.visitor_at=time.monotonic()-601
        with patch.object(state,'fast',side_effect=RuntimeError('Unavailable')):state.prime()
        self.assertEqual(state.visitor,'still-valid-context')

    def test_refresh_clears_quick_flags_before_loading_configuration(self):
        state=extract.ExtractionSession(''); downloader=Downloader({'extractor_args':{'youtube':{'player_skip':['configs'],'visitor_data':['old']}}})
        ie=extract.SessionYoutubeIE(state)
        downloader.get_info_extractor=lambda _:ie
        with patch.object(state,'fast',return_value=downloader), patch.object(ie,'initialize'), \
             patch.object(ie,'_download_ytcfg',return_value={}), \
             patch.object(extract.YoutubeIE,'_extract_visitor_data',return_value='new-anonymous-context'):
            state.prime()
        self.assertEqual(state.visitor,'new-anonymous-context')
        self.assertNotIn('player_skip',downloader.params['extractor_args']['youtube'])
        self.assertNotIn('visitor_data',downloader.params['extractor_args']['youtube'])


if __name__=='__main__': unittest.main()
