// ============================================================
// Rendering / RendererFactory.cs
// 渲染器工厂（无头/交互选择，可测的确定性逻辑）：
//   interactive=false（管道输入 / 输出重定向 / 非交互环境）→ PlainTextRenderer
//   interactive=true（真实终端）→ SpectreRenderer
// 调用方（Program.cs）负责判定终端可用性：
//   !Console.IsInputRedirected && !Console.IsOutputRedirected
// ============================================================
namespace Agent.Infrastructure.Rendering;

public static class RendererFactory
{
    public static IRenderer Create(bool interactive)
        => interactive ? new SpectreRenderer() : new PlainTextRenderer();
}
