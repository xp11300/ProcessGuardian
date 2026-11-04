namespace ProcessGuardian.Services.ApplicationLifecycle;

public interface ITrayIconService : IDisposable
{
    event EventHandler? OpenRequested;

    event EventHandler? ApplicationsRequested;

    event EventHandler? ExitRequested;

    bool IsVisible { get; }
}
