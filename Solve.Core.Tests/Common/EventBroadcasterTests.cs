using Solve.Core.Application.Common;

namespace Solve.Core.Tests.Common;

public sealed class EventBroadcasterTests
{
    private static List<int> Drain(EventSubscription<int> subscription)
    {
        var items = new List<int>();
        while (subscription.Reader.TryRead(out var item))
            items.Add(item);
        return items;
    }

    [Fact]
    public void EverySubscriber_ReceivesEveryEvent_InOrder()
    {
        var hub = new EventBroadcaster<int>(capacity: 8);
        using var a = hub.Subscribe();
        using var b = hub.Subscribe();

        hub.Publish(1);
        hub.Publish(2);

        Assert.Equal([1, 2], Drain(a));
        Assert.Equal([1, 2], Drain(b));
    }

    [Fact]
    public void SlowSubscriber_DropsOnlyItsOwnOldestEvents()
    {
        var hub = new EventBroadcaster<int>(capacity: 2);
        using var slow = hub.Subscribe();
        using var fast = hub.Subscribe();

        hub.Publish(1);
        Assert.Equal([1], Drain(fast));
        hub.Publish(2);
        Assert.Equal([2], Drain(fast));
        hub.Publish(3);

        Assert.Equal([2, 3], Drain(slow));
        Assert.Equal([3], Drain(fast));
    }

    [Fact]
    public async Task Dispose_Unsubscribes_AndCompletesReader()
    {
        var hub = new EventBroadcaster<int>(capacity: 4);
        var gone = hub.Subscribe();
        using var stays = hub.Subscribe();

        gone.Dispose();
        hub.Publish(7);

        Assert.Equal(1, hub.SubscriberCount);
        await gone.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([7], Drain(stays));
    }

    [Fact]
    public async Task Complete_CompletesCurrentReadersAfterDrain_AndLateSubscribers()
    {
        var hub = new EventBroadcaster<int>(capacity: 4);
        using var current = hub.Subscribe();
        hub.Publish(1);

        hub.Complete();
        hub.Complete();

        // 完成前已入队的事件仍可读完，读完后 Reader 才结束
        Assert.Equal([1], Drain(current));
        await current.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));

        using var late = hub.Subscribe();
        await late.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, hub.SubscriberCount);
    }
}
