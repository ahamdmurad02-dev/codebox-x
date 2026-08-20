using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeBoxX.Services;

public sealed class UpdateService : IDisposable
{
    public const string RepositoryOwner = "ahamdmurad02-dev";
    public const string RepositoryName = "codebox-x";
    public const string InstallerAssetName = "CodeBoxX-Setup-win-x64.exe";
    public const string LatestReleaseEndpoint = "https://api.github.com/repos/ahamdmurad02-dev/codebox-x/releases/latest";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public UpdateService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any()) _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBoxX-Updater/1.2.2");
        if (!_httpClient.DefaultRequestHeaders.Accept.Any()) _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(Version installedVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UpdateCheckResult.Failed("No published CodeBox X release was found in the official GitHub repository.");
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub could not check for updates (HTTP {(int)response.StatusCode}). Try again later.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
            if (release is null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
                return UpdateCheckResult.Failed("GitHub returned an invalid release record. No update was downloaded.");

            if (!TryParseReleaseVersion(release.TagName, out var latestVersion))
                return UpdateCheckResult.Failed($"The official release tag '{release.TagName}' is not a supported CodeBox X version.");

            var releaseNotes = release.Body?.Trim() ?? "No release notes were supplied.";
            if (latestVersion.CompareTo(installedVersion) <= 0)
                return UpdateCheckResult.UpToDate(installedVersion, latestVersion, releaseNotes, release.HtmlUrl);

            var asset = release.Assets?.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, InstallerAssetName, StringComparison.Ordinal)
                && string.Equals(candidate.State, "uploaded", StringComparison.OrdinalIgnoreCase));
            if (asset is null || !IsTrustedAssetUrl(asset.BrowserDownloadUrl) || !TryGetSha256(asset.Digest, out var sha256))
                return UpdateCheckResult.Failed("The official update installer is missing or could not be verified. No update was downloaded.");

            return UpdateCheckResult.Available(installedVersion, latestVersion, releaseNotes, release.HtmlUrl, asset.BrowserDownloadUrl!, sha256, asset.Size);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("Update checking was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("The update check timed out. Check your internet connection and try again.");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failed("CodeBox X could not reach GitHub. Check your internet connection and try again.");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed("GitHub returned an invalid update response. No update was downloaded.");
        }
        catch (Exception)
        {
            return UpdateCheckResult.Failed("CodeBox X could not check for updates safely. Try again later.");
        }
    }

    public async Task<UpdateDownloadResult> DownloadInstallerAsync(UpdateCheckResult update, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!update.IsUpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.ExpectedSha256) || !IsTrustedAssetUrl(update.DownloadUrl))
            return UpdateDownloadResult.Failed("No verified official update is available to download.");

        var updateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBoxX", "Updates");
        var versionName = update.LatestVersion?.ToString() ?? "latest";
        var installerPath = Path.Combine(updateDirectory, $"CodeBoxX-Setup-{versionName}-win-x64.exe");
        var temporaryPath = installerPath + ".partial";

        try
        {
            Directory.CreateDirectory(updateDirectory);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

            long received = 0;
            long? contentLength;

            using (var response = await GetTrustedDownloadResponseAsync(update.DownloadUrl, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                    return UpdateDownloadResult.Failed($"The official update download failed (HTTP {(int)response.StatusCode}).");

                contentLength = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;
                        progress?.Report(new UpdateDownloadProgress(received, contentLength));
                    }

                    await destination.FlushAsync(cancellationToken);
                }
            }

            if (contentLength is not null && received != contentLength.Value)
                return UpdateDownloadResult.Failed("The update download was incomplete and was discarded.");

            if (!await HasPortableExecutableHeaderAsync(temporaryPath, cancellationToken))
                return UpdateDownloadResult.Failed("The downloaded update is not a valid Windows installer and was discarded.");

            var actualSha256 = await CalculateSha256Async(temporaryPath, cancellationToken);
            if (!string.Equals(actualSha256, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return UpdateDownloadResult.Failed("The downloaded update did not match GitHub’s SHA-256 digest and was discarded.");

            if (File.Exists(installerPath)) File.Delete(installerPath);
            File.Move(temporaryPath, installerPath);
            progress?.Report(new UpdateDownloadProgress(received, contentLength));
            return UpdateDownloadResult.Succeeded(installerPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateDownloadResult.Failed("Update download was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return UpdateDownloadResult.Failed("The update download timed out. Check your internet connection and try again.");
        }
        catch (HttpRequestException)
        {
            return UpdateDownloadResult.Failed("The update download failed because GitHub could not be reached.");
        }
        catch (IOException)
        {
            return UpdateDownloadResult.Failed("CodeBox X could not save the update installer. Check available disk space and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            return UpdateDownloadResult.Failed("CodeBox X does not have permission to save the update installer.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public UpdateLaunchResult StartInstaller(string installerPath)
    {
        try
        {
            if (!File.Exists(installerPath) || !HasPortableExecutableHeader(installerPath))
                return UpdateLaunchResult.Failed("The verified update installer is no longer available.");

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath)
            });
            return UpdateLaunchResult.Succeeded();
        }
        catch (Exception)
        {
            return UpdateLaunchResult.Failed("CodeBox X could not start the update installer. You can try the download again from Settings.");
        }
    }

    private static bool TryParseReleaseVersion(string value, out Version version)
    {
        if (Version.TryParse(value.Trim().TrimStart('v', 'V'), out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static bool TryGetSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var value = digest[7..];
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) return false;
        sha256 = value;
        return true;
    }

    private static bool IsTrustedAssetUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        return uri.AbsolutePath.StartsWith($"/{RepositoryOwner}/{RepositoryName}/releases/download/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith($"/{InstallerAssetName}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> GetTrustedDownloadResponseAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        var current = new Uri(downloadUrl, UriKind.Absolute);
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            var response = await _httpClient.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            var next = location is null ? null : (location.IsAbsoluteUri ? location : new Uri(current, location));
            response.Dispose();
            if (next is null || next.Scheme != Uri.UriSchemeHttps || !IsTrustedRedirectHost(next.Host))
                throw new HttpRequestException("The official GitHub update redirect was not trusted.");
            current = next;
        }

        throw new HttpRequestException("The official GitHub update redirected too many times.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsTrustedRedirectHost(string host) => host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static bool HasPortableExecutableHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static async Task<bool> HasPortableExecutableHeaderAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[2];
        var read = await stream.ReadAsync(bytes, cancellationToken);
        return read == 2 && bytes[0] == 'M' && bytes[1] == 'Z';
    }

    private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; init; }
        [JsonPropertyName("digest")] public string? Digest { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
    }
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version InstalledVersion,
    Version? LatestVersion,
    string Message,
    string ReleaseNotes,
    string? ReleasePageUrl,
    string? DownloadUrl,
    string? ExpectedSha256,
    long DownloadSize)
{
    public bool IsUpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable;
    public static UpdateCheckResult Available(Version current, Version latest, string notes, string? page, string url, string hash, long size) => new(UpdateCheckStatus.UpdateAvailable, current, latest, "A verified update is available.", notes, page, url, hash, size);
    public static UpdateCheckResult UpToDate(Version current, Version latest, string notes, string? page) => new(UpdateCheckStatus.UpToDate, current, latest, "CodeBox X is up to date.", notes, page, null, null, 0);
    public static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, new Version(0, 0), null, message, string.Empty, null, null, null, 0);
}

public enum UpdateCheckStatus { UpToDate, UpdateAvailable, Failed }

public sealed record UpdateDownloadProgress(long ReceivedBytes, long? TotalBytes)
{
    public double Percentage => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value * 100, 0, 100) : 0;
}

public sealed record UpdateDownloadResult(bool Success, string Message, string? InstallerPath)
{
    public static UpdateDownloadResult Succeeded(string path) => new(true, "The update was downloaded and verified.", path);
    public static UpdateDownloadResult Failed(string message) => new(false, message, null);
}

public sealed record UpdateLaunchResult(bool Success, string Message)
{
    public static UpdateLaunchResult Succeeded() => new(true, "The verified installer was started.");
    public static UpdateLaunchResult Failed(string message) => new(false, message);
}
