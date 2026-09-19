using System.Security.AccessControl;
using System.Security.Principal;

namespace AdBlocker.Config;

/// <summary>
/// Where AdBlocker keeps its files. Everything lives under %ProgramData%\AdBlocker so
/// the console and the Windows service share one configuration.
/// </summary>
/// <param name="restrictAccess">Lock a newly created folder down to SYSTEM and administrators (tests turn this off).</param>
public sealed class AppPaths(string root, bool restrictAccess = true)
{
    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdBlocker"));

    public string Root => root;
    public string ConfigFile => Path.Combine(root, "config.json");
    public string DnsStateFile => Path.Combine(root, "dns-backup.json");
    public string StatsFile => Path.Combine(root, "stats.json");
    public string UpdateFile => Path.Combine(root, "update.json");
    public string ListsDirectory => Path.Combine(root, "lists");
    public string ListStatusFile => Path.Combine(ListsDirectory, "status.json");
    public string LogsDirectory => Path.Combine(root, "logs");
    public string QueryLog => Path.Combine(LogsDirectory, "queries.log");
    public string ServiceLog => Path.Combine(LogsDirectory, "service.log");

    public bool Exists => Directory.Exists(root);

    /// <summary>
    /// Creates the folders. A new root only lets SYSTEM and administrators change
    /// anything, because the service (running as SYSTEM) acts on these files.
    /// </summary>
    public void EnsureCreated()
    {
        if (restrictAccess && !Directory.Exists(root))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        Directory.CreateDirectory(ListsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

internal static class AtomicFile
{
    /// <summary>Writes to a temporary file first so readers never see half a file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
