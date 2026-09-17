namespace Ralven.App.Services;

internal static class TaskExtensions
{
    /// <summary>
    /// Marca uma tarefa deliberadamente não aguardada e observa sua exceção,
    /// para que uma falha em segundo plano seja engolida de forma explícita em
    /// vez de virar uma <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    public static void Forget(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
