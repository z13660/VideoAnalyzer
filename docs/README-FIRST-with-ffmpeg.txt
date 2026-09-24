VideoAnalyzer — Windows x64, with ffmpeg
========================================

Nothing to install: this build carries its own .NET runtime and its own ffmpeg.

1. Unpack the zip anywhere.
2. Run VideoAnalyzer.exe. Ctrl+O opens a video; a file can also be dropped onto the
   window, or passed as an argument.

ffmpeg and ffprobe are in tools\ffmpeg\bin\ and are found automatically. To use a
different build instead, put it on PATH or point FFMPEG_HOME at it.

The graphs need a moment of decoding before they fill in — the transport opens
about 0.4 s after a file is picked, and QP and motion arrive while it plays.

Source, documentation and the full README:
   https://github.com/z13660/VideoAnalyzer
