using AdBlocker.Config;
using AdBlocker.Core;

namespace AdBlocker.Tests;

public class UpdatesTests
{
    private const string ApkUrl = "https://github.com/teykaijun/AdBlockerPlugin/releases/download/v1.4.1/adblocker.exe";

    private static string Release(string tag = "v1.4.1", string? assets = null, string extra = "") => $$"""
        {
          "tag_name": "{{tag}}", {{extra}}
          "html_url": "https://github.com/teykaijun/AdBlockerPlugin/releases/tag/{{tag}}",
          "assets": [{{assets ?? $$"""
            { "name": "adblocker.exe", "size": 12247948, "browser_download_url": "{{ApkUrl}}" },
            { "name": "SHA256SUMS.txt", "size": 250, "browser_download_url": "https://github.com/x/releases/download/v1.4.1/SHA256SUMS.txt" }
          """}}]
        }
        """;

    [Fact]
    public void Reads_the_version_and_files_of_a_release()
    {
        var release = Updates.ParseRelease(Release())!;
        Assert.Equal("1.4.1", release.Version);
        Assert.Equal(ApkUrl, release.ExeUrl);
        Assert.Equal(12_247_948, release.ExeSize);
        Assert.EndsWith("SHA256SUMS.txt", release.SumsUrl);
        Assert.EndsWith("/tag/v1.4.1", release.PageUrl);
    }

    [Theory]
    [InlineData("\"draft\": true,", null)]
    [InlineData("\"prerelease\": true,", null)]
    [InlineData("", """{ "name": "AdBlocker.apk", "browser_download_url": "https://x/AdBlocker.apk" }""")]
    [InlineData("", """{ "name": "adblocker.exe", "browser_download_url": "http://x/adblocker.exe" }""")]
    public void Ignores_releases_it_cannot_install(string extra, string? assets)
    {
        Assert.Null(Updates.ParseRelease(Release(assets: assets, extra: extra)));
        Assert.Null(Updates.ParseRelease(Release(tag: "")));
    }

    [Theory]
    [InlineData("1.4.1", "1.4.0", true)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.4.0", "1.4.0", false)]
    [InlineData("1.3.9", "1.4.0", false)]
    [InlineData("1.4.0", "1.4.1", false)]
    public void Compares_version_numbers_not_text(string latest, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(latest, current));

    [Fact]
    public void Finds_the_checksum_of_the_file_it_downloads()
    {
        const string hash = "e12a704dcf3976e1070a458aab5816f3266d36e3d0e85eb095dccbacd22d6bde";
        var sums = $"{hash}  adblocker.exe\ndbd0c90244c4eff6ea973791f531231185d4e66a9e498965e0d636cda04f3cdd  AdBlocker.apk\n";
        Assert.Equal(hash, Updates.ExpectedHash(sums, "adblocker.exe"));
        Assert.Equal(hash, Updates.ExpectedHash($"{hash} *adblocker.exe", "adblocker.exe"));
        Assert.Null(Updates.ExpectedHash(sums, "missing.exe"));
        Assert.Null(Updates.ExpectedHash("not a checksum file", "adblocker.exe"));
    }

    [Fact]
    public void Remembers_what_a_check_found()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adblocker-updates-{Guid.NewGuid():N}");
        var paths = new AppPaths(root, restrictAccess: false);
        try
        {
            paths.EnsureCreated();
            Assert.Null(Updates.Read(paths));

            Updates.Save(paths, new Release("1.4.1", ApkUrl, 1, null, "https://example.com/release"));
            var state = Updates.Read(paths)!;
            Assert.Equal("1.4.1", state.Version);
            Assert.Equal("https://example.com/release", state.PageUrl);
            Assert.True(DateTimeOffset.UtcNow - state.CheckedAt < TimeSpan.FromMinutes(1));

            Updates.Save(paths, null);
            Assert.Null(Updates.Read(paths)!.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
