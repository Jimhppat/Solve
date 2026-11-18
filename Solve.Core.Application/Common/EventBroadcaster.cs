using System.Threading.Channels;

namespace Solve.Core.Application.Common;

/// <summary>
/// 一对多事件广播：每个订阅者拥有独立的有界队列，订阅者之间互不抢占事件、互不阻塞。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>发布：<see cref="Publish"/> 非阻塞、无 I/O，可在发布方的状态锁内调用以保证事件顺序。</item>
/// <item>背压：某个订阅者的队列满时只丢弃它自己最旧的事件，慢消费者不会拖慢发布方或其他订阅者。</item>
/// <item>生命周期：订阅者用完 Dispose 退订；<see cref="Complete"/> 后所有 Reader 在读完剩余事件后完成，之后的订阅直接得到已完成的 Reader。</item>
/// </list>
/// </remarks>
public sealed class EventBroadcaster<T>
{
    private readonly int _capacity;
    private readonly Lock _gate = new();

    // 写时复制：Publish 无锁遍历快照，订阅/退订在锁内替换数组
    private EventSubscription<T>[] _subscribers = [];
    private bool _completed;

    public EventBroadcaster(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int SubscriberCount => Volatile.Read(ref _subscribers).Length;

    public EventSubscription<T> Subscribe()
    {
        var subscription = new EventSubscription<T>(this, _capacity);
        lock (_gate)
        {
            if (!_completed)
            {
                _subscribers = [.. _subscribers, subscription];
                return subscription;
            }
        }

        subscription.Complete();
        return subscription;
    }

    public void Publish(T item)
    {
        foreach (var subscriber in Volatile.Read(ref _subscribers))
            subscriber.Offer(item);
    }

    /// <summary>
    /// 结束广播，幂等。
    /// </summary>
    public void Complete()
    {
        EventSubscription<T>[] subscribers;
        lock (_gate)
        {
            if (_completed)
                return;

            _completed = true;
            subscribers = _subscribers;
            _subscribers = [];
        }

        foreach (var subscriber in subscribers)
            subscriber.Complete();
    }

    internal void Remove(EventSubscription<T> subscription)
    {
        lock (_gate)
        {
            _subscribers = Array.FindAll(_subscribers, s => !ReferenceEquals(s, subscription));
        }
    }
}

/// <summary>
/// 一个订阅者的事件队列。Dispose 即退订，Reader 随之完成。
/// </summary>
public sealed class EventSubscription<T> : IDisposable
{
    private readonly EventBroadcaster<T> _owner;
    private readonly Channel<T> _channel;

    internal EventSubscription(EventBroadcaster<T> owner, int capacity)
    {
        _owner = owner;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<T> Reader => _channel.Reader;

    internal void Offer(T item) => _channel.Writer.TryWrite(item);

    internal void Complete() => _channel.Writer.TryComplete();

    public void Dispose()
    {
        _owner.Remove(this);
        Complete();
    }
}
