using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AdBlocker.Core;

namespace AdBlocker.Config;

/// <summary>
/// Where AdBlocker keeps its files. Everything lives under %ProgramData%\AdBlocker so
/// the console and the Windows service share one configuration.
/// </summary>
/// <param name="restrictAccess">
/// Lock a newly created folder down to SYSTEM and administrators, and refuse a folder someone
/// else created (tests turn this off).
/// </param>
public sealed class AppPaths(string root, bool restrictAccess = true)
{
    /// <summary>NT SERVICE\TrustedInstaller, which owns folders that come with Windows.</summary>
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AdBlocker"));

    public string Root => root;
    public string ConfigFile => Path.Combine(root, "config.json");
    public string DnsStateFile => Path.Combine(root, "dns-backup.json");
    public string StatsFile => Path.Combine(root, "stats.json");
    public string UpdateFile => Path.Combine(root, "update.json");

    /// <summary>Held by the blocker that manages the network settings (see <see cref="BlockerLock"/>).</summary>
    public string LockFile => Path.Combine(root, "blocker.lock");

    public string ListsDirectory => Path.Combine(root, "lists");
    public string ListStatusFile => Path.Combine(ListsDirectory, "status.json");
    public string LogsDirectory => Path.Combine(root, "logs");
    public string QueryLog => Path.Combine(LogsDirectory, "queries.log");
    public string ServiceLog => Path.Combine(LogsDirectory, "service.log");

    public bool Exists => Directory.Exists(root);

    /// <summary>
    /// Creates the folders. A new root only lets SYSTEM and administrators change anything,
    /// because the service (running as SYSTEM) acts on these files. For the same reason a root
    /// that someone else created, or a folder that links elsewhere, is refused: whoever made it
    /// could plant settings or redirect what the service writes.
    /// </summary>
    /// <exception cref="UserError">The folder can't be trusted.</exception>
    public void EnsureCreated()
    {
        if (restrictAccess)
        {
            if (!Directory.Exists(root)) new DirectoryInfo(root).Create(ProtectedSecurity());
            // Checked even when just created: someone may have created it first, and then Create
            // succeeds without applying anything.
            Refuse(root, checkOwner: true);
        }
        Directory.CreateDirectory(ListsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        if (restrictAccess)
        {
            Refuse(ListsDirectory, checkOwner: false);
            Refuse(LogsDirectory, checkOwner: false);
        }
    }

    /// <summary>Whether a folder owned by <paramref name="owner"/> can only have been made by Windows or an administrator.</summary>
    internal static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null
        && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || owner.Equals(TrustedInstaller));

    /// <summary>Throws a <see cref="UserError"/> if <paramref name="folder"/> is a link, or (for the root) not owned by an administrator.</summary>
    internal static void Refuse(string folder, bool checkOwner)
    {
        Problem? problem;
        try
        {
            problem = FindProblem(folder, checkOwner);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            problem = new($"could not be checked ({e.Message})");
        }
        if (problem is null) return;
        var message = $"{folder} {problem.Text}, so AdBlocker can't trust what is in it. " +
                      "Delete that folder as administrator and run the command again.";
        if (problem.WrongOwner)
        {
            // Where Windows makes the administrator who creates a folder its owner (a security
            // policy), an earlier version's own folder ends up here too.
            message += " If an earlier version of AdBlocker made it and you'd like to keep your settings, " +
                       $"give it to the Administrators group instead, in a terminal opened as administrator: icacls \"{folder}\" /setowner *S-1-5-32-544 /T";
        }
        throw new UserError(message);
    }

    private sealed record Problem(string Text, bool WrongOwner = false);

    private static Problem? FindProblem(string folder, bool checkOwner)
    {
        // First, because reading the owner would follow the link.
        if (File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint)) return new("is a link to another folder");
        if (!checkOwner) return null;
        var owner = OwnerOf(folder);
        return IsTrustedOwner(owner) ? null : new($"belongs to {Describe(owner)} rather than to the Administrators group", WrongOwner: true);
    }

    private static SecurityIdentifier? OwnerOf(string folder) =>
        new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    private static string Describe(SecurityIdentifier? owner)
    {
        if (owner is null) return "nobody";
        try
        {
            return owner.Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException)
        {
            return owner.Value; // an account that no longer exists, for example
        }
    }

    private static DirectorySecurity ProtectedSecurity()
    {
        var security = new DirectorySecurity();
        // Owned by administrators even where Windows would make the creating admin user the owner.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}

internal static class AtomicFile
{
    /// <summary>Writes to a temporary file first so readers never see half a file.</summary>
    /// <param name="durable">
    /// Flush to disk before the rename. Without it a power cut shortly after the write can leave
    /// the file empty; worth the wait for files that matter after a crash (config.json, dns-backup.json).
    /// </param>
    public static void WriteAllText(string path, string contents, bool durable = false)
    {
        // Unique, so two programs writing at once don't trip over each other's temporary file.
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(contents));
                if (durable) stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="path"/>. Someone reading the old file
    /// (the service, or a command showing the status) blocks that for a moment, so it is retried.
    /// </summary>
    public static void Replace(string source, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
