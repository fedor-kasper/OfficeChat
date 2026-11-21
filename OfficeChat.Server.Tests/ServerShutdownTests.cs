using System.Reflection;
using OfficeChat.Client;

namespace OfficeChat.Server.Tests;

public class ServerShutdownTests
{
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _actions = new();
        public override void Post(SendOrPostCallback callback, object? state) =>
            _actions.Enqueue(() => callback(state));
        public void Drain()
        {
            while (_actions.TryDequeue(out var action)) action();
        }
    }

    private static ServerConnection Create(QueuedContext context)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            return new ServerConnection(new ServerApi("localhost", 1, "", null), () => 0, _ => Task.CompletedTask);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_notification_is_suppressed_only_after_shutdown(bool shutdown)
    {
        var context = new QueuedContext();
        await using var connection = Create(context);
        var calls = 0;
        var post = typeof(ServerConnection).GetMethod("Post", BindingFlags.Instance | BindingFlags.NonPublic)!;
        post.Invoke(connection, new object[] { (Action)(() => calls++) });
        if (shutdown) await connection.DisposeAsync();
        context.Drain();
        Assert.Equal(shutdown ? 0 : 1, calls);
    }

    [Fact]
    public async Task Queued_database_read_is_cancelled_without_waiting_for_ui()
    {
        var context = new QueuedContext();
        await using var connection = Create(context);
        var calls = 0;
        var invoke = typeof(ServerConnection).GetMethod("InvokeOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(long));
        var pending = (Task<long>)invoke.Invoke(connection, new object[] { (Func<long>)(() => ++calls) })!;
        await connection.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        context.Drain();
        Assert.Equal(0, calls);
    }
}
