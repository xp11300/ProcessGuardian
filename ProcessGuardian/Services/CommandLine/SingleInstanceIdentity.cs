using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ProcessGuardian.Services.CommandLine;

/// <summary>Names that scope single-instance behaviour to one effective data directory, user and session.</summary>
internal sealed record SingleInstanceNames(string DataDirectory, string Key, string MutexName, string PipeName)
{
    /// <summary>Uses the same data-directory rule as the app (AppDataPaths / PROCESSGUARDIAN_DATA_DIR). No file access.</summary>
    internal static SingleInstanceNames ForCurrentProcess()
    {
        var dataDirectory = AppDataPaths.GetDataDirectory();
        var key = SingleInstanceIdentity.ComputeKey(dataDirectory);
        string userSid;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            userSid = identity.User?.Value
                ?? throw new InvalidOperationException("The current Windows user has no SID; single-instance names cannot be built.");
        }

        int sessionId;
        using (var process = Process.GetCurrentProcess())
        {
            sessionId = process.SessionId;
        }

        return new SingleInstanceNames(
            dataDirectory,
            key,
            SingleInstanceIdentity.GetMutexName(key),
            SingleInstanceIdentity.GetPipeName(key, userSid, sessionId));
    }
}

internal static class SingleInstanceIdentity
{
    internal const int KeyLength = 16;

    /// <summary>Full path, trailing separators trimmed, upper-cased with the invariant culture.</summary>
    internal static string NormalizeDataDirectory(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return (trimmed.Length == 0 ? fullPath : trimmed).ToUpperInvariant();
    }

    /// <summary>First 16 hex characters of SHA-256 over the UTF-8 bytes of the normalized data directory.</summary>
    internal static string ComputeKey(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeDataDirectory(dataDirectory)));
        return Convert.ToHexString(hash)[..KeyLength];
    }

    internal static string GetMutexName(string key) => @"Local\ProcessGuardian." + key;

    internal static string GetPipeName(string key, string userSid, int sessionId) =>
        $"ProcessGuardian.{userSid}.{sessionId}.{key}";
}

/// <summary>
/// The per-data-directory mutex. It is held for the whole life of the first instance and released on the UI thread
/// after cleanup. An abandoned mutex (previous owner died) counts as acquired.
/// </summary>
internal sealed class SingleInstanceMutex : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceMutex(Mutex mutex, bool isOwner, bool wasAbandoned)
    {
        _mutex = mutex;
        IsOwner = isOwner;
        WasAbandoned = wasAbandoned;
    }

    internal bool IsOwner { get; private set; }

    internal bool WasAbandoned { get; }

    internal static SingleInstanceMutex Acquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool owned;
            var abandoned = false;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
                abandoned = true;
            }

            return new SingleInstanceMutex(mutex, owned, abandoned);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>Releases ownership (must run on the acquiring thread). Idempotent.</summary>
    internal void Release()
    {
        if (!IsOwner)
        {
            return;
        }

        IsOwner = false;
        _mutex.ReleaseMutex();
    }

    public void Dispose()
    {
        try
        {
            Release();
        }
        finally
        {
            _mutex.Dispose();
        }
    }
}
