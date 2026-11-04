using ProcessGuardian.ViewModels;

namespace ProcessGuardian.JobObjectTests;

internal sealed class FixedExecutableFilePicker(params string[] executablePaths) : IExecutableFilePicker
{
    private readonly Queue<string> _paths = new(executablePaths);

    public string? SelectExecutable() => _paths.Count == 0 ? null : _paths.Dequeue();
}
