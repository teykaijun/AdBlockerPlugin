using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AdBlocker.Platform;

/// <summary>
/// Finds browser settings that send DNS lookups past Windows (DNS over HTTPS to a
/// provider of the browser's own), which would let ads through.
/// </summary>
public static partial class BrowserCheck
{
    private static readonly (string Browser, string LocalState, string PolicyKey)[] ChromiumBrowsers =
    [
        ("Chrome", @"Google\Chrome\User Data\Local State", @"SOFTWARE\Policies\Google\Chrome"),
        ("Edge", @"Microsoft\Edge\User Data\Local State", @"SOFTWARE\Policies\Microsoft\Edge"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data\Local State", @"SOFTWARE\Policies\BraveSoftware\Brave"),
        ("Vivaldi", @"Vivaldi\User Data\Local State", @"SOFTWARE\Policies\Vivaldi"),
    ];

    public static IEnumerable<string> Problems()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, localState, policyKey) in ChromiumBrowsers)
        {
            if (ReadPolicy(policyKey) == "secure")
            {
                yield return $"{browser} is managed by a policy that forces its own secure DNS, so ads are not blocked in {browser}.";
            }
            else if (ReadSecureDnsMode(Path.Combine(localAppData, localState)) == "secure")
            {
                yield return $"{browser} uses a secure DNS provider of its own, so ads are not blocked in {browser}. " +
                             $"In {browser}, open Settings > Privacy and security > Security > Use secure DNS and choose your current service provider.";
            }
        }

        if (FirefoxUsesOwnDns())
        {
            yield return "Firefox is set to use DNS over HTTPS, so ads are not blocked in Firefox. " +
                         "In Firefox, open Settings > Privacy & Security > DNS over HTTPS and choose Off or Default Protection.";
        }
    }

    private static string? ReadSecureDnsMode(string localStateFile)
    {
        if (!File.Exists(localStateFile)) return null;
        try
        {
            using var stream = new FileStream(localStateFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("dns_over_https", out var doh) && doh.TryGetProperty("mode", out var mode)
                ? mode.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadPolicy(string keyPath)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(keyPath);
            if (key?.GetValue("DnsOverHttpsMode") is string mode) return mode;
        }
        return null;
    }

    private static bool FirefoxUsesOwnDns()
    {
        var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) return false;
        foreach (var prefs in Directory.EnumerateFiles(profiles, "prefs.js", SearchOption.AllDirectories))
        {
            try
            {
                // Mode 2 (with fallback) and 3 (only) use Firefox's own resolver. A user-set mode
                // also overrides the use-application-dns.net signal AdBlocker sends.
                if (TrrMode().Match(File.ReadAllText(prefs)) is { Success: true } match && match.Groups[1].Value is "2" or "3") return true;
            }
            catch (IOException)
            {
            }
        }
        return false;
    }

    [GeneratedRegex("""user_pref\("network\.trr\.mode",\s*(\d+)\)""")]
    private static partial Regex TrrMode();
}
