using ProcessGuardian.Models;

namespace ProcessGuardian.Services.Configuration;

public sealed record ConfigurationLoadResult(
    IReadOnlyList<ApplicationConfiguration> Applications,
    Exception? Error = null,
    bool WasCreated = false,
    ConfigurationLoadFailureStage? FailureStage = null);

public enum ConfigurationLoadFailureStage
{
    Read,
    CreateDefault,
}
