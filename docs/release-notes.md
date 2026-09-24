**A frame-level video stream analyser for Windows** — per-frame bitrate, picture type, QP, motion,
GOP position and reorder distance plotted as a stack of synchronised graphs under one pinned
playhead, with motion-vector, block-noise and macroblock-grid overlays over the picture.

---

## What changed in 1.0.1

**Analysing part of a clip.** The deep passes take a range: right-click the timeline twice, or
type the two times into 分析范围…, and only that stretch is decoded — which is what makes a two
hour recording workable. Ranges accumulate: analysing 5–15 s and then 18–28 s keeps both, and the
time between them is shaded, so an unmeasured stretch is never painted as measured.

**Analysis you can stop.** 取消分析 ends the deep passes and keeps everything they had already
produced — the frame table, bitrate, GOP and reorder data, plus whatever QP and motion rows were
filled in. What is already on screen stays.

**Files reopen without being decoded again.** The analysis of the most recently opened files is
held in memory, so switching back to one shows its graphs immediately. Nothing is written to
disk.

**The decoders are cleaned up.** They are now registered and taken down explicitly when a file is
opened or the window is closed. Previously they outlived the work they were started for:
cancelling a token only stops a read loop, and an ffmpeg child does not die with its parent.

**Smaller things.** The macroblock grid draws at the real 16×16 block pitch and washes the blocks
whose edges stand out; the motion row draws bars instead of hairlines; the axis labels are
readable at 125% display scaling; the mute glyph matches the sound state; the info panel's
container line no longer prints the demuxer's alias list; full screen, the drag/click split and
playback resuming after a seek were all fixed.

---

## Downloads

| | |
|---|---|
| `VideoAnalyzer-1.0.1-win-x64.zip` | the program on its own — needs the .NET 8 desktop runtime and a copy of ffmpeg |
| `VideoAnalyzer-1.0.1-win-x64-with-ffmpeg.zip` | self contained, ffmpeg included: unpack it and run |

### Running the plain build

1. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (once).
2. Put `ffmpeg.exe` and `ffprobe.exe` into `tools\ffmpeg\bin\` next to `VideoAnalyzer.exe` —
   [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) or
   [BtbN builds](https://github.com/BtbN/FFmpeg-Builds/releases).
3. Run `VideoAnalyzer.exe`. `Ctrl+O` opens a video; a file can also be dropped onto the window.

The self-contained build needs none of that: unpack it and run.

No third-party dependencies: .NET 8 + WPF throughout, ffmpeg for every probe, decode and
analysis pass.

**Full documentation:** https://github.com/z13660/VideoAnalyzer
