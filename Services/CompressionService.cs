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
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        return item.Kind switch
        {
            "VIDEO" => await CompressVideoAsync(item, outputDirectory, quality, requestedFormat, progress, status, token),
            "IMAGE" => await CompressImageAsync(item, outputDirectory, quality, requestedFormat, progress, status, token),
            "AUDIO" => await CompressAudioAsync(item, outputDirectory, quality, requestedFormat, progress, status, token),
            _ => await CompressZipAsync(item, outputDirectory, progress, token)
        };
    }

    private async Task<CompressionResult> CompressVideoAsync(
        CompressionItem item, string output, string quality, string requestedFormat,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat.Equals("WebM", StringComparison.OrdinalIgnoreCase) ? "WebM" : "MP4";
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", format == "WebM" ? ".webm" : ".mp4");
        var duration = await _ffmpeg.ProbeDurationAsync(item.Path, token);
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

    private async Task<CompressionResult> CompressImageAsync(
        CompressionItem item, string output, string quality, string requestedFormat,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat is "JPEG" or "PNG" or "WebP" ? requestedFormat : "WebP";
        var extension = format switch { "JPEG" => ".jpg", "PNG" => ".png", _ => ".webp" };
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", extension);
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

    private async Task<CompressionResult> CompressAudioAsync(
        CompressionItem item, string output, string quality, string requestedFormat,
        IProgress<double>? progress, IProgress<string>? status, CancellationToken token)
    {
        await _ffmpeg.EnsureAvailableAsync(status, token);
        var format = requestedFormat.Equals("AAC", StringComparison.OrdinalIgnoreCase) ? "AAC" : "MP3";
        var path = UniquePath(output, Path.GetFileNameWithoutExtension(item.Path) + "_compressed", format == "AAC" ? ".m4a" : ".mp3");
        var duration = await _ffmpeg.ProbeDurationAsync(item.Path, token);
        var bitrate = quality == "Smallest" ? "96k" : quality == "High quality" ? "256k" : "160k";
        var args = new List<string> { "-y", "-i", item.Path, "-map_metadata", "-1", "-vn" };
        args.AddRange(format == "AAC" ? ["-c:a", "aac", "-b:a", bitrate] : ["-c:a", "libmp3lame", "-b:a", bitrate]);
        args.Add(path);
        await _ffmpeg.RunAsync(args, duration, progress, token);
        return new CompressionResult(path, new FileInfo(path).Length);
    }

    private static async Task<CompressionResult> CompressZipAsync(
        CompressionItem item, string output, IProgress<double>? progress, CancellationToken token)
    {
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

    private static string UniquePath(string directory, string stem, string extension)
    {
        var candidate = Path.Combine(directory, stem + extension);
        var index = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{stem}_{index++}{extension}");
        return candidate;
    }
}
