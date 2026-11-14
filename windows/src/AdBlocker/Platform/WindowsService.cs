using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using AdBlocker.Core;

namespace AdBlocker.Platform;

/// <summary>Runs the blocker when Windows starts AdBlocker as a service.</summary>
public sealed class AdBlockerWindowsService : ServiceBase
{
    private readonly Func<CancellationToken, Task> _run;
    private CancellationTokenSource? _stopping;
    private Task? _running;

    public AdBlockerWindowsService(Func<CancellationToken, Task> run)
    {
        _run = run;
        ServiceName = ServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        // Windows notes every start and stop in the System log already. With a lasting problem
        // (port 53 taken) the service is retried once a minute, and three entries for each try
        // would push everything else out of the Application log.
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        var stopping = _stopping = new CancellationTokenSource();
        _running = Task.Run(() => _run(stopping.Token));
        _running.ContinueWith(task =>
        {
            if (UnexpectedEnd(task, stopping.IsCancellationRequested) is not { } reason) return;
            try
            {
                EventLog.WriteEntry($"AdBlocker stopped: {reason}", EventLogEntryType.Error);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                // A full event log is no reason to keep looking as if all is well.
            }
            // Without stopping, Windows would show AdBlocker as running while nothing answers DNS.
            // Stopping with an error code lets Windows restart it (see ServiceManager.Install).
            ExitCode = 1;
            Stop();
        }, TaskScheduler.Default);
    }

    protected override void OnStop()
    {
        // Restoring network settings can take a moment.
        RequestAdditionalTime(20_000);
        StopBlocking(TimeSpan.FromSeconds(20));
    }

    // No RequestAdditionalTime here: Windows calls this while the service still reports running, and
    // it throws then, which would end the process before the network settings are restored. Windows
    // only gives services a few seconds at shutdown anyway; restoring takes well under one.
    protected override void OnShutdown() => StopBlocking(TimeSpan.FromSeconds(4));

    private void StopBlocking(TimeSpan wait)
    {
        _stopping?.Cancel();
        try
        {
            _running?.Wait(wait);
        }
        catch (AggregateException)
        {
            // Already reported by the continuation in OnStart.
        }
    }

    /// <summary>
    /// Why the blocker ended although nobody asked it to stop, or null when it was asked to. Ending
    /// by itself is a failure whatever the reason, also when it ended as cancelled (a timeout does that).
    /// </summary>
    internal static string? UnexpectedEnd(Task blocker, bool stopRequested)
    {
        if (stopRequested) return null;
        if (blocker.Exception?.GetBaseException() is { } error) return error.Message;
        return blocker.IsCanceled
            ? "The blocker was cancelled without being asked to stop."
            : "The blocker ended without being asked to stop.";
    }
}

/// <summary>Installs, removes and controls the AdBlocker Windows service.</summary>
public static class ServiceManager
{
    public const string ServiceName = "AdBlocker";
    private const string Description = "Blocks ads and trackers for the whole PC by filtering DNS lookups.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AdBlocker");

    private static readonly ExecutableSwap Files = new(InstallDirectory);

    public static string InstalledExecutable => Files.Executable;

    /// <summary>The service state, or null if the service is not installed.</summary>
    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static bool IsRunning => Status() is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

    /// <summary>Copies this executable to Program Files and registers it as an automatic service.</summary>
    public static string Install()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find adblocker.exe.");
        if (!string.Equals(Path.GetFullPath(current), InstalledExecutable, StringComparison.OrdinalIgnoreCase))
        {
            if (Status() is not null) Stop();
            // The installed copy may still be running: "adblocker upgrade" in 1.4.0 and 1.5.0 runs
            // the new version's install from the temporary folder and waits for it, and so might
            // "adblocker log -f" in another window. Windows lets a running program be renamed, not
            // overwritten, so the new version goes in the same way as an upgrade's does.
            Files.PutInPlace(File.ReadAllBytes(current));
        }
        // The files an upgrade leaves behind stay until this version is confirmed to block (see
        // DeleteLeftoversAfter): the upgrade goes back to the previous version if it doesn't.

        var binaryPath = $"\"{InstalledExecutable}\" service";
        if (Status() is null)
        {
            Sc("create", ServiceName, "binPath=", binaryPath, "start=", "auto", "DisplayName=", "AdBlocker");
        }
        else
        {
            Sc("config", ServiceName, "binPath=", binaryPath, "start=", "auto");
        }
        Sc("description", ServiceName, Description);
        // Restart after a crash, so the PC is not left pointing at a DNS server that is gone. The
        // last action repeats, so a lasting problem (port 53 taken, say) is retried once a minute.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/10000/restart/60000");
        // Also when the service stops itself with an error, not only when its process crashes.
        Sc("failureflag", ServiceName, "1");
        return InstalledExecutable;
    }

    /// <summary>
    /// Puts an already verified new version in place of the installed one, for "upgrade". The
    /// bytes go straight from memory into Program Files, which only administrators can change,
    /// so nothing can swap the file between the checksum check and the start.
    /// </summary>
    public static void PutInPlace(byte[] executable)
    {
        if (Status() is not null) Stop();
        Files.PutInPlace(executable);
    }

    /// <summary>
    /// After a failed upgrade, goes back to the version <see cref="PutInPlace"/> replaced. Returns
    /// whether that version is in place again. Without one to go back to, the service is left as
    /// it is: the new version only removes the previous one once it is confirmed to block.
    /// </summary>
    public static bool RestorePrevious()
    {
        if (!Files.CanRestore) return false;
        // The new version may have started the service before it failed, and its file can only be
        // replaced once it has stopped. Failing to stop shows up as failing to replace the file.
        try
        {
            if (Status() is not null) Stop();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
        }
        return Files.RestorePrevious();
    }

    /// <summary>
    /// Runs <paramref name="confirm"/>, which starts the service and checks it blocks, and only then
    /// removes what an upgrade left behind, so a failed start can still go back to the previous version.
    /// </summary>
    public static void DeleteLeftoversAfter(Action confirm) => Files.DeleteLeftoversAfter(confirm);

    public static void Uninstall()
    {
        if (Status() is null) return;
        Stop();
        Sc("delete", ServiceName);
    }

    /// <summary>
    /// Asks Windows to start the service. Windows calls it running before it has opened port 53,
    /// so the caller waits for it to block (and notices it stopping again) by itself.
    /// </summary>
    public static void Start()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return;
        service.Start();
    }

    public static void Stop()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped) return;
        if (service.Status is not ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    private static void Sc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not run sc.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new UserError($"sc {arguments[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}

/// <summary>
/// Swaps adblocker.exe in <paramref name="directory"/> for a new version, for "upgrade", and keeps
/// the previous one as adblocker.exe.old until the new one is confirmed to work.
/// </summary>
public sealed class ExecutableSwap(string directory)
{
    /// <summary><see cref="PutInPlace"/> moved the installed copy aside, to adblocker.exe.old.</summary>
    private bool _keptPrevious;

    /// <summary><see cref="PutInPlace"/> moved the new version in.</summary>
    private bool _installedNew;

    public string Executable { get; } = Path.Combine(directory, "adblocker.exe");

    private string Previous => Executable + ".old";
    private string Staged => Executable + ".new";

    /// <summary>Puts <paramref name="executable"/> in place of the installed copy, which is kept as adblocker.exe.old.</summary>
    public void PutInPlace(byte[] executable)
    {
        Directory.CreateDirectory(directory);
        // A copy an earlier upgrade left behind is not the version to go back to.
        TryDelete(Previous);
        File.WriteAllBytes(Staged, executable);
        // The installed copy may be the very program running this upgrade. Windows lets a
        // running program be renamed, just not overwritten.
        if (File.Exists(Executable))
        {
            RetryWhileInUse(() => File.Move(Executable, Previous, overwrite: true));
            _keptPrevious = true;
        }
        RetryWhileInUse(() => File.Move(Staged, Executable, overwrite: true));
        _installedNew = true;
    }

    /// <summary>
    /// Whether <see cref="RestorePrevious"/> would leave the version that was installed before in
    /// place: also when <see cref="PutInPlace"/> failed before changing anything, but not once the
    /// previous version was removed (by a new version that was confirmed to work) or when there was none.
    /// </summary>
    public bool CanRestore => _keptPrevious ? File.Exists(Previous) : !_installedNew && File.Exists(Executable);

    /// <summary>
    /// Goes back to the version <see cref="PutInPlace"/> replaced. Returns whether that version is
    /// in place (see <see cref="CanRestore"/>).
    /// </summary>
    public bool RestorePrevious()
    {
        TryDelete(Staged);
        if (!_keptPrevious) return !_installedNew && File.Exists(Executable);
        if (!File.Exists(Previous)) return false;
        RetryWhileInUse(() => File.Move(Previous, Executable, overwrite: true));
        _keptPrevious = _installedNew = false;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="confirm"/>, which starts the new version and checks it works, and only
    /// then removes the previous version, so that a failed start can still go back to it.
    /// </summary>
    public void DeleteLeftoversAfter(Action confirm)
    {
        confirm();
        DeleteLeftovers();
    }

    /// <summary>
    /// Removes the files an upgrade leaves behind. The previous version may still be running
    /// (it is the program that started the upgrade), so failing that, Windows removes it at
    /// the next restart.
    /// </summary>
    private void DeleteLeftovers()
    {
        TryDelete(Staged);
        if (!File.Exists(Previous) || TryDelete(Previous)) return;
        NativeMethods.MoveFileEx(Previous, null, NativeMethods.MovefileDelayUntilReboot);
    }

    /// <summary>
    /// A service that just stopped can keep its file open for a moment while its process exits.
    /// Replacing a file that is still open (or still mapped as a running program) is "access
    /// denied" rather than "in use", so both are waited out.
    /// </summary>
    internal void RetryWhileInUse(Action change)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                change();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(250);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new UserError($"Could not replace {Executable} because it is still in use. Please wait a moment and try again.", e);
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public static class Elevation
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void Require(string command)
    {
        if (IsAdministrator) return;
        throw new UserError(
            $"\"adblocker {command}\" changes system settings, so it needs administrator rights.\n" +
            "Open Terminal (or Command Prompt) with \"Run as administrator\" and run the command again.");
    }
}
