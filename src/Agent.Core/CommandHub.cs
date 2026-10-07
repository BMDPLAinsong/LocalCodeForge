using System.Threading.Channels;

namespace Agent.Core;

/// <summary>
/// 命令通道（10-07 webUI 交互）：终端输入与 Dashboard POST /api/commands 提交统一汇入同一通道，
/// CLI 主循环一次消费一条（一次一任务串行语义不变）。有界 100 条，满时丢最旧（DropOldest）。
/// </summary>
public sealed class CommandHub
{
    private readonly Channel<string?> _channel = Channel.CreateBounded<string?>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false,
    });

    /// <summary>提交一条命令（null 表示 EOF/退出信号，无头 stdin 关闭时由控制台线程写入）。</summary>
    public bool TrySubmit(string? command) => _channel.Writer.TryWrite(command);

    /// <summary>取下一条命令：通道有数据立即返回，否则等待（终端输入或 web 提交都会唤醒）。取消/EOF 返回 null。</summary>
    public async Task<string?> NextAsync(CancellationToken ct)
    {
        if (_channel.Reader.TryRead(out var item)) return item;
        try { return await _channel.Reader.ReadAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    public int Count => _channel.Reader.Count;
}
