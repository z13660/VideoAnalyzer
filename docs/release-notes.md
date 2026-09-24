**A frame-level video stream analyser for Windows** — per-frame bitrate, picture type, QP, motion,
GOP position and reorder distance plotted as a stack of synchronised graphs under one pinned
playhead, with motion-vector, block-noise and macroblock-grid overlays over the picture.

### Running it

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (once).
2. Unpack this zip and put `ffmpeg.exe` + `ffprobe.exe` into `tools\ffmpeg\bin\` next to
   `VideoAnalyzer.exe` — [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) or
   [BtbN builds](https://github.com/BtbN/FFmpeg-Builds/releases).
3. Run `VideoAnalyzer.exe`. `Ctrl+O` opens a video; a file can also be dropped onto the window.

`README-FIRST.txt` inside the zip has the same steps with the folder layout.

### What is in it

* Six synchronised graphs — bitrate, picture type, QP, motion, GOP, reorder — sharing one
  timeline and one playhead, which is pinned to the centre so the values under it stay put
  while the data travels.
* Frame-accurate playback and stepping, decoded through an ffmpeg `rawvideo` pipe.
* Motion vectors, block-noise dots, and a macroblock grid drawn at the real 16×16 block pitch
  with the blocks whose edges stand out washed amber.
* Progressive analysis: the transport opens about 0.4 s after a file is picked, and the graphs
  fill in while the QP and motion passes run side by side.
* A settable analysis range — mark it on the ruler or type the two times in — so a long
  recording can be examined a minute at a time, with the unmeasured stretches shaded.
* Analysis that can be stopped part way, keeping everything it had already produced.

No third-party dependencies: .NET 8 + WPF throughout, ffmpeg for every probe, decode and
analysis pass.

**Full documentation:** https://github.com/z13660/VideoAnalyzer
