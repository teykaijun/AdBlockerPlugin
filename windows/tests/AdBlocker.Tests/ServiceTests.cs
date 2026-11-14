using AdBlocker.Core;
using AdBlocker.Platform;

namespace AdBlocker.Tests;

/// <summary>The file swap "upgrade" does in Program Files, here in a throw-away folder.</summary>
public sealed class ExecutableSwapTests : IDisposable
{
    private static readonly byte[] Old = "the version you had"u8.ToArray();
    private static readonly byte[] New = "the new version"u8.ToArray();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adblocker-tests", Guid.NewGuid().ToString("N"));

    public ExecutableSwapTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Executable, Old);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Executable => Path.Combine(_directory, "adblocker.exe");

    private string[] Files => Directory.GetFiles(_directory).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Goes_back_to_the_version_you_had_when_the_new_one_fails()
    {
        var swap = new ExecutableSwap(_directory);
        swap.PutInPlace(New);
        Assert.Equal(New, File.ReadAllBytes(Executable));
        Assert.Equal(["adblocker.exe", "adblocker.exe.old"], Files);

        // The new version installs itself (another process, so another swap), and its service stops right away.
        Assert.Throws<UserError>(() => new ExecutableSwap(_directory).DeleteLeftoversAfter(() => throw new UserError("The service stopped right after starting.")));
        Assert.Equal(["adblocker.exe", "adblocker.exe.old"], Files); // still there to go back to

        Assert.True(swap.CanRestore);
        Assert.True(swap.RestorePrevious());
        Assert.Equal(Old, File.ReadAllBytes(Executable));
        Assert.Equal(["adblocker.exe"], Files);
    }

    [Fact]
    public void Removes_the_previous_version_only_once_the_new_one_works()
    {
        var swap = new ExecutableSwap(_directory);
        swap.PutInPlace(New);

        var confirmed = false;
        new ExecutableSwap(_directory).DeleteLeftoversAfter(() =>
        {
            Assert.True(File.Exists(Executable + ".old")); // kept while the start is being confirmed
            confirmed = true;
        });
        Assert.True(confirmed);
        Assert.Equal(["adblocker.exe"], Files);
        Assert.Equal(New, File.ReadAllBytes(Executable));

        // Nothing is left to go back to, so the upgrade leaves the working new version running.
        Assert.False(swap.CanRestore);
        Assert.False(swap.RestorePrevious());
        Assert.Equal(New, File.ReadAllBytes(Executable));
    }

    [Fact]
    public void Leaves_the_installed_version_alone_when_the_swap_fails_early()
    {
        File.WriteAllBytes(Executable + ".old", "an even older version"u8.ToArray()); // left behind by an earlier upgrade
        var swap = new ExecutableSwap(_directory);
        using (new FileStream(Executable + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => swap.PutInPlace(New)); // can't write the new version
        }

        Assert.True(swap.CanRestore);
        Assert.True(swap.RestorePrevious()); // what is installed is the version you had
        Assert.Equal(Old, File.ReadAllBytes(Executable));
        Assert.Equal(["adblocker.exe"], Files); // and the older copy was not mistaken for it
    }

    [Fact]
    public async Task Waits_for_the_file_of_a_service_that_is_still_exiting()
    {
        var swap = new ExecutableSwap(_directory);
        swap.PutInPlace(New);

        // Replacing a file that is still open is "access denied" rather than "in use".
        var held = new FileStream(Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Run(async () =>
        {
            await Task.Delay(300);
            await held.DisposeAsync();
        });
        Assert.True(swap.RestorePrevious());
        await released;
        Assert.Equal(Old, File.ReadAllBytes(Executable));
    }

    [Fact]
    public void Has_nothing_to_go_back_to_without_an_installed_version()
    {
        File.Delete(Executable);
        var swap = new ExecutableSwap(_directory);
        swap.PutInPlace(New);
        Assert.False(swap.CanRestore);
        Assert.False(swap.RestorePrevious());
        Assert.Equal(New, File.ReadAllBytes(Executable));
    }
}

public class WindowsServiceTests
{
    [Fact]
    public void Treats_any_end_nobody_asked_for_as_a_failure()
    {
        Assert.Null(AdBlockerWindowsService.UnexpectedEnd(Task.CompletedTask, stopRequested: true));
        Assert.Null(AdBlockerWindowsService.UnexpectedEnd(Task.FromCanceled(new CancellationToken(canceled: true)), stopRequested: true));
        Assert.Null(AdBlockerWindowsService.UnexpectedEnd(Task.FromException(new IOException("x")), stopRequested: true));

        Assert.Equal("Port 53 is already in use.",
            AdBlockerWindowsService.UnexpectedEnd(Task.FromException(new UserError("Port 53 is already in use.")), stopRequested: false));
        // The blocker's own description, not the innermost detail.
        Assert.Equal("Updating the lists failed: The disk is full.", AdBlockerWindowsService.UnexpectedEnd(
            Task.FromException(new InvalidOperationException("Updating the lists failed: The disk is full.", new IOException("The disk is full."))),
            stopRequested: false));
        // A timeout ends a task as cancelled, and that is no reason to look as if all is well.
        Assert.Equal("The blocker was cancelled without being asked to stop.",
            AdBlockerWindowsService.UnexpectedEnd(Task.FromCanceled(new CancellationToken(canceled: true)), stopRequested: false));
        Assert.Equal("The blocker ended without being asked to stop.",
            AdBlockerWindowsService.UnexpectedEnd(Task.CompletedTask, stopRequested: false));
    }
}

public sealed class BrowserCheckTests : IDisposable
{
    private readonly string _profiles = Path.Combine(Path.GetTempPath(), "adblocker-tests", Guid.NewGuid().ToString("N"));

    public BrowserCheckTests() => Directory.CreateDirectory(_profiles);

    public void Dispose() => Directory.Delete(_profiles, recursive: true);

    private string Prefs(string profile, int mode, string below = "")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_profiles, profile, below)).FullName;
        var file = Path.Combine(folder, "prefs.js");
        File.WriteAllText(file, $"user_pref(\"browser.startup.page\", 3);\nuser_pref(\"network.trr.mode\", {mode});\n");
        return file;
    }

    [Fact]
    public void Finds_a_Firefox_profile_that_uses_its_own_DNS()
    {
        Assert.False(BrowserCheck.FirefoxUsesOwnDns(Path.Combine(_profiles, "missing")));
        Prefs("abc.default-release", mode: 5);
        Assert.False(BrowserCheck.FirefoxUsesOwnDns(_profiles));

        // Only the prefs.js at the top of a profile counts; the folders below aren't searched.
        Prefs("abc.default-release", mode: 3, below: @"storage\default");
        Assert.False(BrowserCheck.FirefoxUsesOwnDns(_profiles));

        Prefs("xyz.work", mode: 2);
        Assert.True(BrowserCheck.FirefoxUsesOwnDns(_profiles));
    }

    [Fact]
    public void Skips_a_profile_it_cannot_read()
    {
        var file = Prefs("abc.default-release", mode: 3);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(BrowserCheck.FirefoxUsesOwnDns(_profiles));
        }
        Assert.True(BrowserCheck.FirefoxUsesOwnDns(_profiles));
    }
}
