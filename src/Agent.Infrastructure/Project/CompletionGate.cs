using System.IO;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 批次1·验证层总盘点：写任务完成门（确定性纯函数，无 LLM、无副作用）。
/// 让"写了文件"升级为"写到了用户要求的目标"：
///   - C. 写被拒未重试 → 失败（write/edit 被拒 ≥1 且本轮无任何写成功）
///   - B. 目标达成门（收尾时目标文件/目录不存在 → 失败，即使写过别处文件）
/// 供 AgentLoop 正常收尾 / 熔断收尾 与 Orchestrator 顶层失败统一调用。
/// </summary>
public static class CompletionGate
{
    /// <summary>
    /// 完成门判定结果。
    /// </summary>
    /// <param name="isWriteTask">输入是否含写语境（IntentOverride.IsWriteTask 或 HasWriteContext）。</param>
    /// <param name="wroteAny">本轮是否成功写入过文件（write_file/edit_file/append_file 任一成功）。</param>
    /// <param name="writeRejected">本轮写工具是否被拒过（越界/敏感/只读/项目文件/类型冲突等）。</param>
    /// <param name="target">提取到的显式写目标（TryGetWriteTarget 输出；无目标为 null）。</param>
    /// <param name="targetIsFile">目标是否为文件级（目录级按 Directory.Exists 验证）。</param>
    /// <returns>不阻断返回 null；阻断返回 (shouldFail, message, isSuspicious)。</returns>
    public static CompletionVerdict? Evaluate(
        bool isWriteTask,
        bool wroteAny,
        bool writeRejected,
        string? target,
        bool targetIsFile)
    {
        // 非写任务不过门（读任务/设计问答正常收尾）
        if (!isWriteTask) return null;

        // C：写被拒未重试 → 失败（拒绝≥1 且无任何写成功 = 任务根本没落地）
        if (writeRejected && !wroteAny)
        {
            return new CompletionVerdict(
                shouldFail: true,
                message: "写入被拒绝且本轮未成功写入任何文件（写目标未落地）。可检查路径是否越界/敏感，或改用工程内路径后重试。",
                isSuspicious: false);
        }

        // B：目标达成门——提取到显式目标但目标不存在 → 失败（写了别处 ≠ 写到目标）
        if (target is not null)
        {
            var exists = targetIsFile ? File.Exists(target) : Directory.Exists(target);
            if (!exists)
            {
                return new CompletionVerdict(
                    shouldFail: true,
                    message: $"任务要求写入 {target}，但该目标不存在（本轮写入位置与用户指定不符）。",
                    isSuspicious: true);
            }
        }

        return null;
    }
}

/// <summary>完成门判定结果：shouldFail + 失败消息 + 是否标可疑（目标不符 = 假完成嫌疑）。</summary>
public sealed record CompletionVerdict(bool shouldFail, string message, bool isSuspicious);
