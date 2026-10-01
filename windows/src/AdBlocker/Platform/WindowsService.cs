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
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _stopping = new CancellationTokenSource();
        _running = Task.Run(() => _run(_stopping.Token));
        _running.ContinueWith(task =>
        {
            if (!task.IsFaulted) return;
            var error = task.Exception!.GetBaseException();
            EventLog.WriteEntry($"AdBlocker stopped: {error.Message}", EventLogEntryType.Error);
            ExitCode = 1;
            Stop();
        }, TaskScheduler.Default);
    }

    protected override void OnStop() => StopBlocking();

    protected override void OnShutdown() => StopBlocking();

    private void StopBlocking()
    {
        // Restoring network settings can take a moment.
        RequestAdditionalTime(20_000);
        _stopping?.Cancel();
        try
        {
            _running?.Wait(TimeSpan.FromSeconds(20));
        }
        catch (AggregateException)
        {
            // Already reported by the continuation in OnStart.
        }
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

    public static string InstalledExecutable { get; } = Path.Combine(InstallDirectory, "adblocker.exe");

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
            Directory.CreateDirectory(InstallDirectory);
            RetryWhileInUse(() => File.Copy(current, InstalledExecutable, overwrite: true));
        }
        DeleteLeftovers();

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
        // Restart after a crash, so the PC is not left pointing at a DNS server that is gone.
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000/restart/30000");
        return InstalledExecutable;
    }

    private static string PreviousExecutable => InstalledExecutable + ".old";
    private static string StagedExecutable => InstalledExecutable + ".new";

    /// <summary>
    /// Puts an already verified new version in place of the installed one, for "upgrade". The
    /// bytes go straight from memory into Program Files, which only administrators can change,
    /// so nothing can swap the file between the checksum check and the start.
    /// </summary>
    public static void PutInPlace(byte[] executable)
    {
        if (Status() is not null) Stop();
        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllBytes(StagedExecutable, executable);
        TryDelete(PreviousExecutable);
        // The installed copy may be the very program running this upgrade. Windows lets a
        // running program be renamed, just not overwritten.
        if (File.Exists(InstalledExecutable))
        {
            RetryWhileInUse(() => File.Move(InstalledExecutable, PreviousExecutable, overwrite: true));
        }
        File.Move(StagedExecutable, InstalledExecutable, overwrite: true);
    }

    /// <summary>Goes back to the version <see cref="PutInPlace"/> replaced, if the new one did not start.</summary>
    public static bool RestorePrevious()
    {
        if (!File.Exists(PreviousExecutable)) return false;
        RetryWhileInUse(() => File.Move(PreviousExecutable, InstalledExecutable, overwrite: true));
        return true;
    }

    /// <summary>
    /// Removes the files an upgrade leaves behind. The previous version may still be running
    /// (it is the program that started the upgrade), so failing that, Windows removes it at
    /// the next restart.
    /// </summary>
    public static void DeleteLeftovers()
    {
        TryDelete(StagedExecutable);
        if (!File.Exists(PreviousExecutable) || TryDelete(PreviousExecutable)) return;
        NativeMethods.MoveFileEx(PreviousExecutable, null, NativeMethods.MovefileDelayUntilReboot);
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

    /// <summary>A service that just stopped can keep its file open for a moment while its process exits.</summary>
    private static void RetryWhileInUse(Action change)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                change();
                return;
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(250);
            }
            catch (IOException e)
            {
                throw new UserError($"Could not replace {InstalledExecutable} because it is still in use. Please wait a moment and try again.", e);
            }
        }
    }

    public static void Uninstall()
    {
        if (Status() is null) return;
        Stop();
        Sc("delete", ServiceName);
    }

    public static void Start()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status is ServiceControllerStatus.Running) return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
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
