// ============================================================
// ILintService.cs
// 静态分析服务接口。
// ============================================================

namespace Agent.Infrastructure.Quality;

/// <summary>
/// Lint 服务。在跑 run_command 之前，用静态分析快速拦截语法/作用域错误。
/// </summary>
public interface ILintService
{
    /// <summary>
    /// 检查一批文件。
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录（用于执行 lint 命令）</param>
    /// <param name="filePaths">要检查的文件相对路径</param>
    /// <param name="language">语言：python / csharp / javascript</param>
    /// <param name="ct">取消令牌</param>
    Task<LintResult> LintAsync(
        string workspaceRoot,
        IReadOnlyList<string> filePaths,
        string language,
        CancellationToken ct);
}