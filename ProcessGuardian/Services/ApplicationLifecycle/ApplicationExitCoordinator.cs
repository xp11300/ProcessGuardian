namespace ProcessGuardian.Services.ApplicationLifecycle;

/// <summary>Coordinates one idempotent, ordered application shutdown.</summary>
public sealed class ApplicationExitCoordinator(
    Action stopAcceptingOperations,
    Func<Task> waitForOperations,
    Func<Task> cleanup,
    Action shutdown)
{
    private readonly object _gate = new();
    private Task? _exitTask;
    private bool _exitRequested;

    public bool ExitRequested
    {
        get
        {
            lock (_gate)
            {
                return _exitRequested;
            }
        }
    }

    public Task ExitAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_exitTask is not null)
            {
                return _exitTask;
            }

            _exitRequested = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _exitTask = completion.Task;
        }

        _ = RunExitAsync(completion);
        return completion.Task;
    }

    private async Task RunExitAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            stopAcceptingOperations();
            await waitForOperations();
            await cleanup();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            shutdown();
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }
    }
}
