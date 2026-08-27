using System.IO.Compression;
using WindowsCompressor.Models;

namespace WindowsCompressor.Services;

public sealed record CompressionResult(string OutputPath, long OutputBytes)
{
    public long SavedBytes(long original) => original - OutputBytes;
}

public sealed class CompressionService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".ts", ".mts", ".m2ts" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".gif", ".avif", ".heic" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma" };

    private readonly FfmpegService _ffmpeg = new();

    public static string GetKind(string path)
    {
        var ext = Path.GetExtension(path);
        if (VideoExtensions.Contains(ext)) return "VIDEO";
        if (ImageExtensions.Contains(ext)) return "IMAGE";
        if (AudioExtensions.Contains(ext)) return "AUDIO";
        return "FILE";
    }

    public async Task<CompressionResult> CompressAsync(
        CompressionItem item,
        string outputDirectory,
        string quality,
        string requestedFormat,
        double? targetMegabytes,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        return item.Kind switch
        {
            "VIDEO" => await CompressVideoAsync(item, outputDirectory, quality, requestedFormat, targetMegabytes, progress, status, token),
            "IMAGE" => await CompressImageAsync(item, outputDirectory, quality, requestedFormat, targetMegabytes, progress, status, token),
            "AUDIO" => await CompressAudioAsync(item, outputDirectory, quality, requestedFormat, targetMegabytes, progress, status, token),
            _ => await CompressZipAsync(item, outputDirectory, targetMegabytes, progress, status, token)
        };
    }

    private async Task<CompressionResult> CompressVideoAsync(
        CompressionItem item, string output, string quality, string requestedFormat, double? targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat.Equals("WebM", StringComparison.OrdinalIgnoreCase) ? "WebM" : "MP4";
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", format == "WebM" ? ".webm" : ".mp4");
        var duration = await _ffmpeg.ProbeDurationAsync(item.Path, token);

        if (targetMegabytes is > 0)
            return await CompressVideoToTargetAsync(item, path, format, duration, targetMegabytes.Value, progress, status, token);

        var args = new List<string> { "-y", "-i", item.Path, "-map_metadata", "-1" };
        if (format == "WebM")
        {
            var crf = quality switch { "Smallest" => "38", "High quality" => "25", _ => "31" };
            args.AddRange(["-c:v", "libvpx-vp9", "-crf", crf, "-b:v", "0", "-c:a", "libopus", "-b:a", quality == "Smallest" ? "80k" : "128k"]);
        }
        else
        {
            var crf = quality switch { "Smallest" => "30", "High quality" => "19", _ => "24" };
            args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", crf, "-c:a", "aac", "-b:a", quality == "Smallest" ? "96k" : quality == "High quality" ? "192k" : "144k", "-movflags", "+faststart"]);
        }

        args.Add(path);
        await _ffmpeg.RunAsync(args, duration, progress, token);
        return new CompressionResult(path, new FileInfo(path).Length);
    }

    private async Task<CompressionResult> CompressVideoToTargetAsync(
        CompressionItem item, string path, string format, double duration, double targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        if (duration <= 0)
            throw new InvalidOperationException("Could not determine video duration for target-size compression.");

        var targetBytes = MegabytesToBytes(targetMegabytes);
        var totalKbps = targetBytes * 8d / duration / 1000d * 0.95;
        var audioKbps = totalKbps switch
        {
            < 130 => 32,
            < 220 => 48,
            < 360 => 64,
            < 700 => 96,
            _ => 128
        };
        var videoKbps = (int)Math.Floor(totalKbps - audioKbps);
        if (videoKbps < 80)
            throw new InvalidOperationException($"{targetMegabytes:0.##} MB is too small for this video's duration. Increase the target size.");

        status?.Report($"Fitting video to {targetMegabytes:0.##} MB…");
        await RunVideoTwoPassAsync(item.Path, path, format, duration, videoKbps, audioKbps, progress, status, token);

        var outputBytes = new FileInfo(path).Length;
        if (outputBytes > targetBytes)
        {
            var correction = targetBytes / (double)outputBytes * 0.94;
            videoKbps = Math.Max(80, (int)Math.Floor(videoKbps * correction));
            status?.Report("Tightening bitrate to stay under target…");
            File.Delete(path);
            await RunVideoTwoPassAsync(item.Path, path, format, duration, videoKbps, audioKbps, progress, status, token);
            outputBytes = new FileInfo(path).Length;
        }

        return new CompressionResult(path, outputBytes);
    }

    private async Task RunVideoTwoPassAsync(
        string inputPath, string outputPath, string format, double duration, int videoKbps, int audioKbps,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        var passRoot = Path.Combine(Path.GetTempPath(), "WindowsCompressor", "passes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(passRoot)!);
        var firstProgress = progress is null ? null : new Progress<double>(value => progress.Report(value * 0.48));
        var secondProgress = progress is null ? null : new Progress<double>(value => progress.Report(48 + value * 0.52));

        try
        {
            status?.Report($"Target pass 1/2 · video {videoKbps} kbps");
            var pass1 = new List<string> { "-y", "-i", inputPath, "-map_metadata", "-1" };
            if (format == "WebM")
                pass1.AddRange(["-c:v", "libvpx-vp9", "-b:v", $"{videoKbps}k", "-pass", "1", "-passlogfile", passRoot, "-an", "-f", "null", "NUL"]);
            else
                pass1.AddRange(["-c:v", "libx264", "-preset", "medium", "-b:v", $"{videoKbps}k", "-pass", "1", "-passlogfile", passRoot, "-an", "-f", "null", "NUL"]);
            await _ffmpeg.RunAsync(pass1, duration, firstProgress, token);

            status?.Report($"Target pass 2/2 · aiming under {CompressionItem.FormatBytes((long)(duration * (videoKbps + audioKbps) * 1000d / 8d))}");
            var pass2 = new List<string> { "-y", "-i", inputPath, "-map_metadata", "-1" };
            if (format == "WebM")
                pass2.AddRange(["-c:v", "libvpx-vp9", "-b:v", $"{videoKbps}k", "-pass", "2", "-passlogfile", passRoot, "-c:a", "libopus", "-b:a", $"{audioKbps}k"]);
            else
                pass2.AddRange(["-c:v", "libx264", "-preset", "medium", "-b:v", $"{videoKbps}k", "-pass", "2", "-passlogfile", passRoot, "-c:a", "aac", "-b:a", $"{audioKbps}k", "-movflags", "+faststart"]);
            pass2.Add(outputPath);
            await _ffmpeg.RunAsync(pass2, duration, secondProgress, token);
        }
        finally
        {
            try
            {
                var directory = Path.GetDirectoryName(passRoot)!;
                var prefix = Path.GetFileName(passRoot);
                foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
                    File.Delete(file);
            }
            catch { }
        }
    }

    private async Task<CompressionResult> CompressImageAsync(
        CompressionItem item, string output, string quality, string requestedFormat, double? targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat is "JPEG" or "PNG" or "WebP" ? requestedFormat : "WebP";
        var extension = format switch { "JPEG" => ".jpg", "PNG" => ".png", _ => ".webp" };
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", extension);

        if (targetMegabytes is > 0)
            return await CompressImageToTargetAsync(item.Path, path, format, targetMegabytes.Value, progress, status, token);

        var args = new List<string> { "-y", "-i", item.Path, "-map_metadata", "-1", "-frames:v", "1" };
        switch (format)
        {
            case "JPEG":
                args.AddRange(["-c:v", "mjpeg", "-q:v", quality == "Smallest" ? "8" : quality == "High quality" ? "2" : "4"]);
                break;
            case "PNG":
                args.AddRange(["-c:v", "png", "-compression_level", "9"]);
                break;
            default:
                args.AddRange(["-c:v", "libwebp", "-quality", quality == "Smallest" ? "58" : quality == "High quality" ? "88" : "74"]);
                break;
        }

        args.Add(path);
        await _ffmpeg.RunAsync(args, 0, progress, token);
        return new CompressionResult(path, new FileInfo(path).Length);
    }

    private async Task<CompressionResult> CompressImageToTargetAsync(
        string inputPath, string outputPath, string format, double targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        var targetBytes = MegabytesToBytes(targetMegabytes);
        var tempRoot = Path.Combine(Path.GetTempPath(), "WindowsCompressor", "images", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var extension = Path.GetExtension(outputPath);
        var candidate = Path.Combine(tempRoot, "candidate" + extension);
        var scales = new[] { 1d, .92, .84, .76, .68, .60, .52, .45, .38, .32, .27, .22, .18, .15 };

        try
        {
            for (var scaleIndex = 0; scaleIndex < scales.Length; scaleIndex++)
            {
                token.ThrowIfCancellationRequested();
                var scale = scales[scaleIndex];
                status?.Report($"Fitting image to {targetMegabytes:0.##} MB · {(int)Math.Round(scale * 100)}% dimensions");

                if (format == "PNG")
                {
                    await EncodeImageCandidateAsync(inputPath, candidate, format, 80, scale, token);
                    progress?.Report((scaleIndex + 1d) / scales.Length * 100d);
                    if (new FileInfo(candidate).Length <= targetBytes)
                    {
                        File.Copy(candidate, outputPath, true);
                        return new CompressionResult(outputPath, new FileInfo(outputPath).Length);
                    }
                    continue;
                }

                var low = 8;
                var high = 95;
                var bestQuality = -1;
                for (var attempt = 0; attempt < 7 && low <= high; attempt++)
                {
                    var q = (low + high) / 2;
                    await EncodeImageCandidateAsync(inputPath, candidate, format, q, scale, token);
                    var size = new FileInfo(candidate).Length;
                    if (size <= targetBytes)
                    {
                        bestQuality = q;
                        low = q + 1;
                    }
                    else
                    {
                        high = q - 1;
                    }
                    var completed = scaleIndex + (attempt + 1d) / 7d;
                    progress?.Report(Math.Min(99, completed / scales.Length * 100d));
                }

                if (bestQuality >= 0)
                {
                    await EncodeImageCandidateAsync(inputPath, outputPath, format, bestQuality, scale, token);
                    progress?.Report(100);
                    return new CompressionResult(outputPath, new FileInfo(outputPath).Length);
                }
            }

            throw new InvalidOperationException($"Could not reduce this image to {targetMegabytes:0.##} MB without extreme downscaling.");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private async Task EncodeImageCandidateAsync(
        string inputPath, string outputPath, string format, int quality, double scale, CancellationToken token)
    {
        var args = new List<string> { "-y", "-i", inputPath, "-map_metadata", "-1", "-frames:v", "1" };
        if (scale < .999)
        {
            var scaleText = scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            args.AddRange(["-vf", $"scale=max(2\\,trunc(iw*{scaleText}/2)*2):max(2\\,trunc(ih*{scaleText}/2)*2)"]);
        }

        switch (format)
        {
            case "JPEG":
                var qscale = Math.Clamp((int)Math.Round(31 - quality * 29d / 100d), 2, 31);
                args.AddRange(["-c:v", "mjpeg", "-q:v", qscale.ToString()]);
                break;
            case "PNG":
                args.AddRange(["-c:v", "png", "-compression_level", "9"]);
                break;
            default:
                args.AddRange(["-c:v", "libwebp", "-quality", quality.ToString()]);
                break;
        }

        args.Add(outputPath);
        await _ffmpeg.RunAsync(args, 0, null, token);
    }

    private async Task<CompressionResult> CompressAudioAsync(
        CompressionItem item, string output, string quality, string requestedFormat, double? targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat.Equals("AAC", StringComparison.OrdinalIgnoreCase) ? "AAC" : "MP3";
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", format == "AAC" ? ".m4a" : ".mp3");
        var duration = await _ffmpeg.ProbeDurationAsync(item.Path, token);

        string bitrate;
        if (targetMegabytes is > 0)
        {
            if (duration <= 0)
                throw new InvalidOperationException("Could not determine audio duration for target-size compression.");
            var targetBytes = MegabytesToBytes(targetMegabytes.Value);
            var calculatedKbps = targetBytes * 8d / duration / 1000d * 0.96;
            if (calculatedKbps < 24)
                throw new InvalidOperationException($"{targetMegabytes:0.##} MB is too small for this audio duration. Increase the target size.");
            var kbps = Math.Clamp((int)Math.Floor(calculatedKbps), 24, 320);
            bitrate = $"{kbps}k";
            status?.Report($"Target audio bitrate · {kbps} kbps");
        }
        else
        {
            bitrate = quality == "Smallest" ? "96k" : quality == "High quality" ? "256k" : "160k";
        }

        var args = new List<string> { "-y", "-i", item.Path, "-map_metadata", "-1", "-vn" };
        args.AddRange(format == "AAC" ? ["-c:a", "aac", "-b:a", bitrate] : ["-c:a", "libmp3lame", "-b:a", bitrate]);
        args.Add(path);
        await _ffmpeg.RunAsync(args, duration, progress, token);
        return new CompressionResult(path, new FileInfo(path).Length);
    }

    private static async Task<CompressionResult> CompressZipAsync(
        CompressionItem item, string output, double? targetMegabytes,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        if (targetMegabytes is > 0)
            status?.Report("ZIP is lossless; target size cannot be guaranteed. Using maximum ZIP compression.");

        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", ".zip");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(item.Path, Path.GetFileName(item.Path), CompressionLevel.SmallestSize);
            progress?.Report(100);
        }, token);
        return new CompressionResult(path, new FileInfo(path).Length);
    }

    private static long MegabytesToBytes(double megabytes) => (long)Math.Round(megabytes * 1024d * 1024d);

    private static string UniquePath(string directory, string stem, string extension)
    {
        var candidate = Path.Combine(directory, stem + extension);
        var index = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{stem}_{index++}{extension}");
        return candidate;
    }
}
