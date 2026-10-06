// ============================================================
// Events / TaskEventHub.cs
// 任务事件多播总线（10-07，Web Dashboard 第二消费者）。
//   · 旁路接线（非发布-订阅）：渲染器仍直接消费事件流，Program.cs 在
//     renderer.RenderEventAsync 之后再调 PublishAsync——450+ tests 零冲击；
//     hub 消费被 renderer 阻塞拖慢可接受（任务 ~20 事件）。
//   · 背压：每订阅者 BoundedChannel（容量 1000，FullMode=DropOldest）——
//     浏览器标签页后台 / JS 卡住时不占内存；TryWrite 在 DropOldest 下恒成功。
//   · 订阅返回 IDisposable 句柄（dispose 即退订）；无订阅者时 Publish 零开销。
// ============================================================
using System.Threading.Channels;
using Agent.Core.Models;

namespace Agent.Infrastructure.Events;

public sealed class TaskEventHub
{
    private sealed record Subscriber(Guid Id, Channel<AgentEvent> Channel);

    private readonly List<Subscriber> _subscribers = new();
    private readonly object _lock = new();

    /// <summary>订阅事件流。返回 (读取器, 退订句柄)；句柄 dispose 后不再接收。</summary>
    public (ChannelReader<AgentEvent> Reader, IDisposable Handle) Subscribe()
    {
        var channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 满则丢弃最旧，绝不阻塞/占内存
            SingleReader = true,
            SingleWriter = false,
        });
        Guid id;
        lock (_lock)
        {
            id = Guid.NewGuid();
            _subscribers.Add(new Subscriber(id, channel));
        }
        return (channel.Reader, new UnsubscribeHandle(this, id));
    }

    /// <summary>向全部订阅者发布一个事件（DropOldest 下 TryWrite 恒成功，不阻塞）。</summary>
    public void Publish(AgentEvent evt)
    {
        List<Subscriber> snapshot;
        lock (_lock) snapshot = _subscribers.ToList();
        foreach (var s in snapshot)
            s.Channel.Writer.TryWrite(evt);
    }

    private sealed class UnsubscribeHandle : IDisposable
    {
        private readonly TaskEventHub _hub;
        private readonly Guid _id;
        private int _disposed;

        public UnsubscribeHandle(TaskEventHub hub, Guid id) { _hub = hub; _id = id; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (_hub._lock)
                    _hub._subscribers.RemoveAll(s => s.Id == _id);
        }
    }
}
