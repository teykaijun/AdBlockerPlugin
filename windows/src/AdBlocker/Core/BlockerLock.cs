using AdBlocker.Config;

namespace AdBlocker.Core;

/// <summary>
/// Lets only one blocker manage the network settings at a time. The service and
/// "adblocker run" would otherwise fight over port 53 and over the adapters' DNS settings,
/// and one of them would restore settings the other still relies on.
/// </summary>
public static class BlockerLock
{
    private const string AlreadyRunning = "Another copy of AdBlocker is already running (the service or \"adblocker run\").";

    /// <summary>Takes the lock, held until the returned stream is disposed.</summary>
    /// <exception cref="UserError">Another blocker holds it.</exception>
    public static FileStream Acquire(AppPaths paths)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // The file only exists while a blocker holds it. A lock file left lying around could
                // be opened by any user who can read the folder, which would keep the service from
                // starting; nobody else can create one there.
                return new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException e) when (IsSharingViolation(e))
            {
                // A copy that is just stopping may need a moment to let go.
                if (attempt == 5) throw new UserError(AlreadyRunning, e);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Whether a blocker holds the lock right now, for example after the service was stopped and
    /// before network settings it left behind are restored. False when that can't be told.
    /// </summary>
    public static bool IsHeldElsewhere(AppPaths paths)
    {
        try
        {
            using var probe = new FileStream(paths.LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException e) when (IsSharingViolation(e))
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // no lock file yet, or not allowed to look
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION.</summary>
    private static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;
}
