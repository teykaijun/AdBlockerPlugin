using System.Security.Cryptography;
using System.Text.Json;
using AdBlocker.Config;

namespace AdBlocker.Core;

/// <summary>A release on the project's GitHub page.</summary>
public sealed record Release(string Version, string ExeUrl, long ExeSize, string? SumsUrl, string PageUrl);

/// <summary>
/// Looks for a newer AdBlocker on the project's GitHub releases page and downloads it.
/// The executable is not code-signed, so the SHA-256 published with the release is checked;
/// that catches a broken download, and beyond it this trusts GitHub like a manual download does.
/// </summary>
public static class Updates
{
    public const string LatestReleaseApi = "https://api.github.com/repos/teykaijun/AdBlockerPlugin/releases/latest";
    public const string ExeName = "adblocker.exe";
    public const string SumsName = "SHA256SUMS.txt";
    private const long MaxBytes = 100 * 1024 * 1024;

    /// <summary>Reads GitHub's "latest release" answer. Null if it holds nothing to install.</summary>
    public static Release? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

        var version = Text(root, "tag_name").TrimStart('v', 'V').Trim();
        if (version.Length == 0) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        string? exeUrl = null, sumsUrl = null;
        long exeSize = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            var url = Text(asset, "browser_download_url");
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            switch (Text(asset, "name"))
            {
                case ExeName:
                    exeUrl = url;
                    exeSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) ? value : 0;
                    break;
                case SumsName:
                    sumsUrl = url;
                    break;
            }
        }
        return exeUrl is null ? null : new Release(version, exeUrl, exeSize, sumsUrl, Text(root, "html_url"));
    }

    /// <summary>Whether <paramref name="latest"/> ("1.4.1" or "v1.4.1") is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = Parts(latest);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right) return left > right;
        }
        return false;
    }

    /// <summary>The SHA-256 recorded for <paramref name="fileName"/> in a SHA256SUMS.txt.</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64) continue;
            if (parts[1].TrimStart('*').Trim() == fileName) return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The latest release, or null when this build is already up to date.</summary>
    public static async Task<Release?> CheckAsync(HttpClient http, string currentVersion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Add("Accept", "application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = ParseRelease(await response.Content.ReadAsStringAsync(cancellationToken));
        return release is not null && IsNewer(release.Version, currentVersion) ? release : null;
    }

    /// <summary>Downloads the release's executable into <paramref name="directory"/> and checks its SHA-256.</summary>
    public static async Task<string> DownloadAsync(
        Release release,
        HttpClient http,
        string directory,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        if (release.SumsUrl is null) throw new UserError("The release has no checksum file to check the download against.");
        var expected = ExpectedHash(await http.GetStringAsync(release.SumsUrl, cancellationToken), ExeName)
            ?? throw new UserError($"The release's {SumsName} does not list {ExeName}.");

        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"adblocker-{release.Version}.exe");
        using (var response = await http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = release.ExeSize > 0 ? release.ExeSize : response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(file);
            var buffer = new byte[81_920];
            long written = 0;
            var reported = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > MaxBytes) throw new UserError("The download is unexpectedly large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                var percent = total > 0 ? (int)Math.Clamp(written * 100 / total, 0, 100) : 0;
                if (percent != reported)
                {
                    reported = percent;
                    progress?.Invoke(percent);
                }
            }
        }

        string actual;
        await using (var written = File.OpenRead(file))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellationToken));
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new UserError("The download does not match the checksum published with the release.");
        }
        return file;
    }

    /// <summary>Records what a check found, so "adblocker status" can mention it without going online.</summary>
    public static void Save(AppPaths paths, Release? release)
    {
        var state = new UpdateState { CheckedAt = DateTimeOffset.UtcNow, Version = release?.Version, PageUrl = release?.PageUrl };
        AtomicFile.WriteAllText(paths.UpdateFile, JsonSerializer.Serialize(state, AppJson.Default.UpdateState));
    }

    public static UpdateState? Read(AppPaths paths)
    {
        try
        {
            return File.Exists(paths.UpdateFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(paths.UpdateFile), AppJson.Default.UpdateState)
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int[] Parts(string version) => version.Trim().TrimStart('v', 'V')
        .Split(['.', '-', '+'])
        .Select(part => int.TryParse(part, out var value) ? value : 0)
        .ToArray();

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
