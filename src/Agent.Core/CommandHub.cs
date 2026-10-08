namespace Agent.Core;

/// <summary>
/// 命令通道（10-07 webUI 交互）：终端输入与 Dashboard POST /api/commands 提交统一汇入同一队列，
/// CLI 主循环一次消费一条（一次一任务串行语义不变）。
/// 自实现有界队列（seq 递增 + 快照 + DropOldest 100）——Channel 无内建枚举，/api/queue 需要快照。
/// 任务取消：主循环任务开始时 AttachTaskCancellation（taskCts），web POST /api/cancel → CancelCurrentTask。
/// </summary>
public sealed class CommandHub
{
    /// <summary>队列条目：seq 提交序号（归属/位置）、Input（null = EOF/退出信号）、SubmittedAt 提交时间、Source 来源（terminal/web）。</summary>
    public readonly record struct QueuedCommand(int Seq, string? Input, DateTimeOffset SubmittedAt, string Source);

    private readonly object _lock = new();
    private readonly Queue<QueuedCommand> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private int _nextSeq;
    private CancellationTokenSource? _taskCts;   // 当前任务的取消源（web 取消入口）

    /// <summary>提交一条命令（null = EOF，无头 stdin 关闭时由控制台线程写入）。有界 100：满时丢最旧（DropOldest）。返回 seq。</summary>
    public int TrySubmit(string? command, string source = "terminal")
    {
        lock (_lock)
        {
            var seq = ++_nextSeq;
            if (_queue.Count >= 100)
                _queue.Dequeue();   // 丢最旧（信号计数虚高由 NextAsync 空查循环容错，只多一次空唤醒）
            _queue.Enqueue(new QueuedCommand(seq, command, DateTimeOffset.Now, source));
            _signal.Release();
            return seq;
        }
    }

    /// <summary>取下一条命令：队列有数据立即返回，否则等待（终端输入 / web 提交唤醒）。取消 / EOF 返回 null。</summary>
    public async Task<QueuedCommand?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_queue.Count > 0)
                    return _queue.Dequeue();
            }
            try { await _signal.WaitAsync(ct); }
            catch (OperationCanceledException) { return null; }
        }
    }

    /// <summary>队列快照（按提交序；EOF 信号过滤）。/api/queue 数据源。</summary>
    public IReadOnlyList<QueuedCommand> Snapshot()
    {
        lock (_lock) return _queue.Where(q => q.Input is not null).ToArray();
    }

    public int Count
    {
        get { lock (_lock) return _queue.Count(q => q.Input is not null); }
    }

    // ── 任务取消（web POST /api/cancel → 当前任务 CancellationTokenSource.Cancel）──
    public void AttachTaskCancellation(CancellationTokenSource cts)
    {
        lock (_lock) _taskCts = cts;
    }

    public void DetachTaskCancellation()
    {
        lock (_lock) _taskCts = null;
    }

    public bool CancelCurrentTask()
    {
        CancellationTokenSource? cts;
        lock (_lock) cts = _taskCts;
        if (cts is null || cts.IsCancellationRequested) return false;
        cts.Cancel();
        return true;
    }

    // ── 当前任务归属（10-08 P1：SSE 混排的归属问题——前端显示"当前任务 = 哪条提交")──
    // 主循环任务开始时 AttachTaskInfo(seq, input)，finally Detach；GET /api/current-task 数据源。
    private int _currentSeq;
    private string? _currentInput;

    public void AttachTaskInfo(int seq, string input)
    {
        lock (_lock) { _currentSeq = seq; _currentInput = input; }
    }

    public void DetachTaskInfo()
    {
        lock (_lock) { _currentSeq = 0; _currentInput = null; }
    }

    public (int Seq, string? Input) CurrentTask
    {
        get { lock (_lock) return (_currentSeq, _currentInput); }
    }
}
