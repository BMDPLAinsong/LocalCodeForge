// ============================================================
// Rendering / SpectreRenderer.cs
// 交互态渲染器（真实终端，非管道/重定向）：
//   · 启动横幅 / 配置块 → Panel 状态卡
//   · 任务执行 → 彩色事件流（MarkupLine，文本格式与 FormatEvent 单一来源一致）
//     + 任务开始/结束 Panel 头尾
// 构造不碰 AnsiConsole——保证非终端环境可构造可测（RendererFactory.Create(true) 可测）。
// P0 边界：命令块（plan/memory/stats/rollback/use/健康检查）仍由 Program.cs 直出 Console。
// 【P1 待办·诚实记录】LiveDisplay 实时刷新区未落地：Spectre 0.49 的 LiveDisplay API 为
//   回调阻塞式（Start/StartAsync 必须在回调内自驱动工作），与内核"外部事件流驱动渲染"
//   模型不兼容；P1 方案 = 事件 Channel + 后台渲染线程 + LiveDisplay 阻塞回调消费队列，
//   或升级 Spectre 版本后重新评估。P0 以 Panel + 彩色事件流交付同等信息量。
// ============================================================
using Agent.Core.Models;
using Spectre.Console;

namespace Agent.Infrastructure.Rendering;

public sealed class SpectreRenderer : RendererBase
{
    private bool _taskActive;

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

    // ── 事件渲染：Panel 头尾 + 彩色事件行（文本与 FormatEvent 单一来源一致）──
    public override Task RenderEventAsync(AgentEvent evt)
    {
        var line = FormatEvent(evt);

        // 任务开始（首个非结束事件）：Panel 头
        if (!_taskActive && evt is not (AgentEvent.FinalAnswer or AgentEvent.Error))
        {
            AnsiConsole.Write(new Rule("[bold green]任务执行中[/]") { Style = Style.Parse("green") });
            _taskActive = true;
        }

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

        // 多行事件（PlanGenerated 含文件清单）逐行着色输出
        foreach (var l in line.Split('\n'))
            AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(l)}[/]");

        // 任务结束：Panel 尾 + 空行
        if (evt is AgentEvent.FinalAnswer or AgentEvent.Error)
        {
            AnsiConsole.Write(new Rule(evt is AgentEvent.FinalAnswer ? "[bold green]任务完成[/]" : "[bold red]任务失败[/]") { Style = Style.Parse(evt is AgentEvent.FinalAnswer ? "green" : "red") });
            Console.WriteLine();
            _taskActive = false;
        }

        return Task.CompletedTask;
    }
}
