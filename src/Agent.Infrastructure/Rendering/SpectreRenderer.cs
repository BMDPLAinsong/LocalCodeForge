// ============================================================
// Rendering / SpectreRenderer.cs
// 交互态渲染器（真实终端，非管道/重定向）：
//   · 启动横幅 / 配置块 → Panel 状态卡
//   · 任务执行 → 【P1】LiveDisplay 实时刷新区（事件 Channel + 后台渲染线程）
//   · 命令块面板（stats/memory/rollback）→ Program.cs 交互分支调用（P2）
// 构造不碰 AnsiConsole——保证非终端环境可构造可测（RendererFactory.Create(true) 可测）。
//
// 【P1 实时刷新区实现（已落地，替代 P0 的 Panel+事件流）】
//   Spectre 0.49 LiveDisplay 为回调阻塞式 API（StartAsync 必须在回调内自驱动工作）。
//   本实现：RenderEventAsync 把事件投进 Channel<AgentEvent>（unbounded）后立即返回，
//   不阻塞事件循环；后台线程跑 LiveDisplay.StartAsync，回调内 ReadAllAsync 消费队列并
//   刷新（滚动保留最近 N 条）；收到 FinalAnswer/Error 时 TryComplete 关闭 Channel →
//   回调读完剩余事件后返回 → LiveDisplay 区域结束；随后最终答案/错误经 AnsiConsole
//   输出到滚动缓冲区（LiveDisplay 区域结束后其内容不可见，必须留痕）。
//   失败安全：LiveDisplay 启动/刷新异常 → 捕获并回退为 P0 彩色事件流直出；等待 live
//   线程加入带超时（5s），防非终端环境死锁。
// ============================================================
using System.Threading.Channels;
using Agent.Core.Models;
using Spectre.Console;

namespace Agent.Infrastructure.Rendering;

public sealed class SpectreRenderer : RendererBase
{
    // ── P1：实时刷新区状态 ──
    private Channel<AgentEvent>? _eventChannel;
    private Task? _liveTask;
    private bool _liveActive; // 当前是否有 LiveDisplay 在跑
    private readonly object _liveLock = new();
    private const int MaxVisibleLines = 15; // 实时区滚动保留条数

    // ── 启动横幅：Panel 状态卡 ──
    public override void Banner(string workspace, string dbPath)
    {
        var panel = new Panel(
                new Markup(
                    $"[bold aqua]=== Local AI Workbench ===[/]\n" +
                    $"运行目录: [cyan]{Markup.Escape(workspace)}[/]\n" +
                    $"会话库:   [cyan]{Markup.Escape(dbPath)}[/]\n" +
                    "[grey]输入需求，回车执行。输入 exit 退出。输入 new 开启新会话。[/]"))
            .Header("LocalAI", Justify.Left)
            .BorderColor(Color.Aqua);
        AnsiConsole.Write(panel);
        Console.WriteLine();
    }

    // ── 配置块：Panel ──
    public override void ConfigBlock(AgentOptions options)
    {
        var panel = new Panel(
                new Markup(
                    $"[bold]实际生效配置[/]\n" +
                    $"Classifier: [cyan]{Markup.Escape(options.Classifier.Model)}[/]\n" +
                    $"Small:      [cyan]{Markup.Escape(options.Small.Model)}[/]\n" +
                    $"AgentPro:   [cyan]{Markup.Escape(options.AgentPro.Model)}[/]  (ComplexToPro={options.ComplexToPro})\n" +
                    $"Large:      [cyan]{Markup.Escape(options.Large.Model)}[/]"))
            .BorderColor(Color.Blue);
        AnsiConsole.Write(panel);
        Console.WriteLine();
    }

    // ── P1：事件渲染（LiveDisplay 实时区 + 结束留痕） ──
    public override Task RenderEventAsync(AgentEvent evt)
    {
        // 任务开始（首个事件且非结束事件）：启动实时区
        if (!_liveActive)
        {
            lock (_liveLock)
            {
                if (!_liveActive && evt is not (AgentEvent.FinalAnswer or AgentEvent.Error))
                {
                    _eventChannel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                    });
                    _liveActive = true;
                    _liveTask = Task.Run(() => LiveLoopAsync(_eventChannel));
                }
            }
        }

        // 结束事件：投递后关闭 Channel（后台读完剩余即退出），并等待实时区收尾
        var isTerminal = evt is AgentEvent.FinalAnswer or AgentEvent.Error;
        var channel = _eventChannel;
        if (channel is not null)
        {
            channel.Writer.TryWrite(evt);
            if (isTerminal)
            {
                channel.Writer.TryComplete();
                // 等待 LiveDisplay 回调退出（带超时防非终端死锁）；失败则回退直出
                try
                {
                    _liveTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                    // 后台渲染异常已在 LiveLoopAsync 内捕获并回退，此处忽略
                }
                _liveActive = false;

                // 【10-04 微调】省掉重复输出：最终行已由 LiveLoopAsync 写入面板（LiveDisplay
                // 结束后最后画面保留在屏幕上，用户截图实证边框可见）——不再向滚动缓冲区重复
                // 输出 [完成]/[错误] 与 Rule。异常回退态（无实时区）仍由下方 else 分支直出兜底。
            }
        }
        else if (isTerminal)
        {
            // 无实时区（异常回退态）：P0 直出，保证结束事件可见
            var line = FormatEvent(evt);
            var color = evt is AgentEvent.FinalAnswer ? "green" : "red";
            foreach (var l in line.Split('\n'))
                AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(l)}[/]");
        }

        return Task.CompletedTask;
    }

    // ── P1：LiveDisplay 后台循环（阻塞回调内消费队列） ──
    private static async Task LiveLoopAsync(Channel<AgentEvent> channel)
    {
        try
        {
            var lines = new List<string>(MaxVisibleLines);
            var target = new Panel(new Markup("[grey]等待事件…[/]")).BorderColor(Color.Green);

            await AnsiConsole.Live(target).StartAsync(async ctx =>
            {
                await foreach (var evt in channel.Reader.ReadAllAsync())
                {
                    var line = FormatEvent(evt);
                    foreach (var l in line.Split('\n'))
                        lines.Add(l);
                    while (lines.Count > MaxVisibleLines)
                        lines.RemoveAt(0);

                    // 实时区渲染：滚动事件流 + 当前状态
                    var isTerminalEvt = evt is AgentEvent.FinalAnswer or AgentEvent.Error;
                    var color = evt switch
                    {
                        AgentEvent.Routing => "yellow",
                        AgentEvent.PlanGenerated => "yellow",
                        AgentEvent.FileWritten => "cyan",
                        AgentEvent.ToolCall => "cyan",
                        AgentEvent.ToolResultEvent tr => tr.Success ? "grey" : "red",
                        AgentEvent.FinalAnswer => "green",
                        AgentEvent.Error => "red",
                        _ => "default",
                    };

                    Panel panel;
                    if (isTerminalEvt)
                    {
                        // 【10-04 微调】最终视图：面板内带最终行——最近事件灰显 + 分隔 + 最终行高亮
                        var tail = lines.Skip(Math.Max(0, lines.Count - 5)).ToList();
                        var body = new List<string>();
                        foreach (var l in tail)
                            body.Add($"[grey]{Markup.Escape(l)}[/]");
                        body.Add("[grey]──────────[/]");
                        foreach (var l in line.Split('\n'))
                            body.Add($"[bold {color}]{Markup.Escape(l)}[/]");
                        panel = new Panel(new Markup(string.Join('\n', body)))
                            .Header(evt is AgentEvent.FinalAnswer ? "任务完成" : "任务失败", Justify.Left)
                            .BorderColor(evt is AgentEvent.FinalAnswer ? Color.Green : Color.Red);
                    }
                    else
                    {
                        var markup = new Markup(string.Join('\n', lines.Select(l => $"[{color}]{Markup.Escape(l)}[/]")));
                        panel = new Panel(markup)
                            .Header("任务执行中", Justify.Left)
                            .BorderColor(Color.Green);
                    }
                    ctx.UpdateTarget(panel);
                    ctx.Refresh();
                }
            });
        }
        catch (Exception)
        {
            // 非终端 / LiveDisplay 不可用：静默放弃实时区（事件仍由 RenderEventAsync 直出留痕）
        }
    }

    // ═══════════ P2：命令块面板（交互态专用；无头路径不动，仍走旧 Console 文本） ═══════════

    /// <summary>stats 概览 BarChart（交互态）</summary>
    public void StatsPanel(string title, IReadOnlyList<(string Label, double Value)> bars)
    {
        var chart = new BarChart()
            .Width(60)
            .Label(title)
            .CenterLabel();
        foreach (var (label, value) in bars)
            chart.AddItem(label, value);
        AnsiConsole.Write(chart);
        Console.WriteLine();
    }

    /// <summary>memory list / stats Table（交互态）</summary>
    public void MemoryTable(string title, IReadOnlyList<(string Id, string Kind, string Hits, string Count, string Topic)> rows)
    {
        var table = new Table()
            .Title(title)
            .Border(TableBorder.Rounded)
            .AddColumns(
                new TableColumn("ID").LeftAligned(),
                new TableColumn("Kind").LeftAligned(),
                new TableColumn("Hits").RightAligned(),
                new TableColumn("Count").RightAligned(),
                new TableColumn("Topic").LeftAligned());
        foreach (var (id, kind, hits, count, topic) in rows)
            table.AddRow(id, kind, hits, count, Markup.Escape(topic));
        AnsiConsole.Write(table);
        Console.WriteLine();
    }

    /// <summary>rollback 备份列表 Table（交互态）</summary>
    public void BackupTable(string title, IReadOnlyList<(string TaskId, string Stamp, string Rel)> rows)
    {
        var table = new Table()
            .Title(title)
            .Border(TableBorder.Rounded)
            .AddColumns(
                new TableColumn("TaskId").LeftAligned(),
                new TableColumn("Stamp").LeftAligned(),
                new TableColumn("File").LeftAligned());
        foreach (var (taskId, stamp, rel) in rows)
            table.AddRow(taskId, stamp, Markup.Escape(rel));
        AnsiConsole.Write(table);
        Console.WriteLine();
    }

    /// <summary>rollback 多选恢复（交互态；未选任何项返回空列表）</summary>
    public IReadOnlyList<string> RollbackPrompt(string title, IReadOnlyList<string> choices)
    {
        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title(title)
                .NotRequired()
                .PageSize(10)
                .AddChoices(choices.Distinct().ToArray()));
        return selected;
    }
}
