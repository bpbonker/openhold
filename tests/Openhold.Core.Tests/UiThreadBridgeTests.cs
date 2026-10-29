using Openhold.Client.Sdk;

namespace Openhold.Core.Tests;

public class UiThreadBridgeTests
{
    /// <summary>
    /// A synchronization context like a CAD UI thread that is blocked inside a command: anything posted to it never runs.
    /// </summary>
    private sealed class BlockedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private static async Task<int> WorkThatAwaitsAsync()
    {
        await Task.Delay(50);
        await Task.Yield();
        return 42;
    }

    [Fact]
    public void Work_started_from_a_blocked_ui_thread_completes()
    {
        var result = 0;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedUiContext());
            result = UiThreadBridge.Run(WorkThatAwaitsAsync);
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "UiThreadBridge.Run deadlocked on a UI-style synchronization context.");
        Assert.Equal(42, result);
    }
}
