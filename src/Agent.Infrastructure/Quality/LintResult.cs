// ============================================================
// LintResult.cs
// Lint 检查结果的数据结构。
// ============================================================

namespace Agent.Infrastructure.Quality;

/// <summary>
/// Lint 检查结果。
/// </summary>
public sealed record LintResult(
    bool Passed,
    IReadOnlyList<LintError> Errors,
    string RawOutput)
{
    /// <summary>通过。</summary>
    public static LintResult Success(string rawOutput = "") =>
        new(true, Array.Empty<LintError>(), rawOutput);

    /// <summary>失败。</summary>
    public static LintResult Fail(IReadOnlyList<LintError> errors, string rawOutput) =>
        new(false, errors, rawOutput);
}

/// <summary>
/// 单个 lint 错误。
/// </summary>
public sealed record LintError(
    string FilePath,
    int Line,
    int Column,
    string RuleId,
    string Message)
{
    /// <summary>格式化为 "file(line,col): [RULE] message"。</summary>
    public string ToDiagnosticString() =>
        $"{FilePath}({Line},{Column}): [{RuleId}] {Message}";
}