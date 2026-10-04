// ============================================================
// Rendering / RendererBase.cs
// 渲染基类：Console 直出实现（无头/默认路径，输出与重构前 CLI 逐字节兼容）
// + 纯函数 FormatEvent / Truncate / FormatToolArgs（唯一事件格式化来源，tests 直接断言）。
// 历史：原 Agent.Cli/Program.cs 的 RenderEvent(856-916) / Truncate(593) / FormatToolArgs(600)
//      搬迁至此（10-04 Spectre P0，消灭 CLI 单点、下沉可测层）。
// ============================================================
using Agent.Core.Models;
using System.Text.Json;

namespace Agent.Infrastructure.Rendering;

public abstract class RendererBase : IRenderer
{
    // ── 启动横幅（无色，与旧 CLI 输出逐字节一致）──
    public virtual void Banner(string workspace, string dbPath)
    {
        Console.WriteLine("=== Local AI Workbench ===");
        Console.WriteLine($"运行目录: {workspace}");
        Console.WriteLine($"会话库:   {dbPath}");
        Console.WriteLine("输入需求，回车执行。输入 exit 退出。输入 new 开启新会话。");
        Console.WriteLine();
    }

    // ── 实际生效配置块（无色）──
    public virtual void ConfigBlock(AgentOptions options)
    {
        Console.WriteLine("=== 实际生效配置 ===");
        Console.WriteLine($"Classifier: {options.Classifier.Model}");
        Console.WriteLine($"Small:      {options.Small.Model}");
        Console.WriteLine($"AgentPro:   {options.AgentPro.Model}   (ComplexToPro={options.ComplexToPro})");
        Console.WriteLine($"Large:      {options.Large.Model}");
        Console.WriteLine();
    }

    // ── 通用状态行（颜色语义映射，与原 Console.ForegroundColor 精确对应）──
    public virtual void Info(string text) { WithColor(ConsoleColor.DarkGray, text); }
    public virtual void Notice(string text) { WithColor(ConsoleColor.Magenta, text); }
    public virtual void Warn(string text) { WithColor(ConsoleColor.Yellow, text); }
    public virtual void Error(string text) { WithColor(ConsoleColor.Red, text); }
    public virtual void System(string text) { Console.WriteLine(text); }

    public virtual void Prompt() => Console.Write("> ");
    public virtual string? ReadLine() => Console.ReadLine();

    public abstract Task RenderEventAsync(AgentEvent evt);

    // ──────────────────────────────────────────────────────────
    // 纯函数区（tests 直接断言；Spectre 实现复用 FormatEvent 作历史行）
    // ──────────────────────────────────────────────────────────

    /// <summary>把 AgentEvent 格式化为一行（或数行）文本（无 ANSI，纯文本）。</summary>
    public static string FormatEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.Routing rt:
            {
                var intentText = string.IsNullOrEmpty(rt.Intent) ? "" : $" [{rt.Intent}]";
                return $"[路由] {rt.Tier}{intentText} → {rt.ServiceId}";
            }
            case AgentEvent.PlanGenerated pg:
            {
                var sb = new System.Text.StringBuilder();
                // 显式 '\n'（AppendLine 会引入环境相关 \r\n，破坏 tests 与跨平台断言）
                sb.Append($"[规划] {pg.Summary}（{pg.Language}，{pg.FilePaths.Count} 个文件）\n");
                foreach (var p in pg.FilePaths)
                    sb.Append($"  - {p}\n");
                return sb.ToString().TrimEnd('\n');
            }
            case AgentEvent.FileWritten fw:
                return $"[写入] {fw.Path}（{fw.Size} 字符）";
            case AgentEvent.ToolCall tc:
                return $"[工具调用] {tc.Name}  {FormatToolArgs(tc.Name, tc.Args)}";
            case AgentEvent.ToolResultEvent tr:
                return $"[工具结果] {tr.Name}: {Truncate(tr.Output, 300)}";
            case AgentEvent.FinalAnswer fa:
                return $"[完成] {fa.Text}";
            case AgentEvent.Error e:
                return $"[错误] {e.Message}";
            default:
                return $"[事件] {evt.GetType().Name}";
        }
    }

    /// <summary>字符串截断（原 Program.cs:593）</summary>
    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// 根据工具类型，智能格式化参数显示（原 Program.cs:600）。
    /// write_file 只显示 path 和 content 长度，不显示全文。
    /// </summary>
    public static string FormatToolArgs(string toolName, JsonElement args)
    {
        // 剥离工具名前缀：SK 会把 pluginName 拼到工具名前，形如 "agent_tools-list_dir" 或 "_-list_dir"
        var dashIndex = toolName.IndexOf('-');
        if (dashIndex > 0 && dashIndex < toolName.Length - 1)
        {
            toolName = toolName[(dashIndex + 1)..];
        }

        try
        {
            // write_file：显示 path + content 长度
            if (toolName.Contains("write_file"))
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() : "?";
                var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                return $"path=\"{path}\", content={content.Length} 字符";
            }

            // run_command：显示 command
            if (toolName.Contains("run_command"))
            {
                var cmd = args.TryGetProperty("command", out var c) ? c.GetString() : "?";
                return $"command=\"{cmd}\"";
            }

            // list_dir / read_file：显示 path
            if (toolName.Contains("list_dir") || toolName.Contains("read_file"))
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() : ".";
                return $"path=\"{path}\"";
            }

            // 其它工具：原样输出，超长截断
            var raw = args.ToString();
            return raw.Length > 150 ? raw[..150] + "..." : raw;
        }
        catch
        {
            var raw = args.ToString();
            return raw.Length > 150 ? raw[..150] + "..." : raw;
        }
    }

    private static void WithColor(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
