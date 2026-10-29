import unittest
from unittest.mock import patch
import extract


class Downloader:
    def add_info_extractor(self, _): pass
    def __enter__(self): return self
    def __exit__(self, *_): pass
    def extract_info(self, *args, **kwargs):
        return {'url':'https://media.googlevideo.com/audio','http_headers':{}}


class ExtractionChecks(unittest.TestCase):
    def test_fast_client_needs_no_javascript_runtime(self):
        options = []
        def create(value, **kwargs): options.append(value); return Downloader()
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
        def create(value, **kwargs): options.append(value); return Downloader()
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


if __name__=='__main__': unittest.main()
