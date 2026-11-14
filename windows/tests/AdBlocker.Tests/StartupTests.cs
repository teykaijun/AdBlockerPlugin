using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using AdBlocker.Config;
using AdBlocker.Core;
using AdBlocker.Platform;

namespace AdBlocker.Tests;

public class LoopTests
{
    private static Task Forever(CancellationToken token) => Task.Delay(Timeout.Infinite, token);

    [Fact]
    public async Task Stops_every_loop_and_waits_for_them()
    {
        var finished = false;
        using var stopping = new CancellationTokenSource();
        var running = Blocker.RunLoopsAsync(
            [
                ("Waiting", Forever),
                ("Slow to stop", async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    finally
                    {
                        await Task.Delay(200, CancellationToken.None);
                        finished = true;
                    }
                }),
            ],
            stopping.Token);
        await stopping.CancelAsync();
        await running; // a requested stop is not an error
        Assert.True(finished);
    }

    [Fact]
    public async Task Reports_a_loop_that_ends_by_itself()
    {
        var stopped = new TaskCompletionSource();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Blocker.RunLoopsAsync(
            [
                ("Waiting", async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    finally
                    {
                        stopped.SetResult();
                    }
                }),
                ("Updating the lists", _ => Task.CompletedTask),
            ],
            CancellationToken.None));
        Assert.Equal("Updating the lists stopped unexpectedly.", error.Message);
        Assert.True(stopped.Task.IsCompleted); // the other loop was stopped first
    }

    [Fact]
    public async Task Treats_a_timeout_inside_a_loop_as_a_failure()
    {
        // HttpClient reports a timeout as a cancellation, which must not look like a normal stop.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Blocker.RunLoopsAsync(
            [
                ("Waiting", Forever),
                ("Checking for updates", async _ =>
                {
                    await Task.Yield();
                    throw new TaskCanceledException("The request timed out.");
                }),
            ],
            CancellationToken.None));
        Assert.Equal("Checking for updates stopped unexpectedly.", error.Message);
    }

    [Fact]
    public async Task Passes_on_what_made_a_loop_fail()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Blocker.RunLoopsAsync(
            [
                ("Waiting", Forever),
                ("Saving the statistics", async _ =>
                {
                    await Task.Yield();
                    throw new IOException("The disk is full.");
                }),
            ],
            CancellationToken.None));
        Assert.Equal("Saving the statistics failed: The disk is full.", error.Message);
        Assert.IsType<IOException>(error.InnerException);
    }
}

public class SettingsFolderTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "adblocker-tests", Guid.NewGuid().ToString("N"));

    public SettingsFolderTests() => Directory.CreateDirectory(_parent);

    public void Dispose()
    {
        // Junctions first, so deleting the rest never reaches through one.
        foreach (var directory in Directory.EnumerateDirectories(_parent, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(directory);
        }
        Directory.Delete(_parent, recursive: true);
    }

    [Theory]
    [InlineData(WellKnownSidType.LocalSystemSid, true)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, true)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, false)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid, false)]
    [InlineData(WellKnownSidType.WorldSid, false)]
    [InlineData(WellKnownSidType.LocalServiceSid, false)]
    [InlineData(WellKnownSidType.NetworkServiceSid, false)]
    public void Trusts_only_folders_that_Windows_or_administrators_own(WellKnownSidType owner, bool trusted) =>
        Assert.Equal(trusted, AppPaths.IsTrustedOwner(new SecurityIdentifier(owner, null)));

    [Fact]
    public void Trusts_TrustedInstaller_but_not_ordinary_accounts()
    {
        Assert.True(AppPaths.IsTrustedOwner(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")));
        Assert.False(AppPaths.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1004336348-1177238915-682003330-1001")));
        Assert.False(AppPaths.IsTrustedOwner(null));
    }

    [Fact]
    public void Refuses_a_settings_folder_that_links_elsewhere()
    {
        var target = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName;
        var root = Path.Combine(_parent, "AdBlocker");
        CreateJunction(root, target);

        var error = Assert.Throws<UserError>(() => new AppPaths(root, restrictAccess: true).EnsureCreated());
        Assert.Contains(root, error.Message);
        Assert.Contains("is a link to another folder", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // nothing was created through the link
    }

    [Fact]
    public void Refuses_a_lists_or_logs_folder_that_links_elsewhere()
    {
        var target = Directory.CreateDirectory(Path.Combine(_parent, "elsewhere")).FullName;
        var lists = Path.Combine(Directory.CreateDirectory(Path.Combine(_parent, "AdBlocker")).FullName, "lists");
        CreateJunction(lists, target);

        var error = Assert.Throws<UserError>(() => AppPaths.Refuse(lists, checkOwner: false));
        Assert.Contains(lists, error.Message);
        AppPaths.Refuse(target, checkOwner: false); // an ordinary folder is fine
    }

    [Fact]
    public void Refuses_a_settings_folder_an_ordinary_account_owns()
    {
        var root = Directory.CreateDirectory(Path.Combine(_parent, "AdBlocker")).FullName;
        var owner = new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var paths = new AppPaths(root, restrictAccess: true);
        if (AppPaths.IsTrustedOwner(owner))
        {
            // Running elevated, Windows makes the administrators the owner of what the tests create.
            paths.EnsureCreated();
            Assert.True(Directory.Exists(paths.ListsDirectory));
        }
        else
        {
            var error = Assert.Throws<UserError>(paths.EnsureCreated);
            Assert.Contains("belongs to", error.Message);
            Assert.Contains("Delete that folder as administrator", error.Message);
            Assert.False(Directory.Exists(paths.ListsDirectory)); // nothing is created in it either
        }
    }

    private static void CreateJunction(string link, string target)
    {
        // Junctions, unlike symbolic links, need no administrator rights.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }
}

public class SharedFileTests : IDisposable
{
    private readonly AppPaths _paths = TestPaths.Temporary();

    public SharedFileTests() => _paths.EnsureCreated();

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private string File1 => Path.Combine(_paths.Root, "settings.json");

    [Fact]
    public void Writes_whole_files_and_leaves_no_temporary_ones()
    {
        AtomicFile.WriteAllText(File1, "first", durable: true);
        AtomicFile.WriteAllText(File1, "second");
        Assert.Equal("second", File.ReadAllText(File1));
        Assert.Equal(File1, Assert.Single(Directory.GetFiles(_paths.Root))); // no temporary files left behind
    }

    [Fact]
    public async Task Waits_for_a_reader_to_let_go()
    {
        File.WriteAllText(File1, "old");
        // Opened without delete sharing, as File.ReadAllText does: replacing the file has to wait.
        var reader = new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Run(async () =>
        {
            await Task.Delay(50);
            reader.Dispose();
        });
        AtomicFile.WriteAllText(File1, "new");
        await released;
        Assert.Equal("new", File.ReadAllText(File1));
    }

    [Fact]
    public void Gives_up_cleanly_when_the_file_stays_in_use()
    {
        File.WriteAllText(File1, "old");
        using (new FileStream(File1, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => AtomicFile.WriteAllText(File1, "new"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal("old", File.ReadAllText(File1));
        Assert.Equal(File1, Assert.Single(Directory.GetFiles(_paths.Root))); // no temporary files left behind
    }

    [Fact]
    public void Saving_the_statistics_never_throws()
    {
        var stats = new Stats(_paths.StatsFile);
        stats.Save(running: true);
        using (new FileStream(_paths.StatsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stats.Record(blocked: true);
            stats.Save(running: true); // the reader wins this time; the next save catches up
            Assert.NotNull(Stats.Read(_paths.StatsFile)); // and reading still works meanwhile
        }
        stats.Save(running: true);
        Assert.Equal(1, Stats.Read(_paths.StatsFile)!.Blocked);
    }

    [Fact]
    public void Lets_only_one_blocker_manage_the_network()
    {
        Assert.False(BlockerLock.IsHeldElsewhere(_paths));
        using (BlockerLock.Acquire(_paths))
        {
            Assert.True(BlockerLock.IsHeldElsewhere(_paths));
            var error = Assert.Throws<UserError>(() => BlockerLock.Acquire(_paths));
            Assert.Equal("Another copy of AdBlocker is already running (the service or \"adblocker run\").", error.Message);
        }
        Assert.False(BlockerLock.IsHeldElsewhere(_paths));
        // Nothing is left behind that another user could open and hold to keep AdBlocker from starting.
        Assert.False(File.Exists(_paths.LockFile));
        using (BlockerLock.Acquire(_paths))
        {
        }
        Assert.False(File.Exists(_paths.LockFile));
    }
}

public class NetworkSettingsTests
{
    [Fact]
    public void Fails_to_start_only_when_no_adapter_could_be_switched()
    {
        Assert.False(new ApplyResult([], [], Adapters: 0).NothingSwitched); // no network yet: wait for one
        Assert.False(new ApplyResult(["Wi-Fi"], [("Ethernet", "refused")], Adapters: 2).NothingSwitched);
        Assert.False(new ApplyResult([], [("Ethernet", "refused")], Adapters: 2).NothingSwitched); // Wi-Fi already pointed here
        Assert.True(new ApplyResult([], [("Wi-Fi", "refused"), ("Ethernet", "refused")], Adapters: 2).NothingSwitched);
    }
}
