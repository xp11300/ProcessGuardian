using System.IO;
using System.Text.Json;
using ProcessGuardian.Models;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.Services.Configuration;

public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private const int MaxCommitAttempts = 10;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorUnableToRemoveReplaced = 1175;
    private static readonly TimeSpan CommitRetryDelayStep = TimeSpan.FromMilliseconds(25);

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigurationService(string? configurationPath = null)
    {
        ConfigurationPath = configurationPath ?? Path.Combine(AppDataPaths.GetDataDirectory(), "config.json");
    }

    public string ConfigurationPath { get; }

    public async Task<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var failureStage = ConfigurationLoadFailureStage.Read;
        try
        {
            if (!File.Exists(ConfigurationPath))
            {
                failureStage = ConfigurationLoadFailureStage.CreateDefault;
                await SaveAsync([], cancellationToken);
                return new ConfigurationLoadResult([], WasCreated: true);
            }

            await using var stream = new FileStream(
                ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var applications = await JsonSerializer.DeserializeAsync<List<ApplicationConfiguration>>(
                stream,
                JsonOptions,
                cancellationToken);

            if (applications is null)
            {
                throw new JsonException("The configuration file did not contain an application list.");
            }

            var sanitized = Sanitize(applications);
            AppLog.Write(LogLevel.Information, "Configuration", $"Loaded {sanitized.Count} application configuration(s)");
            return new ConfigurationLoadResult(sanitized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write(LogLevel.Error, "Configuration", "Configuration load failed; original file was preserved", exception: exception);
            return new ConfigurationLoadResult([], exception, FailureStage: failureStage);
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<ApplicationConfiguration> applications,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applications);
        await _writeLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(ConfigurationPath)
                ?? throw new InvalidOperationException("The configuration path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"config.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, applications, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            await CommitTemporaryFileAsync(temporaryPath, cancellationToken);

            temporaryPath = null;
            AppLog.Write(LogLevel.Information, "Configuration", $"Saved {applications.Count} application configuration(s)");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Keep the original configuration intact if temporary cleanup fails.
                }
            }

            _writeLock.Release();
        }
    }

    // Antivirus or indexing filter drivers can briefly open a freshly written file without
    // FILE_SHARE_DELETE, so File.Replace fails transiently with ERROR_UNABLE_TO_REMOVE_REPLACED
    // or a sharing/lock violation. With no backup file, both files keep their names on these
    // errors, so a short bounded retry is safe; any other or persistent failure is rethrown.
    private async Task CommitTemporaryFileAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(ConfigurationPath))
                {
                    File.Replace(temporaryPath, ConfigurationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, ConfigurationPath);
                }

                return;
            }
            catch (IOException exception) when (attempt < MaxCommitAttempts && IsTransientCommitFailure(exception))
            {
                await Task.Delay(CommitRetryDelayStep * attempt, cancellationToken);
            }
        }
    }

    private static bool IsTransientCommitFailure(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation or ErrorUnableToRemoveReplaced;

    private static IReadOnlyList<ApplicationConfiguration> Sanitize(IEnumerable<ApplicationConfiguration> applications)
    {
        var ids = new HashSet<Guid>();
        var result = new List<ApplicationConfiguration>();
        foreach (var item in applications)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.SoftwareName)
                || string.IsNullOrWhiteSpace(item.ExecutablePath))
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }
}
