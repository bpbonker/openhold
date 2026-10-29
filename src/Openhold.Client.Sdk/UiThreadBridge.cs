namespace Openhold.Client.Sdk;

/// <summary>
/// Runs Openhold's async work from a CAD command, which must return synchronously on the UI thread.
/// </summary>
/// <remarks>
/// Starting the work on the UI thread and then blocking on it deadlocks: the work's continuations are
/// posted back to the UI thread's synchronization context, which is busy waiting. Taking a factory
/// (not an already-started task) means the work starts on the thread pool, where nothing is captured.
/// </remarks>
public static class UiThreadBridge
{
    public static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();
}
