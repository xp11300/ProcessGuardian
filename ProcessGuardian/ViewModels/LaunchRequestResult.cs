using ProcessGuardian.Services.CommandLine;

namespace ProcessGuardian.ViewModels;

/// <summary>
/// Outcome of <see cref="MainViewModel.RequestLaunchAsync"/>. For Started, <see cref="LaunchTask"/> is the click
/// path's command task (already begun) and <see cref="Item"/> the entry; for AlreadyRunning,
/// <see cref="WindowHandle"/> is the entry's tracked main window (0 when there is none).
/// </summary>
internal sealed record LaunchRequestResult(
    CommandReplyCode Code,
    string Message,
    uint ProcessId = 0,
    long WindowHandle = 0,
    Task? LaunchTask = null,
    ApplicationItemViewModel? Item = null)
{
    internal CommandReply ToReply() => CommandReply.Create(Code, Message, Code == CommandReplyCode.AlreadyRunning ? WindowHandle : 0);
}
