using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace MasLauncher;

/// <summary>One launcher release, as published on the repository's Releases page.</summary>
internal sealed record LauncherRelease(
    string Version,
    string DownloadUrl,
    string? Sha256,
    string? Notes,
    string? PageUrl);

/// <summary>
/// Updates the launcher from its own published releases. The repository is compiled in on
/// purpose: MAS publishes the engine, not this program, so the two update channels stay
/// separate. The downloaded image must report the version the release tag names and, when
/// GitHub supplies an asset digest, must match that hash too.
/// </summary>
internal static class AppUpdater
{
    public const string Repo = "eddshine/py_hello_world";
    private const string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>Assets are published as “WinAct &lt;version&gt;.exe”.</summary>
    private const string AssetLabel = "WinAct <version>.exe";

    /// <summary>The name early builds used; still accepted so those copies can move across.</summary>
    private const string LegacyAssetName = "WindowsActivator.exe";

    private const long MinBytes = 1024 * 1024;
    private const long MaxBytes = 200L * 1024 * 1024;

    /// <summary>Reads the newest published release, or null when none exists yet.</summary>
    public static async Task<LauncherRelease?> GetLatestAsync(CancellationToken token = default)
    {
        using var http = CreateHttp(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync(LatestApi, token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the release feed answered HTTP {(int)response.StatusCode}");

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Reads a release payload. Exposed so the diagnostics can exercise it offline.</summary>
    public static LauncherRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) return null;

        var tag = root.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() : null;
        var version = AppInfo.FromTag(tag);
        if (version is null) return null;

        string? url = null;
        string? digest = null;
        long size = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                if (!IsLauncherAsset(name)) continue;

                url = asset.TryGetProperty("browser_download_url", out var urlProperty)
                    ? urlProperty.GetString()
                    : null;
                if (asset.TryGetProperty("size", out var sizeProperty) && sizeProperty.TryGetInt64(out var bytes))
                    size = bytes;
                if (asset.TryGetProperty("digest", out var digestProperty) &&
                    digestProperty.ValueKind == JsonValueKind.String)
                    digest = digestProperty.GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidDataException($"the release “{tag}” has no {AssetLabel} attached");

        if (size > 0 && (size < MinBytes || size > MaxBytes))
            throw new InvalidDataException($"the attached file is {size:N0} bytes, which is not a plausible launcher size");

        var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest[7..].Trim().ToLowerInvariant()
            : null;

        return new LauncherRelease(
            version,
            url,
            string.IsNullOrWhiteSpace(sha) ? null : sha,
            root.TryGetProperty("body", out var body) ? body.GetString() : null,
            root.TryGetProperty("html_url", out var page) ? page.GetString() : null);
    }

    /// <summary>
    /// True for “WinAct &lt;version&gt;.exe” and for the name earlier releases used.
    /// </summary>
    private static bool IsLauncherAsset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(name, LegacyAssetName, StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith("WinAct", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads the release asset into <c>%TEMP%</c> and returns its path.</summary>
    public static async Task<string> DownloadAsync(LauncherRelease release, CancellationToken token = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "WinAct-app");
        Directory.CreateDirectory(folder);
        var staged = Path.Combine(folder, "WinAct.exe");

        using var http = CreateHttp(TimeSpan.FromMinutes(5));
        using var response = await http
            .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the download answered HTTP {(int)response.StatusCode}");

        if (response.Content.Headers.ContentLength is long announced && announced > MaxBytes)
            throw new InvalidDataException($"the download is {announced:N0} bytes, which is too large");

        await using var remote = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var local = File.Create(staged);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await remote.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBytes) throw new InvalidDataException("the download is larger than any launcher build");
            await local.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }

        if (total < MinBytes) throw new InvalidDataException($"the download is only {total:N0} bytes");

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (release.Sha256 is not null && !string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the download does not match the hash published with the release");

        return staged;
    }

    /// <summary>Checks that the download really is the launcher build the release names.</summary>
    public static string? Validate(string path, string expectedVersion)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "the downloaded file is missing";
            if (info.Length < MinBytes) return "the downloaded file is too small to be the launcher";

            var reported = AppInfo.FromTag(FileVersionInfo.GetVersionInfo(path).ProductVersion)
                ?? AppInfo.FromTag(FileVersionInfo.GetVersionInfo(path).FileVersion);

            if (reported is null) return "the downloaded file has no version information";
            if (AppInfo.Compare(reported, expectedVersion) != 0)
                return $"the download reports version {reported}, but the release is {expectedVersion}";

            return null;
        }
        catch (Exception ex)
        {
            return "cannot read the download (" + ex.Message + ")";
        }
    }

    /// <summary>True when the running image can be replaced: it is the exe and its folder is writable.</summary>
    public static bool CanSelfInstall(out string reason)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            reason = "the running file could not be located";
            return false;
        }

        if (!IsLauncherAsset(Path.GetFileName(exe)))
        {
            reason = "this build is not running as a WinAct executable (" + Path.GetFileName(exe) + ")";
            return false;
        }

        try
        {
            var folder = Path.GetDirectoryName(exe)!;
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            reason = "the app folder is not writable (" + ex.Message + ")";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Puts <paramref name="staged"/> in place of the running image. The old image is renamed
    /// to <c>.old</c>, which Windows permits even while it runs, so the path is never absent.
    /// </summary>
    public static void Install(string staged, string target, out string backup)
    {
        var incoming = target + ".new";
        File.Copy(staged, incoming, overwrite: true);

        backup = target + ".old";
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }

        File.Move(target, backup);
        File.Move(incoming, target);
    }

    /// <summary>Removes the previous image left behind by an update.</summary>
    public static void CleanupStale()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch
        {
            // still locked; try again next launch
        }
    }

    /// <summary>Starts the newly installed image.</summary>
    public static void Restart(string exe)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        };
        Process.Start(start);
    }

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinAct/" + AppInfo.Version);
        return http;
    }
}
