#!/usr/bin/env python3
"""Offline regression checks using yt-dlp's real selector and production constants.

Install yt-dlp in the test Python environment, then run this script. No video
requests or downloads are made; socket connections are explicitly blocked.
"""

import copy
from pathlib import Path
import re
import socket
from unittest.mock import patch

from yt_dlp import YoutubeDL
from yt_dlp.utils import DownloadError, ExtractorError
from yt_dlp.version import __version__


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "src/Jellyfin.Plugin.TrailerReel/Services/YtDlpDownloader.cs"
CONSTANTS = dict(re.findall(r'const string (\w+) = "([^"]+)";', SOURCE.read_text()))
STRICT = CONSTANTS["FullHdFormatSelector"]
RELAXED = CONSTANTS["UpToFullHdFormatSelector"]


class QuietLogger:
    def debug(self, message):
        pass

    def warning(self, message):
        pass

    def error(self, message):
        pass


def video(key, width, height, *, muxed=False, codec="avc1.640028"):
    return {
        "format_id": key,
        "url": f"https://example.invalid/{key}.mp4",
        "ext": "mp4",
        "width": width,
        "height": height,
        "vcodec": codec,
        "acodec": "mp4a.40.2" if muxed else "none",
        "tbr": 2500,
        "fps": 24,
    }


AUDIO = {
    "format_id": "140", "url": "https://example.invalid/audio.m4a",
    "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 128,
}


def select(formats, selector=STRICT):
    with YoutubeDL({
        "format": selector, "quiet": True, "no_warnings": True,
        "skip_download": True, "check_formats": False, "logger": QuietLogger(),
    }) as ydl:
        try:
            result = ydl.process_ie_result({
                "id": "fixture", "title": "Offline trailer fixture",
                "extractor": "generic", "extractor_key": "Generic",
                "webpage_url": "https://example.invalid/fixture",
                "formats": copy.deepcopy(formats),
            }, download=False)
        except (DownloadError, ExtractorError) as error:
            if "Requested format is not available" not in str(error):
                raise
            return None
    streams = result.get("requested_formats", [result])
    picture = next(stream for stream in streams if stream.get("vcodec") != "none")
    assert any(stream.get("acodec") != "none" for stream in streams), "Audio missing"
    return picture["width"], picture["height"]


checks = 0


def check(name, actual, expected):
    global checks
    checks += 1
    assert actual == expected, f"{name}: expected {expected!r}, got {actual!r}"


with patch.object(socket.socket, "connect", side_effect=AssertionError("Offline test attempted a network connection")):
    for width, height in [(1920, 1080), (1920, 960), (1920, 800), (1440, 1080)]:
        for muxed in (False, True):
            formats = [video("video", width, height, muxed=muxed)]
            if not muxed:
                formats.append(AUDIO)
            check(f"Full HD {width}x{height}, muxed={muxed}", select(formats), (width, height))

    for width, height in [(1280, 720), (1280, 640), (2560, 1280), (3840, 1920),
                          (3840, 1080), (1080, 1920), (None, 1080), (1920, None)]:
        check(f"Reject {width}x{height}", select([video("video", width, height), AUDIO]), None)

    supplied_listing = [
        video("136", 1280, 640), video("137", 1920, 960),
        video("248", 1920, 960, codec="vp9"),
        video("399", 1920, 960, codec="av01.0.08M.08"),
        video("271", 2560, 1280, codec="vp9"),
        video("313", 3840, 1920, codec="vp9"), AUDIO,
    ]
    check("Original selector reproduces the reported failure",
          select(supplied_listing, "bv*[height=1080]+ba/b[height=1080]"), None)
    check("Corrected selector accepts the supplied 1080p tier", select(supplied_listing), (1920, 960))
    check("Full HD selector requires audio", select([video("silent", 1920, 960)]), None)
    check("Audio-only listing cannot become a trailer", select([AUDIO]), None)
    check("Relaxed selector remains bounded to Full HD", select(supplied_listing, RELAXED), (1920, 960))
    check("Relaxed selector can still opt into lower quality",
          select([video("720", 1280, 640), AUDIO], RELAXED), (1280, 640))

print(f"All {checks} offline format-selection checks passed with yt-dlp {__version__}.")
