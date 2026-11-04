namespace ProcessGuardian.Models;

public sealed class ApplicationConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string SoftwareName { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Per-application opt-in (default off). When true, hiding the selected main window
    /// (for example a close button that only hides to the tray) is treated like closing it.
    /// Older config.json files without this field load as false.
    /// </summary>
    public bool TreatMainWindowHideAsClose { get; set; }
}
