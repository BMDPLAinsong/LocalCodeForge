// ============================================================
// Rendering / RendererFactory.cs
// 渲染器工厂（无头/交互选择，可测的确定性逻辑）：
//   interactive=false（管道输入 / 输出重定向 / 非交互环境）→ PlainTextRenderer
//   interactive=true（真实终端）→ SpectreRenderer
// 调用方（Program.cs）负责判定终端可用性：
//   !Console.IsInputRedirected && !Console.IsOutputRedirected
//
// 【10-04 根因修复·终端宽度】Spectre 在控制台下可能把终端宽度误算为 80 列，
//   导致宽内容（如 120+ 列的事件 Panel）边框错位、Rule 头尾字符（┌/└）被替换。
//   此处（Spectre 初始化处）显式写入实际窗口宽度（下限 80），配合关键 Panel 的
//   .Expand()（撑满宽度 + 内容自动折行）双保险。
// ============================================================
using Spectre.Console;

namespace Agent.Infrastructure.Rendering;

public static class RendererFactory
{
    public static IRenderer Create(bool interactive)
    {
        if (!interactive)
            return new PlainTextRenderer();

        // Spectre 显式终端宽度（初始化处）：防宽度误算导致的边框错位 / 头尾字符替换
        try
        {
            AnsiConsole.Profile.Width = Math.Max(80, Console.WindowWidth);
        }
        catch
        {
            // 非终端环境（无 Console.WindowWidth）：保留 Spectre 默认
        }

        return new SpectreRenderer();
    }
}
