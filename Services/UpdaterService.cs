using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCompressor.Services;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl);

public sealed class UpdaterService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/ltseverydayyou/WindowsCompressor/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken token)
    {
        using var response = await Http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: token).ConfigureAwait(false);
        if (release is null || !TryParseVersion(release.TagName, out var latestVersion) || latestVersion <= CurrentVersion)
            return null;

        var asset = release.Assets.FirstOrDefault(x => x.Name.Equals("Compressor.exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            return null;

        return new UpdateInfo(latestVersion, release.TagName, asset.BrowserDownloadUrl);
    }

    public async Task DownloadAndRestartAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken token)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            throw new InvalidOperationException("Unable to locate the running Compressor executable.");

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsCompressor",
            "updates",
            update.Tag.TrimStart('v', 'V'));
        Directory.CreateDirectory(updateRoot);

        var downloadedExe = Path.Combine(updateRoot, "Compressor.new.exe");
        var updaterScript = Path.Combine(updateRoot, "apply-update.ps1");

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(downloadedExe, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);

            var buffer = new byte[1024 * 128];
            long copied = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                copied += read;
                if (totalBytes is > 0)
                    progress?.Report(Math.Clamp(copied * 100d / totalBytes.Value, 0, 100));
            }
        }

        if (new FileInfo(downloadedExe).Length < 1024 * 1024)
            throw new InvalidOperationException("The downloaded update is unexpectedly small.");

        var script = BuildUpdaterScript(Environment.ProcessId, downloadedExe, currentExe, updaterScript);
        await File.WriteAllTextAsync(updaterScript, script, token).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(updaterScript);

        _ = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start the updater.");
    }

    private static string BuildUpdaterScript(int processId, string source, string destination, string scriptPath)
    {
        static string Ps(string value) => "'" + value.Replace("'", "''") + "'";

        return $$"""
$ErrorActionPreference = 'Stop'
$pidToWait = {{processId}}
$source = {{Ps(source)}}
$destination = {{Ps(destination)}}
$scriptPath = {{Ps(scriptPath)}}
try {
    Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
    $updated = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $updated = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $updated) { exit 1 }
    Start-Process -FilePath $destination
}
finally {
    Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
}
""";
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var separator = text.IndexOfAny(['-', '+']);
        if (separator >= 0)
            text = text[..separator];

        return Version.TryParse(text, out version!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsCompressor-Updater/0.3.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
