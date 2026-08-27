using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;

namespace WindowsCompressor.Services;

public sealed class FfmpegService
{
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private static readonly HttpClient Http = CreateClient();

    private readonly string _toolDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsCompressor", "ffmpeg");

    public string FfmpegPath => Path.Combine(_toolDirectory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(_toolDirectory, "ffprobe.exe");

    public bool IsInstalled => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    public async Task EnsureAvailableAsync(IProgress<string>? status, CancellationToken token)
    {
        if (IsInstalled)
            return;

        Directory.CreateDirectory(_toolDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "WindowsCompressor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "ffmpeg.zip");
        var extractPath = Path.Combine(tempRoot, "extract");

        try
        {
            status?.Report("Downloading media engine…");
            using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                long copied = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (total is > 0)
                        status?.Report($"Downloading media engine… {copied * 100 / total.Value}%");
                }
            }

            status?.Report("Installing media engine…");
            ZipFile.ExtractToDirectory(zipPath, extractPath, true);

            var ffmpeg = Directory.EnumerateFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            var ffprobe = Directory.EnumerateFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ffmpeg is null || ffprobe is null)
                throw new InvalidOperationException("The downloaded FFmpeg package did not contain the required executables.");

            File.Copy(ffmpeg, FfmpegPath, true);
            File.Copy(ffprobe, FfprobePath, true);
            status?.Report("Media engine ready");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public async Task<double> ProbeDurationAsync(string inputPath, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of");
        psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(inputPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ffprobe.");
        var output = await process.StandardOutput.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0;
    }

    public async Task RunAsync(
        IReadOnlyList<string> arguments,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start FFmpeg.");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        var errorTask = process.StandardError.ReadToEndAsync(token);
        while (!process.StandardOutput.EndOfStream)
        {
            token.ThrowIfCancellationRequested();
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null) break;

            if (durationSeconds > 0 && (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms=")))
            {
                var valueText = line[(line.IndexOf('=') + 1)..];
                if (long.TryParse(valueText, out var microseconds))
                    progress?.Report(Math.Clamp(microseconds / (durationSeconds * 1_000_000d) * 100d, 0, 99));
            }
        }

        await process.WaitForExitAsync(token);
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"FFmpeg exited with code {process.ExitCode}." : error.Trim());

        progress?.Report(100);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsCompressor/0.2");
        return client;
    }
}
