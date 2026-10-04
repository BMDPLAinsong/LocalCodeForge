// ============================================================
// Rendering / PlainTextRenderer.cs
// 无头渲染器（管道 / 重定向 / E2E 脚本场景）：
//   逐事件 Console 输出，与旧 CLI 行为逐字节兼容（E2E 解析依赖）。
// ============================================================
using Agent.Core.Models;

namespace Agent.Infrastructure.Rendering;

public sealed class PlainTextRenderer : RendererBase
{
    public override Task RenderEventAsync(AgentEvent evt)
    {
        var lines = FormatEvent(evt).Split('\n');
        foreach (var line in lines)
        {
            switch (evt)
            {
                case AgentEvent.Routing or AgentEvent.PlanGenerated:
                    WithColor(ConsoleColor.DarkYellow, line);
                    break;
                case AgentEvent.FileWritten or AgentEvent.ToolCall:
                    WithColor(ConsoleColor.Cyan, line);
                    break;
                case AgentEvent.ToolResultEvent tr:
                    WithColor(tr.Success ? ConsoleColor.DarkGray : ConsoleColor.Red, line);
                    break;
                case AgentEvent.FinalAnswer:
                    WithColor(ConsoleColor.Green, line);
                    // 原 CLI：FinalAnswer 后输出一个空行（视觉分隔）
                    Console.WriteLine();
                    break;
                case AgentEvent.Error:
                    WithColor(ConsoleColor.Red, line);
                    break;
                default:
                    Console.WriteLine(line);
                    break;
            }
        }
        return Task.CompletedTask;
    }

    internal static void WithColor(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
