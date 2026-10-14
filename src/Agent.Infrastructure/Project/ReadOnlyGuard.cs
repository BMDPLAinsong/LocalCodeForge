// ============================================================
// ReadOnlyGuard.cs
// 只读工程组合边界预检：工程模式 + 未授权写（use 不带 --write）+ 写任务引用工程内已有文件
// → 必然失败（模型写工程文件会被写白名单拒绝）。CLI 主循环在 Orchestrator/AgentLoop 启动前
// 直接阻断，避免任务空跑 10-15 分钟后才在工具层被拒（纯确定性判断，无 LLM）。
// ============================================================

using Agent.Infrastructure.Orchestration;
using Agent.Infrastructure.Routing;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 只读工程拦截守卫。纯静态、确定性、可测。
/// 与 CompletionGate 同族：都是"任务开始前/收尾时"的确定性预检，不依赖模型判断。
/// </summary>
public static class ReadOnlyGuard
{
    /// <summary>
    /// 是否应阻断任务。
    /// 四个条件【全部】满足才拦截：
    ///   1. 工程模式（projectRoot 非 null）
    ///   2. 未授权写（!projectWriteEnabled，use 不带 --write）
    ///   3. 写任务（IntentOverride.IsWriteTask：写动词 + 代码/文件语境）
    ///   4. 任务输入引用的文件名在工程根内实际存在（FindExistingProjectFileRef 命中）
    /// 放行豁免：显式写目标（TryGetWriteTarget）在工程外 → 不拦（写工程外由 explicitWriteRoots 机制支持）。
    /// </summary>
    public static bool ShouldBlock(string input, string? projectRoot, bool projectWriteEnabled, out string? hitFile)
    {
        hitFile = null;
        if (string.IsNullOrWhiteSpace(input) || projectRoot is null || projectWriteEnabled)
            return false;
        if (!IntentOverride.IsWriteTask(input))
            return false;

        hitFile = FindExistingProjectFileRef(input, projectRoot);
        if (hitFile is null)
            return false;

        // 有显式写目标且在工程外 → 放行（"在 X:\test.txt 创建" 不该被拦）
        if (ProjectPathDetector.TryGetWriteTarget(input, out var target, out _)
            && target is not null
            && !PathBoundary.IsInside(target, projectRoot))
            return false;

        return true;
    }

    /// <summary>
    /// 任务输入引用的文件名中，第一个在工程根内实际存在的（OrdinalIgnoreCase，Windows 大小写不敏感）。
    /// 无引用 / 引用的都不在工程内 → null（不拦截）。
    /// 枚举口径与全层统一（【10-14 F3】FileGrammar 排除 bin/obj/.git/node_modules 等——原为裸全量枚举，
    /// 构建产物里的同名文件会被误判为"工程内已存在"→ 错拦只读工程任务）。
    /// 文件枚举失败（权限/路径不存在）→ null（交给下游工具层正常拒绝，不误拦）。
    /// </summary>
    public static string? FindExistingProjectFileRef(string input, string projectRoot)
    {
        var refs = TaskClassifier.FileReferencePattern.Matches(input)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (refs.Length == 0)
            return null;

        try
        {
            var existing = FileGrammar.EnumerateAllFiles(projectRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return refs.FirstOrDefault(existing.Contains);
        }
        catch
        {
            return null;
        }
    }
}
