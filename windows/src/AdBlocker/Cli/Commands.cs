using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using AdBlocker.Config;
using AdBlocker.Core;
using AdBlocker.Dns;
using AdBlocker.Filtering;
using AdBlocker.Platform;

namespace AdBlocker.Cli;

internal static class Commands
{
    private static readonly AppPaths Paths = AppPaths.Default;
    private const string CompanionUrl = AppInfo.SourceUrl + "#browser-companion";

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = args.Skip(1).ToArray();
        if (command == "service") return RunService();

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached.
        }

        try
        {
            return command switch
            {
                "" => Overview(),
                "run" => await Run(rest),
                "install" => Install(rest),
                "uninstall" => Uninstall(rest),
                "start" => ControlService(rest, start: true, stop: false),
                "stop" => ControlService(rest, start: false, stop: true),
                "restart" => ControlService(rest, start: true, stop: true),
                "status" => Status(rest),
                "restore" => Restore(rest),
                "lists" => Lists(rest),
                "update" => await Update(rest),
                "block" => AddRule(rest, allow: false),
                "allow" => AddRule(rest, allow: true),
                "forget" => ForgetRule(rest),
                "check" => Check(rest),
                "dns" => Dns(rest),
                "lan" => Lan(rest),
                "log" => await Log(rest),
                "stats" when rest.FirstOrDefault() == "reset" => ResetStats(),
                "doctor" => Doctor(rest),
                "upgrade" => await Upgrade(rest),
                "support" => Support(rest),
                "help" or "--help" or "-h" or "/?" => Help(),
                "version" or "--version" => Version(),
                _ => throw new UserError($"Unknown command \"{args[0]}\". Run \"adblocker help\" to see what is available."),
            };
        }
        catch (UserError e)
        {
            Terminal.Error(e.Message);
            return 1;
        }
        catch (UnauthorizedAccessException e)
        {
            Terminal.Error($"Access denied: {e.Message}\nRun the command from a terminal opened with \"Run as administrator\".");
            return 1;
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Running
    // ----------------------------------------------------------------------------------------------

    private static async Task<int> Run(string[] args)
    {
        var options = new Arguments(args, valued: ["--port"], flags: ["--verbose", "-v", "--no-system-dns", "--lan"]);
        options.NoMoreThan(0);
        var manageDns = !options.Flag("--no-system-dns");
        var port = options.Int("--port", 53, 1, 65_535);
        if (manageDns && port != 53)
        {
            throw new UserError("Windows only sends DNS lookups to port 53. Add --no-system-dns to test on another port.");
        }
        // Also needed without --no-system-dns: the settings folder only admits administrators.
        Elevation.Require("run");
        if (ServiceManager.IsRunning)
        {
            throw new UserError("The AdBlocker service is already blocking ads. Stop it first with \"adblocker stop\", or use \"adblocker status\".");
        }

        var blocker = new Blocker(Paths, new BlockerOptions
        {
            Port = port,
            ManageSystemDns = manageDns,
            Verbose = options.Flag("--verbose", "-v"),
            ListenOnLan = options.Flag("--lan") ? true : null,
            Echo = Terminal.Query,
        }, new ConsoleLog());

        using var stopping = new CancellationTokenSource();
        var running = blocker.RunAsync(stopping.Token);

        // Ctrl+C, Ctrl+Break, closing the window, logging off and shutting down all stop the
        // blocker and wait for it to restore the network settings.
        var registrations = new[] { PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM, PosixSignal.SIGHUP }
            .Select(signal => PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                stopping.Cancel();
                try
                {
                    running.Wait(TimeSpan.FromSeconds(4));
                }
                catch (AggregateException)
                {
                }
            }))
            .ToList();
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            if (manageDns) new SystemDns(Paths).Restore();
        };

        try
        {
            Terminal.Heading(manageDns
                ? "AdBlocker is blocking ads for this PC. Press Ctrl+C to stop."
                : $"AdBlocker is answering DNS on 127.0.0.1:{port} without changing network settings. Press Ctrl+C to stop.");
            await running;
            if (manageDns) Terminal.Ok("Stopped. Network settings are back to how they were.");
            return 0;
        }
        finally
        {
            registrations.ForEach(r => r.Dispose());
        }
    }

    private static int RunService()
    {
        var log = new FileLog(Paths.ServiceLog);
        ServiceBase.Run(new AdBlockerWindowsService(async token =>
        {
            try
            {
                await new Blocker(Paths, new BlockerOptions(), log).RunAsync(token);
            }
            catch (Exception e)
            {
                log.Error(e.ToString());
                throw;
            }
        }));
        return 0;
    }

    // ----------------------------------------------------------------------------------------------
    // Service
    // ----------------------------------------------------------------------------------------------

    private static int Install(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        Elevation.Require("install");
        if (SystemDns.FindAdapters([]).Count == 0 && !NetworkInterface.GetIsNetworkAvailable())
        {
            Terminal.Warn("No network connection right now. AdBlocker will switch adapters over when they connect.");
        }

        var executable = ServiceManager.Install();
        Terminal.Line($"Installed {executable}");
        StartAndConfirm();
        Terminal.Ok("AdBlocker is blocking ads and will start automatically with Windows.");
        Terminal.Line("Check on it any time with \"adblocker status\". Community lists download in the background.");
        Terminal.Dim($"Optional: the browser companion closes blocked ad tabs in Chrome and Edge. {CompanionUrl}");
        PrintBrowserProblems();
        return 0;
    }

    private static int Uninstall(string[] args)
    {
        var options = new Arguments(args, [], ["--purge"]);
        options.NoMoreThan(0);
        Elevation.Require("uninstall");

        ServiceManager.Uninstall();
        Firewall.Remove();
        var restored = new SystemDns(Paths).Restore();
        if (restored.Count > 0) Terminal.Line($"Restored DNS settings of {string.Join(", ", restored)}.");
        Terminal.Ok("AdBlocker is uninstalled.");

        if (options.Flag("--purge") && Paths.Exists)
        {
            Directory.Delete(Paths.Root, recursive: true);
            Terminal.Line($"Deleted {Paths.Root}.");
        }
        else if (Paths.Exists)
        {
            Terminal.Dim($"Your settings are kept in {Paths.Root} (use --purge to delete them).");
        }
        if (Directory.Exists(ServiceManager.InstallDirectory))
        {
            Terminal.Dim($"You can now delete {ServiceManager.InstallDirectory}.");
        }
        return 0;
    }

    private static int ControlService(string[] args, bool start, bool stop)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        var name = start && stop ? "restart" : start ? "start" : "stop";
        Elevation.Require(name);
        if (ServiceManager.Status() is null) throw new UserError("AdBlocker is not installed. Run \"adblocker install\" first.");

        if (stop)
        {
            ServiceManager.Stop();
            if (!start) Terminal.Ok("AdBlocker stopped. Ads are no longer blocked and network settings are restored.");
        }
        if (start)
        {
            StartAndConfirm();
            Terminal.Ok("AdBlocker is blocking ads.");
        }
        return 0;
    }

    private static void StartAndConfirm()
    {
        ServiceManager.Start();
        // The service stops again by itself if it cannot start blocking (for example if port 53 is taken).
        Thread.Sleep(TimeSpan.FromSeconds(3));
        if (ServiceManager.Status() is not ServiceControllerStatus.Running)
        {
            throw new UserError($"The AdBlocker service stopped right after starting. Details are in {Paths.ServiceLog}.");
        }
    }

    private static int Restore(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        Elevation.Require("restore");
        if (ServiceManager.IsRunning)
        {
            throw new UserError("The AdBlocker service is running and would switch the settings back. Use \"adblocker stop\" instead.");
        }
        var restored = new SystemDns(Paths).Restore();
        Terminal.Ok(restored.Count > 0
            ? $"Restored the DNS settings of {string.Join(", ", restored)}."
            : "Nothing to restore: no network adapter is using AdBlocker.");
        return 0;
    }

    // ----------------------------------------------------------------------------------------------
    // Status
    // ----------------------------------------------------------------------------------------------

    private static int Overview()
    {
        Terminal.Heading($"AdBlocker for Windows {AppInfo.Version}");
        Terminal.Line("Blocks ads and trackers in every browser and app on this PC by filtering DNS lookups.");
        Terminal.Line();
        PrintProtectionState();
        Terminal.Line();
        Terminal.Line("Run \"adblocker help\" for all commands, or \"adblocker install\" (as administrator) to start blocking.");
        Terminal.Dim($"Support development ☕  {AppInfo.SupportUrl}");

        if (LaunchedByDoubleClick())
        {
            Terminal.Line();
            Terminal.Dim("This is a console program. Open Terminal as administrator and run the commands there.");
            Terminal.Dim("Press any key to close.");
            Console.ReadKey(intercept: true);
        }
        return 0;
    }

    private static int Status(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        Terminal.Heading("AdBlocker");
        PrintProtectionState();

        var stats = Stats.Read(Paths.StatsFile);
        if (stats is not null)
        {
            var today = stats.Day == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Terminal.Line();
            Terminal.Heading("Lookups");
            Terminal.Pairs(
                ("Today", today ? $"{stats.BlockedToday:N0} blocked of {stats.QueriesToday:N0}" : "none yet"),
                ($"Since {stats.Since.LocalDateTime:d MMM yyyy}", $"{stats.Blocked:N0} blocked of {stats.Queries:N0} ({Percent(stats.Blocked, stats.Queries)})"));
        }

        var config = ConfigStore.Load(Paths);
        var lists = new FilterLists(Paths);
        var status = lists.LoadStatus();
        Terminal.Line();
        Terminal.Heading("Filter lists");
        foreach (var list in FilterLists.BuiltIn.Where(l => FilterLists.IsEnabled(config, l)))
        {
            Terminal.Line($"  {list.Title} (built-in)");
        }
        foreach (var list in FilterLists.CommunityLists(config).Where(l => l.Enabled))
        {
            Terminal.Line($"  {list.Title}  {DescribeStatus(status.GetValueOrDefault(list.Id))}");
        }
        if (config.BlockedDomains.Count + config.AllowedDomains.Count > 0)
        {
            Terminal.Line($"  Your rules: {config.BlockedDomains.Count} blocked, {config.AllowedDomains.Count} allowed");
        }

        Terminal.Line();
        Terminal.Heading("Network adapters");
        var adapters = SystemDns.FindAdapters(config.ExcludedAdapters);
        if (adapters.Count == 0) Terminal.Line("  No connected adapters.");
        foreach (var adapter in adapters)
        {
            var dns = adapter.DnsServers.Count > 0 ? string.Join(", ", adapter.DnsServers) : "none";
            Terminal.Line($"  {adapter.Name}: DNS {dns}");
        }
        Terminal.Line($"  Allowed lookups go to: {string.Join(", ", config.Upstream)}");
        if (config.ListenOnLan)
        {
            var own = LocalNetwork.OwnAddresses();
            Terminal.Line($"  Other devices can use: {(own.Count > 0 ? string.Join(", ", own) : "no network address yet")}");
        }

        PrintBrowserProblems();
        return 0;
    }

    private static void PrintProtectionState()
    {
        var service = ServiceManager.Status();
        var stats = Stats.Read(Paths.StatsFile);
        var live = stats?.IsLive == true;
        var redirected = new SystemDns(Paths).IsApplied;

        string state;
        if (service is ServiceControllerStatus.Running && live) state = "ON (Windows service)";
        else if (service is ServiceControllerStatus.Running) state = "starting (Windows service)";
        else if (live) state = "ON (running in a console window)";
        else state = "OFF";

        Terminal.Pairs(
            ("Blocking", state),
            ("Service", service switch
            {
                null => "not installed",
                ServiceControllerStatus.Running => "running, starts with Windows",
                var other => other.Value.ToString().ToLowerInvariant(),
            }));

        if (redirected && !live && service is not ServiceControllerStatus.Running)
        {
            Terminal.Warn("  Network adapters still point at AdBlocker, which is not running, so websites may not load.");
            Terminal.Warn("  Fix it with \"adblocker restore\" (as administrator) or start AdBlocker again.");
        }

        if (Updates.Read(Paths)?.Version is { } newer && Updates.IsNewer(newer, AppInfo.Version))
        {
            Terminal.Line();
            Terminal.Heading($"  AdBlocker {newer} is available. Run \"adblocker upgrade\" as administrator to install it.");
        }
    }

    private static int Doctor(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        var problems = 0;
        void Problem(string text)
        {
            problems++;
            Terminal.Warn($"- {text}");
        }

        Terminal.Heading("Checking for things that let ads through...");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Problem("This Windows version is too old. AdBlocker needs Windows 10 version 2004 or later.");
        }

        foreach (var text in BrowserCheck.Problems()) Problem(text);

        var config = ConfigStore.Load(Paths);
        var live = Stats.Read(Paths.StatsFile)?.IsLive == true;
        foreach (var adapter in SystemDns.FindAdapters(config.ExcludedAdapters))
        {
            var ipv4 = SystemDns.ReadStaticServers(adapter.Id, ipv6: false);
            var api = SystemDns.ReadServersWithApi(adapter.Id, ipv6: false);
            if (api is not null && !SystemDns.SameServers(api, ipv4))
            {
                Problem($"{adapter.Name}: Windows reports DNS setting \"{api}\" but the registry has \"{ipv4}\".");
            }
            if (live && !SystemDns.IsLoopbackSetting(ipv4))
            {
                Problem($"{adapter.Name} does not use AdBlocker for DNS, so ads can get through it. Restart AdBlocker to fix this.");
            }
        }

        if (!live && IsPortTaken(53))
        {
            Problem("Another program is using DNS port 53, so AdBlocker cannot start. Mobile hotspot and Internet Connection Sharing do this.");
        }

        if (config.ListenOnLan && !Firewall.Exists())
        {
            Problem("AdBlocker is set to answer other devices, but the firewall rule is missing, so they cannot reach it. " +
                    "Run \"adblocker lan on\" as administrator.");
        }

        if (live && !LocalApi.IsAnsweringAsync(LocalApi.DefaultPort).GetAwaiter().GetResult())
        {
            Problem($"The browser companion extension can't reach AdBlocker on 127.0.0.1:{LocalApi.DefaultPort}, so it can't close ad tabs. Another program may be using that port.");
        }

        if (problems == 0) Terminal.Ok("No problems found.");
        return problems == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------------
    // Updating
    // ----------------------------------------------------------------------------------------------

    private static async Task<int> Upgrade(string[] args)
    {
        var options = new Arguments(args, [], ["--check"]);
        options.NoMoreThan(0);

        using var http = FilterLists.CreateDownloadClient();
        Terminal.Line("Looking for a newer version...");
        Release? release;
        try
        {
            release = await Updates.CheckAsync(http, AppInfo.Version, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new UserError($"Could not ask GitHub for the latest version: {e.Message}");
        }

        if (release is null)
        {
            Terminal.Ok($"AdBlocker {AppInfo.Version} is the latest version.");
            return 0;
        }

        Terminal.Heading($"AdBlocker {release.Version} is available. You have {AppInfo.Version}.");
        Terminal.Dim($"Release notes: {release.PageUrl}");
        if (options.Flag("--check"))
        {
            Terminal.Line("Run \"adblocker upgrade\" as administrator to install it.");
            return 0;
        }

        Elevation.Require("upgrade");
        if (ServiceManager.Status() is null)
        {
            throw new UserError("AdBlocker is not installed as a service here, so there is nothing to replace. " +
                                "Download the new version yourself and run \"adblocker install\".");
        }

        var directory = Path.Combine(Path.GetTempPath(), "AdBlocker-update");
        var lastReported = 0;
        var downloaded = await Updates.DownloadAsync(release, http, directory, percent =>
        {
            if (percent < lastReported + 25 && percent != 100) return;
            lastReported = percent;
            Terminal.Dim($"  downloading... {percent}%");
        }, CancellationToken.None);
        Terminal.Ok("Downloaded, and the checksum matches the one published with the release.");

        // The new executable stops the service, copies itself over the installed copy and starts again.
        Terminal.Line($"Installing {release.Version}...");
        try
        {
            using var install = Process.Start(new ProcessStartInfo(downloaded, "install") { UseShellExecute = false })
                                ?? throw new UserError("Could not start the downloaded version.");
            await install.WaitForExitAsync();
            if (install.ExitCode != 0) throw new UserError($"The new version could not install itself (exit code {install.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            // AdBlocker is not code-signed, so Smart App Control or SmartScreen can refuse to run it.
            throw new UserError(
                $"Windows would not run the downloaded file: {e.Message}\n" +
                $"It is kept at {downloaded}. You can run it yourself with \"install\", or download the new version from\n" +
                $"{release.PageUrl} and run \"adblocker install\" as administrator.");
        }
        TryDelete(directory);
        Terminal.Ok($"AdBlocker {release.Version} is installed and blocking.");
        return 0;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Terminal.Dim($"You can delete {directory} when you like.");
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Lists and rules
    // ----------------------------------------------------------------------------------------------

    private static int Lists(string[] args)
    {
        var options = new Arguments(args, [], []);
        var action = options.Positional.FirstOrDefault()?.ToLowerInvariant();
        switch (action)
        {
            case null:
                options.NoMoreThan(0);
                PrintLists();
                return 0;
            case "enable" or "disable":
                options.NoMoreThan(2);
                SetListEnabled(options.Required(1, "list id"), action == "enable");
                return 0;
            case "add":
                options.NoMoreThan(3);
                return AddList(options.Required(1, "list URL"), options.Positional.ElementAtOrDefault(2));
            case "remove":
                options.NoMoreThan(2);
                RemoveList(options.Required(1, "list id"));
                return 0;
            default:
                throw new UserError($"Unknown lists command \"{action}\". Use enable, disable, add or remove.");
        }
    }

    private static void PrintLists()
    {
        var config = ConfigStore.Load(Paths);
        var status = new FilterLists(Paths).LoadStatus();
        var rows = FilterLists.BuiltIn
            .Select(l => new[] { l.Id, FilterLists.IsEnabled(config, l) ? "on" : "off", l.Title, $"built-in, {FilterLists.CountDomains(l.Id):N0} domains" })
            .Concat(FilterLists.CommunityLists(config)
                .Select(l => new[] { l.Id, l.Enabled ? "on" : "off", l.Title, DescribeStatus(status.GetValueOrDefault(l.Id)) }))
            .ToList();
        Terminal.Table(["ID", "", "LIST", "DETAILS"], rows);
        Terminal.Line();
        Terminal.Dim("Turn lists on or off with \"adblocker lists enable <id>\" / \"disable <id>\" (as administrator).");
    }

    private static void SetListEnabled(string id, bool enabled)
    {
        Elevation.Require($"lists {(enabled ? "enable" : "disable")}");
        var builtIn = FilterLists.BuiltIn.FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var config = ConfigStore.Load(Paths);
        var community = FilterLists.CommunityLists(config).FirstOrDefault(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (builtIn is null && community is null) throw new UserError($"There is no list \"{id}\". Run \"adblocker lists\" to see them.");

        ConfigStore.Update(Paths, c =>
        {
            if (builtIn is not null)
            {
                c.Lists[builtIn.Id] = enabled;
                return;
            }
            var setting = c.CommunityLists.Find(s => s.Id.Equals(community!.Id, StringComparison.OrdinalIgnoreCase));
            if (setting is null) c.CommunityLists.Add(new CommunityListSetting { Id = community!.Id, Enabled = enabled });
            else setting.Enabled = enabled;
        });

        var title = builtIn?.Title ?? community!.Title;
        Terminal.Ok($"{title} is {(enabled ? "on" : "off")}.");
        if (enabled && community is not null && !File.Exists(new FilterLists(Paths).CachedFile(community.Id)))
        {
            DownloadNow(community with { Enabled = true });
        }
        PrintChangeNote();
    }

    private static int AddList(string url, string? title)
    {
        Elevation.Require("lists add");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UserError($"\"{url}\" is not an https:// address.");
        }
        var config = ConfigStore.Load(Paths);
        if (FilterLists.CommunityLists(config).Any(l => l.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UserError("That list is already added.");
        }

        var id = $"custom-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var list = new CommunityList(id, title ?? uri.Host, url, "Added by you", Enabled: true, Custom: true);
        var result = DownloadNow(list);
        if (result.Error is not null) return 1;

        ConfigStore.Update(Paths, c => c.CommunityLists.Add(new CommunityListSetting { Id = id, Title = list.Title, Url = url, Enabled = true }));
        Terminal.Ok($"Added {list.Title} as \"{id}\".");
        PrintChangeNote();
        return 0;
    }

    private static void RemoveList(string id)
    {
        Elevation.Require("lists remove");
        var removed = 0;
        ConfigStore.Update(Paths, c => removed = c.CommunityLists.RemoveAll(s => s.Url is not null && s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        if (removed == 0)
        {
            throw new UserError($"\"{id}\" is not a list you added. Built-in and preset lists can only be turned off.");
        }
        new FilterLists(Paths).DeleteCached(id.ToLowerInvariant());
        Terminal.Ok($"Removed {id}.");
        PrintChangeNote();
    }

    private static async Task<int> Update(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        Elevation.Require("update");
        var lists = FilterLists.CommunityLists(ConfigStore.Load(Paths)).Where(l => l.Enabled).ToList();
        if (lists.Count == 0)
        {
            Terminal.Line("No community lists are turned on.");
            return 0;
        }
        var failed = 0;
        foreach (var list in lists)
        {
            if (await DownloadAsync(list) is { Error: not null }) failed++;
        }
        PrintChangeNote();
        return failed == 0 ? 0 : 1;
    }

    private static ListStatus DownloadNow(CommunityList list) => DownloadAsync(list).GetAwaiter().GetResult();

    private static async Task<ListStatus> DownloadAsync(CommunityList list)
    {
        Paths.EnsureCreated();
        using var http = FilterLists.CreateDownloadClient();
        Terminal.Line($"Downloading {list.Title}...");
        var result = await new FilterLists(Paths).DownloadAsync(list, http, CancellationToken.None);
        if (result.Error is null) Terminal.Ok($"  {result.Domains:N0} domains");
        else Terminal.Error($"  Failed: {result.Error}");
        return result;
    }

    private static int AddRule(string[] args, bool allow)
    {
        var options = new Arguments(args, [], []);
        options.NoMoreThan(1);
        var input = options.Required(0, "domain");
        var domain = RuleParser.NormalizeDomain(input) ?? throw new UserError($"\"{input}\" is not a domain like ads.example.com.");
        Elevation.Require(allow ? "allow" : "block");

        ConfigStore.Update(Paths, c =>
        {
            c.BlockedDomains.RemoveAll(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
            c.AllowedDomains.RemoveAll(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
            (allow ? c.AllowedDomains : c.BlockedDomains).Add(domain);
            c.AllowedDomains.Sort(StringComparer.Ordinal);
            c.BlockedDomains.Sort(StringComparer.Ordinal);
        });
        Terminal.Ok(allow
            ? $"{domain} and its subdomains will never be blocked."
            : $"{domain} and its subdomains will always be blocked.");
        PrintChangeNote();
        return 0;
    }

    private static int ForgetRule(string[] args)
    {
        var options = new Arguments(args, [], []);
        options.NoMoreThan(1);
        var input = options.Required(0, "domain");
        var domain = RuleParser.NormalizeDomain(input) ?? input.Trim().ToLowerInvariant();
        Elevation.Require("forget");

        var removed = 0;
        ConfigStore.Update(Paths, c =>
        {
            removed += c.BlockedDomains.RemoveAll(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
            removed += c.AllowedDomains.RemoveAll(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
        });
        if (removed == 0) throw new UserError($"You have no rule for {domain}.");
        Terminal.Ok($"Removed your rule for {domain}; the filter lists decide again.");
        PrintChangeNote();
        return 0;
    }

    private static int Check(string[] args)
    {
        var options = new Arguments(args, [], []);
        options.NoMoreThan(1);
        var input = options.Required(0, "domain");
        var domain = (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : input).Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.Length == 0) throw new UserError("Missing domain.");

        var config = ConfigStore.Load(Paths);
        var lists = new FilterLists(Paths);
        var match = lists.BuildMatcher(config).Match(domain);
        if (match.Rule is null)
        {
            Terminal.Ok($"{domain} is not blocked.");
            return 0;
        }

        var sources = lists.FindSources(config, match.Rule, allow: !match.Blocked);
        var where = sources.Count > 0 ? $" by {string.Join(", ", sources)}" : "";
        var rule = match.Rule == domain ? domain : $"{match.Rule} (which covers {domain})";
        if (match.Blocked)
        {
            Terminal.Warn($"{domain} is BLOCKED: {rule} is listed{where}.");
            Terminal.Dim($"To allow it: adblocker allow {domain}");
        }
        else
        {
            Terminal.Ok($"{domain} is allowed: {rule} is an exception{where}.");
        }
        return 0;
    }

    private static int Dns(string[] args)
    {
        var options = new Arguments(args, [], []);
        var config = ConfigStore.Load(Paths);
        if (options.Positional.Count == 0)
        {
            Terminal.Pairs(("Setting", string.Join(" ", config.Upstream)));
            var automatic = new SystemDns(Paths).OriginalServers(config.ExcludedAdapters);
            using var http = DohUpstream.CreateClient();
            var servers = UpstreamSettings.Create(config.Upstream, automatic, http);
            Terminal.Pairs(("Servers", string.Join(", ", servers.Select(s => s.Name))));
            Terminal.Line();
            Terminal.Dim($"Change it with \"adblocker dns <server>...\": auto, {string.Join(", ", UpstreamSettings.Presets.Keys)}, an IP address or an https:// URL.");
            return 0;
        }

        var entries = options.Positional.ToList();
        foreach (var entry in entries)
        {
            if (UpstreamSettings.Validate(entry) is { } problem) throw new UserError(problem);
        }
        Elevation.Require("dns");
        ConfigStore.Update(Paths, c => c.Upstream = entries);
        Terminal.Ok($"Allowed lookups now go to: {string.Join(" ", entries)}");
        PrintChangeNote();
        return 0;
    }

    /// <summary>Serves DNS to phones and other devices on the local network, or stops doing so.</summary>
    private static int Lan(string[] args)
    {
        var options = new Arguments(args, [], []);
        options.NoMoreThan(1);
        var config = ConfigStore.Load(Paths);
        var action = options.Positional.FirstOrDefault()?.ToLowerInvariant();
        if (action is null)
        {
            PrintLanState(config.ListenOnLan);
            Terminal.Dim("Change it with \"adblocker lan on\" or \"adblocker lan off\" (as administrator).");
            return 0;
        }
        if (action is not ("on" or "off")) throw new UserError($"Unknown option \"{action}\". Use \"adblocker lan on\" or \"adblocker lan off\".");

        var wanted = action == "on";
        Elevation.Require($"lan {action}");
        ConfigStore.Update(Paths, c => c.ListenOnLan = wanted);
        if (wanted) Firewall.Allow(53); else Firewall.Remove();

        if (ServiceManager.IsRunning)
        {
            Terminal.Line("Restarting AdBlocker so it listens differently...");
            ServiceManager.Stop();
            StartAndConfirm();
        }
        PrintLanState(wanted);
        if (wanted)
        {
            Terminal.Line();
            Terminal.Line("On the phone: Wi-Fi settings, this network, set DNS to manual and enter the address above.");
            Terminal.Dim("Ads are only blocked while this PC is on and the phone is on this network.");
        }
        return 0;
    }

    private static void PrintLanState(bool on)
    {
        if (!on)
        {
            Terminal.Ok("AdBlocker only answers this PC.");
            return;
        }
        var addresses = LocalNetwork.OwnAddresses();
        Terminal.Ok("AdBlocker answers other devices on your network.");
        Terminal.Pairs(("This PC", addresses.Count > 0 ? string.Join(", ", addresses) : "no network address yet"));
        if (!Firewall.Exists())
        {
            Terminal.Warn("  The firewall rule is missing, so devices cannot reach it. Run \"adblocker lan on\" as administrator.");
        }
    }

    private static int ResetStats()
    {
        Elevation.Require("stats reset");
        Paths.EnsureCreated();
        if (Stats.Read(Paths.StatsFile) is { IsLive: true })
        {
            // The running blocker owns the counters; it resets them when it sees this file.
            File.WriteAllText(Stats.ResetMarker(Paths), "");
        }
        else
        {
            var stats = new Stats(Paths.StatsFile);
            stats.Reset();
            stats.Save(running: false);
        }
        Terminal.Ok("Statistics reset.");
        return 0;
    }

    private static async Task<int> Log(string[] args)
    {
        var options = new Arguments(args, valued: ["-n", "--lines"], flags: ["-f", "--follow"]);
        options.NoMoreThan(0);
        var count = options.Int("-n", options.Int("--lines", 30, 1, 100_000), 1, 100_000);
        var path = Paths.QueryLog;
        if (!File.Exists(path))
        {
            Terminal.Line("Nothing has been blocked yet.");
            if (!options.Flag("-f", "--follow")) return 0;
        }

        long position = 0;
        if (File.Exists(path))
        {
            foreach (var line in ReadTail(path, count, out position)) PrintLogLine(line);
        }
        if (!options.Flag("-f", "--follow")) return 0;

        Terminal.Dim("Following new lookups. Press Ctrl+C to stop.");
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(500, stopping.Token);
                if (!File.Exists(path)) continue;
                var length = new FileInfo(path).Length;
                if (length < position) position = 0; // rotated
                if (length == position) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync() is { } line) PrintLogLine(line);
                position = stream.Position;
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static IEnumerable<string> ReadTail(string path, int count, out long end)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        end = stream.Length;
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == count) tail.Dequeue();
            tail.Enqueue(line);
        }
        return tail.ToList();
    }

    private static void PrintLogLine(string line)
    {
        if (line.Contains("  SCAM     ", StringComparison.Ordinal)) Terminal.Error(line);
        else if (line.Contains("  BLOCKED  ", StringComparison.Ordinal)) Terminal.Warn(line);
        else Terminal.Dim(line);
    }

    // ----------------------------------------------------------------------------------------------
    // Help
    // ----------------------------------------------------------------------------------------------

    private static int Help()
    {
        Terminal.Heading($"AdBlocker for Windows {AppInfo.Version}");
        Terminal.Line("Blocks ads and trackers in every browser and app on this PC by filtering DNS lookups.");
        Terminal.Line("""

            Usage: adblocker <command> [options]

            Start and stop (* = needs a terminal opened with "Run as administrator")
              install *              Install as a Windows service and start blocking
              uninstall [--purge] *  Stop blocking, restore network settings, remove the service
              start | stop | restart *
              run *                  Block ads only while this console window is open (Ctrl+C stops)
                  --verbose          Also show allowed lookups
                  --no-system-dns    Don't change network settings (for testing)
                  --port <n>         Listen on another port (only with --no-system-dns)
                  --lan              Also answer other devices on the local network

            Everyday
              status                 Is blocking on? Today's numbers, lists and adapters
              check <domain>         Is a domain blocked, and by which list?
              block <domain> *       Always block a domain and its subdomains
              allow <domain> *       Never block a domain and its subdomains
              forget <domain> *      Remove your own rule for a domain
              log [-n 30] [-f]       Show recently blocked domains (-f keeps following)

            Lists and DNS
              lists                  Show all filter lists
              lists enable <id> *    Turn a list on (community lists download right away)
              lists disable <id> *   Turn a list off
              lists add <url> [name] *   Add a hosts file or Adblock-style list by https:// URL
              lists remove <id> *    Remove a list you added
              update *               Download the latest community lists now
              lan [on|off] *         Also answer phones and other devices on your network
                                     (an iPhone can then use this PC as its DNS server)
              dns [server...] *      Show, or set, where allowed lookups go:
                                     auto (your network's DNS, the default), cloudflare, quad9,
                                     google (these three use encrypted DNS), an IP address or an https:// URL

            Updating
              upgrade [--check] *    Download the latest version from GitHub and install it
                                     (--check only reports whether one is available)

            Troubleshooting
              doctor                 Look for settings that let ads slip past AdBlocker
              restore *              Put the original network settings back after a crash
              stats reset *          Set the counters back to zero

            More
              support                Support development ☕ (opens Buy Me a Coffee)
            """);
        Terminal.Dim($"Settings and logs: {Paths.Root}");
        Terminal.Dim($"Browser companion for Chrome and Edge (closes blocked ad tabs): {CompanionUrl}");
        Terminal.Dim($"Source: {AppInfo.SourceUrl}");
        return 0;
    }

    private static int Support(string[] args)
    {
        new Arguments(args, [], []).NoMoreThan(0);
        Terminal.Line("Thank you for thinking of it! You can buy me a coffee here:");
        Terminal.Heading($"  {AppInfo.SupportUrl}");

        // An elevated terminal would start the browser with administrator rights; just show the link then.
        if (Elevation.IsAdministrator) return 0;
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.SupportUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Terminal.Dim("Could not open a browser; copy the link above instead.");
        }
        return 0;
    }

    private static int Version()
    {
        Terminal.Line(AppInfo.Version);
        return 0;
    }

    // ----------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------

    private static void PrintChangeNote()
    {
        if (ServiceManager.IsRunning || Stats.Read(Paths.StatsFile)?.IsLive == true)
        {
            Terminal.Dim("AdBlocker picks this up within a few seconds.");
        }
    }

    private static void PrintBrowserProblems()
    {
        var problems = BrowserCheck.Problems().ToList();
        if (problems.Count == 0) return;
        Terminal.Line();
        foreach (var problem in problems) Terminal.Warn(problem);
    }

    private static string DescribeStatus(ListStatus? status) => status switch
    {
        { Error: not null } => $"download failed: {status.Error}",
        { UpdatedAt: { } updated } => $"{status.Domains:N0} domains, updated {updated.LocalDateTime:d MMM HH:mm}",
        _ => "not downloaded yet",
    };

    private static string Percent(long part, long whole) =>
        whole == 0 ? "0%" : (part / (double)whole).ToString("P1", CultureInfo.InvariantCulture);

    private static bool IsPortTaken(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(e => e.Port == port);
    private static bool LaunchedByDoubleClick()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        try
        {
            // Started from Explorer, this process is the only one attached to its console.
            return NativeMethods.GetConsoleProcessList(new uint[2], 2) == 1;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }
}
