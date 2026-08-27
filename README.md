# WindowsCompressor

A compact Windows desktop compressor for videos, images, audio and regular files.

## UI

The interface uses a midnight-purple palette with square corners and a custom rectangular title bar. It is designed around a queue instead of oversized cards or rounded mobile-style controls.

## Features

- Drag and drop files or entire folders
- Batch queue
- Smallest, Balanced and High quality presets
- Video: MP4 (H.264/AAC) or WebM (VP9/Opus)
- Image: WebP, JPEG or PNG
- Audio: MP3 or AAC
- Other files: ZIP
- Original files are never overwritten
- Automatic FFmpeg/ffprobe download on first media compression
- Per-file and overall progress
- Saved-space reporting
- Cancellation
- Self-contained win-x64 single-file EXE

## Build

```powershell
dotnet publish WindowsCompressor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

The GitHub Actions workflow also builds `Compressor.exe` and can publish it to Releases.
