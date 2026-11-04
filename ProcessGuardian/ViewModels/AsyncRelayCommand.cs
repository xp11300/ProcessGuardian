using System.Windows.Input;

namespace ProcessGuardian.ViewModels;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public Task ExecutionTask { get; private set; } = Task.CompletedTask;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return Task.CompletedTask;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        ExecutionTask = ExecuteCoreAsync();
        return ExecutionTask;
    }

    private async Task ExecuteCoreAsync()
    {
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
