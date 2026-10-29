"""Disposable extraction process: cancelled selections also terminate their JS runtime."""
import json
import os
import re
import sys
import yt_dlp


class QuietLogger:
    def debug(self, *_): pass
    def warning(self, *_): pass
    def error(self, *_): pass


if __name__ == '__main__':
    video_id = sys.argv[1]
    if not re.fullmatch(r'[a-zA-Z0-9_-]{11}', video_id):
        raise SystemExit(2)
    options = {'format':'bestaudio[ext=m4a]/bestaudio', 'quiet':True, 'no_warnings':True,
               'logger':QuietLogger(), 'noplaylist':True, 'cachedir':'/tmp/yt-dlp-cache',
               'socket_timeout':12, 'retries':1, 'extractor_retries':1,
               'js_runtimes':{'node':{}}, 'proxy':os.environ.get('YOUTUBE_PROXY','').strip()}
    try:
        with yt_dlp.YoutubeDL(options) as downloader:
            info = downloader.extract_info('https://www.youtube.com/watch?v=' + video_id, download=False)
        print(json.dumps({'url':info.get('url',''), 'headers':info.get('http_headers',{}),
                          'unsupported':bool(info.get('is_live') or info.get('has_drm'))}))
    except Exception:
        raise SystemExit(1)
