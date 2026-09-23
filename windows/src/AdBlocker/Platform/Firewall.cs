using System.Diagnostics;

namespace AdBlocker.Platform;

/// <summary>
/// The inbound rule that lets other devices reach AdBlocker's DNS server. It is only added
/// for private and domain networks, so AdBlocker stays unreachable on public Wi-Fi even when
/// it is serving the local network.
/// </summary>
public static class Firewall
{
    public const string RuleName = "AdBlocker DNS";

    public static void Allow(int port)
    {
        Remove();
        foreach (var protocol in (string[])["UDP", "TCP"])
        {
            Netsh("advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=in", "action=allow",
                $"protocol={protocol}", $"localport={port}", "profile=private,domain",
                "description=Lets phones and other devices on your network use AdBlocker for DNS.");
        }
    }

    /// <summary>Removes the rule. Missing rules are not an error.</summary>
    public static void Remove() => Netsh("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");

    public static bool Exists() => Netsh("advfirewall", "firewall", "show", "rule", $"name={RuleName}") == 0;

    private static int Netsh(params string[] arguments)
    {
        var info = new ProcessStartInfo("netsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
