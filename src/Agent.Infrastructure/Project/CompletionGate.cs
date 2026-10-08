using System.Collections.Generic;
using System.IO;
using System.Linq;

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
    /// <param name="requirementTokens">需求内容 token（ContentConformance.ExtractRequirementTokens；空集跳过 D 门）。</param>
    /// <param name="writtenFiles">本轮成功写过的文件路径列表（内容符合性检查对象）。</param>
    /// <returns>不阻断返回 null；阻断返回 (shouldFail, message, isSuspicious)。</returns>
    public static CompletionVerdict? Evaluate(
        bool isWriteTask,
        bool wroteAny,
        bool writeRejected,
        string? target,
        bool targetIsFile,
        IReadOnlyList<string>? requirementTokens = null,
        IReadOnlyList<string>? writtenFiles = null)
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

        // B：目标达成门——提取到显式目标但"本轮未真正写到此目标" → 失败。
        // 【10-08 语义修正】判据从 File.Exists(target) 改为"target ∈ writtenFiles（本轮写入）"：
        // 实证——第一轮建了目标文件，第二轮模型写了任务目录同名文件，File.Exists=true 误放行。
        // 归一化比较（写入时按 baseRoot 归一化记录，与 WriteFileTool 逻辑对称）。
        if (target is not null)
        {
            var writtenThisRound = writtenFiles is not null &&
                                   writtenFiles.Any(f => PathsEqual(Path.GetFullPath(f), Path.GetFullPath(target)));
            if (writtenFiles is not null)
            {
                // 调用方已提供本轮写入记录 → 只认"本轮真写到了目标"
                if (!writtenThisRound)
                {
                    return new CompletionVerdict(
                        shouldFail: true,
                        message: $"任务要求写入 {target}，但本轮未写到此目标（写入位置与用户指定不符）。",
                        isSuspicious: true);
                }
            }
            else
            {
                // 调用方未提供记录（未来调用方）→ 回退存在性判据
                var exists = targetIsFile ? File.Exists(target) : Directory.Exists(target);
                if (!exists)
                {
                    return new CompletionVerdict(
                        shouldFail: true,
                        message: $"任务要求写入 {target}，但该目标不存在（本轮写入位置与用户指定不符）。",
                        isSuspicious: true);
                }
            }
        }

        // D：内容符合性门（2026-10-08 验证层补洞）
        // 任务输入含"用户显式要求写入的文本"（引号/反引号包裹）且本轮写过文件时，
        // 修改后的文件必须包含该内容——否则"编译通过"也会假成功（BigService 实证：
        // 模型把 x + 1500 改成 x + 15000 且漏加 // 已审查，dotnet run 通过被放行）。
        // 无需求 token / 未写文件 → 跳过（不误伤普通生成任务）。
        // 【10-08 扩展】有显式写目标时调用方已传 ExtractRequirementTokensExtended（含
        // "内容：hello"冒号形态）——E2E 实证：模型写任务目录同名文件+旧内容，引号形态
        // 提取不到放行；扩展形态提取到"hello"即拦截。
        if (requirementTokens is { Count: > 0 } && writtenFiles is { Count: > 0 })
        {
            var anyHit = writtenFiles.Any(f => ContentConformance.FileContainsTokens(f, requirementTokens));
            if (!anyHit)
            {
                var sample = requirementTokens.Count > 1
                    ? string.Join(" / ", requirementTokens)
                    : requirementTokens[0];
                return new CompletionVerdict(
                    shouldFail: true,
                    message: $"任务要求文件中包含内容「{sample}」，但本轮写过的文件均未包含（修改内容与需求不符）。请用 edit_file 精确修改后再验证。",
                    isSuspicious: true);
            }
        }

        return null;
    }

    /// <summary>路径等价比较（归一化 + 忽略大小写，Windows 路径不区分大小写）。</summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>完成门判定结果：shouldFail + 失败消息 + 是否标可疑（目标不符 = 假完成嫌疑）。</summary>
public sealed record CompletionVerdict(bool shouldFail, string message, bool isSuspicious);
