// ============================================================
// Rendering / SpectreRenderer.cs
// 交互态渲染器（真实终端，非管道/重定向）：
//   · 启动横幅 / 配置块 → Panel 状态卡（AnsiConsole.Write 一次性输出，四边完整）
//   · 任务事件 → 【追加输出流】每收到一个事件直接 Write（关键事件=标题 Panel；
//     高频工具事件=彩色行）——不重绘、无帧叠加、无边框残留
//   · 命令块面板（stats/memory/rollback）→ Program.cs 交互分支调用（P2）
// 构造不碰 AnsiConsole——保证非终端环境可构造可测（RendererFactory.Create(true) 可测）。
//
// 【10-04 架构决策·诚实记录】LiveDisplay 已弃用：
//   任务事件是单向追加流（一条一条吐），不是"原地刷新的进度条"。Live 每次 Refresh
//   重绘整块区域，在 Windows 控制台下产生前后帧叠加——实测残留：漂浮竖线、绿框缺底边、
//   "任务完成"分隔线有起点无终点。对比证据：AnsiConsole.Write(Panel) 一次性输出
//   （LocalAI 横幅、配置框）四边完整无残留。→ 改用追加输出：一个事件一个渲染入口。
// ============================================================
using Agent.Core.Models;
using Spectre.Console;

namespace Agent.Infrastructure.Rendering;

public sealed class SpectreRenderer : RendererBase
{
    /// <summary>
    /// 任务状态前缀（交互态增强 10-07）：Program.cs 任务循环维护，格式 "[N步|service|秒] "。
    /// 渲染到高频工具事件行前，让"当前任务进度"随事件流始终可见（追加式终端内最接近常驻状态栏；
    /// 真常驻需全屏 TUI，Live 已实证失败不做）。PlainText 无头路径不使用。
    /// </summary>
    public string TaskPrefix { get; set; } = "";

    /// <summary>verbose 开关（--verbose 启动参数）：工具结果输出全文（默认截断 300，防刷屏）。</summary>
    public bool Verbose { get; set; } = false;

    private string Prefix() => string.IsNullOrEmpty(TaskPrefix) ? "" : $"[grey]{Markup.Escape(TaskPrefix)}[/] ";
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

    // ── 事件渲染：追加输出流（一个事件一个渲染入口，无重绘无残留） ──
    public override Task RenderEventAsync(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.Routing:
                // 关键节点（路由决策 / 完成 / 错误）用 Panel；其余高频事件用彩色行——防一次任务 20 个框
                WriteEventPanel(evt, "路由", Color.Yellow);
                break;
            case AgentEvent.PlanGenerated:
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(FormatEvent(evt))}[/]");
                break;
            case AgentEvent.FileWritten:
                AnsiConsole.MarkupLine($"{Prefix()}[aqua]{Markup.Escape(FormatEvent(evt))}[/]");
                break;
            case AgentEvent.ToolCall:
                AnsiConsole.MarkupLine($"{Prefix()}[cyan]{Markup.Escape(FormatEvent(evt))}[/]");
                break;
            case AgentEvent.ToolResultEvent tr:
                // verbose：输出结果全文（多行）；默认：截断 300 单行（防刷屏）
                if (Verbose && !string.IsNullOrEmpty(tr.Output))
                    AnsiConsole.MarkupLine($"{Prefix()}[{(tr.Success ? "grey" : "red")}][工具结果] {Markup.Escape(tr.Name)}:\n{Markup.Escape(tr.Output)}[/]");
                else
                    AnsiConsole.MarkupLine($"{Prefix()}[{(tr.Success ? "grey" : "red")}]{Markup.Escape(FormatEvent(evt))}[/]");
                break;
            case AgentEvent.FinalAnswer:
                WriteEventPanel(evt, "任务完成", Color.Green);
                Console.WriteLine();
                break;
            case AgentEvent.Error:
                WriteEventPanel(evt, "任务失败", Color.Red);
                Console.WriteLine();
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>关键事件 → 标题 Panel（.Expand() 撑满终端宽度，内容自动折行，边框按实际宽度画）</summary>
    private static void WriteEventPanel(AgentEvent evt, string title, Color border)
    {
        var panel = new Panel(new Markup(Markup.Escape(FormatEvent(evt))))
            .Header(title, Justify.Left)
            .BorderColor(border)
            .Expand()
            .Padding(1, 0, 1, 0);
        AnsiConsole.Write(panel);
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

    /// <summary>diff 红绿渲染（10-07，diff 命令）：unified hunk 头黄、删除行红、新增行绿、上下文灰。</summary>
    public void DiffBlock(Agent.Infrastructure.Diff.DiffResult result)
    {
        if (result.TooLarge)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.TooLargeReason ?? "文件过大，diff 不支持")}[/]");
            return;
        }
        if (result.Hunks.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]无差异[/]");
            return;
        }
        foreach (var h in result.Hunks)
        {
            AnsiConsole.MarkupLine($"[yellow]@@ -{h.OldStart},{h.OldCount} +{h.NewStart},{h.NewCount} @@[/]");
            foreach (var ln in h.Lines)
            {
                var sign = ln.Type switch
                {
                    Agent.Infrastructure.Diff.DiffLineType.Add => "+",
                    Agent.Infrastructure.Diff.DiffLineType.Remove => "-",
                    _ => " "
                };
                var color = ln.Type switch
                {
                    Agent.Infrastructure.Diff.DiffLineType.Add => "green",
                    Agent.Infrastructure.Diff.DiffLineType.Remove => "red",
                    _ => "grey"
                };
                var num = ln.Type switch
                {
                    Agent.Infrastructure.Diff.DiffLineType.Add => ln.NewNo.ToString(),
                    Agent.Infrastructure.Diff.DiffLineType.Remove => ln.OldNo.ToString(),
                    _ => $"{ln.OldNo} {ln.NewNo}"
                };
                AnsiConsole.MarkupLine($"[{color}]{sign} {num}| {Markup.Escape(ln.Text)}[/]");
            }
        }
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
