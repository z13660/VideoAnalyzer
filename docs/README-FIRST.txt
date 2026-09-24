VideoAnalyzer — Windows x64
===========================

1. Install the .NET 8 Desktop Runtime (once per machine):
   https://dotnet.microsoft.com/download/dotnet/8.0

2. Put ffmpeg and ffprobe next to the executable, in tools\ffmpeg\bin\
   (or anywhere on PATH, or point FFMPEG_HOME at them):
   https://www.gyan.dev/ffmpeg/builds/          -> ffmpeg-release-essentials.zip
   https://github.com/BtbN/FFmpeg-Builds/releases

   The folder should end up like this:

     VideoAnalyzer.exe
     tools\ffmpeg\bin\ffmpeg.exe
     tools\ffmpeg\bin\ffprobe.exe

3. Run VideoAnalyzer.exe. Ctrl+O opens a video; a file can also be dropped onto
   the window, or passed as an argument.

Source, documentation and the full README:
   https://github.com/z13660/VideoAnalyzer
