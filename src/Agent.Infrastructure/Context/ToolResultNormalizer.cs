// ============================================================
// Context / ToolResultNormalizer.cs
// 工具结果注入压缩统一入口（10-13 批次2：消"双注入点漂移"）。
//   · 此前三处各写各的截断：AgentLoop.SummarizeOutput（头尾+提示）、
//     ExecutorService ×2（裸 Substring 1500/2000，无提示）——同一策略三份实现，
//     折叠预算只加了主分支，其余两处无感知。
//   · 归一后：全注入点唯一截断实现 Truncate（含省略提示），各调用点只传各自
//     预算参数（预览 1500 / 修复 2000 / run_command 400/300 / read_file 6000/2000）
//     ——消实现漂移，保留语义差异（阈值本来就是场景语义，不该统一）。
//   纯函数，可单测。
// ============================================================

namespace Agent.Infrastructure.Context;

/// <summary>
/// 工具结果注入压缩器（确定性、零模型调用）。
/// </summary>
public static class ToolResultNormalizer
{
    /// <summary>头尾截断 + 省略提示。长度 ≤ head+tail 时原样返回（零开销）。
    /// tail=0 时仅保留头（Executor 预览/修复场景）。返回含总字符数提示，
    /// 让模型知道"这不是全文"（裸 Substring 会让 30B 误以为内容完整）。</summary>
    public static string Truncate(string output, int head = 400, int tail = 300)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= head + tail) return output;
        var sb = new System.Text.StringBuilder();
        sb.Append(output, 0, head);
        sb.Append("\n...(中间省略，共 ").Append(output.Length).Append(" 字符)...\n");
        if (tail > 0) sb.Append(output, output.Length - tail, tail);
        return sb.ToString();
    }
}
