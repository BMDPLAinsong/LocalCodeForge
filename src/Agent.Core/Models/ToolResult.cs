// ============================================================
// Models / ToolResult.cs
// 工具契约结构化（10-13 批次4：消"验证器/熔断/完成门字符串匹配堆规则"根因）。
//   · 此前工具返回 string，错误以"错误："前缀编码——消费方全在匹配文本。
//   · 现在工具返回结构化 ToolResult（Success / Code / Message）：
//       Success = 工具层是否接受（文件类=非错误；run_command=非工具层拒绝）
//       Code    = 确定性错误类别（out_of_bounds / pd_conflict / not_found ...）
//       Message = 既有文本（ToString 逐字兼容——渲染/E2E/tests 断言零冲击）
//   · 兼容转发：Contains/StartsWith/EndsWith/Length + 隐式转 string——
//     既有调用点/tests 的字符串用法不因类型变化而编译破坏（渐进迁移）。
// ============================================================

namespace Agent.Core.Models;

/// <summary>
/// 工具执行结果（结构化契约）。不可变 record。
/// </summary>
public sealed record ToolResult(bool Success, string? Code, string Message)
{
    /// <summary>成功结果（无错误码）。</summary>
    public static ToolResult Ok(string message) => new(true, null, message);

    /// <summary>失败结果（带确定性错误类别码）。</summary>
    public static ToolResult Fail(string code, string message) => new(false, code, message);

    /// <summary>既有文本契约（渲染 / E2E / 事件存储逐字兼容）。</summary>
    public override string ToString() => Message;

    // ─── 兼容转发（旧调用点字符串用法渐进迁移，不阻塞本次结构化） ───
    public bool Contains(string value) => Message.Contains(value, StringComparison.Ordinal);
    public bool StartsWith(string value) => Message.StartsWith(value, StringComparison.Ordinal);
    public bool EndsWith(string value) => Message.EndsWith(value, StringComparison.Ordinal);
    public int Length => Message.Length;
    public static implicit operator string(ToolResult r) => r.Message;
}

/// <summary>
/// 【工具契约结构化】确定性错误类别词表（单一来源）。
/// 消费方（熔断/完成门/归因）读 Code 而非匹配文本。
/// </summary>
public static class ToolErrorCodes
{
    public const string OutOfBounds = "out_of_bounds";       // 路径越界（工作区/工程根之外）
    public const string SensitivePath = "sensitive_path";   // 敏感区源头拒（系统/凭据）
    public const string NotFound = "not_found";             // 文件/目录不存在
    public const string TooLarge = "too_large";             // 文件过大（> 阈值）
    public const string InvalidArg = "invalid_arg";         // 参数错误（range 格式/空 name）
    public const string NotProject = "not_project";         // 需要工程模式（find_symbol/analyze_deps）
    public const string InteractiveInput = "interactive_input"; // 交互输入拒绝（input/ReadLine）
    public const string CmdNotAllowed = "cmd_not_allowed";  // run_command 命令不在白名单
    public const string InlineExec = "inline_exec";         // run_command 禁止 -c/-e 内联
    public const string Timeout = "timeout";                // run_command 超时
    public const string PathEscape = "path_escape";         // run_command 路径穿越 ..
    public const string SensitiveCmd = "sensitive_cmd";     // run_command 命令涉及敏感路径
    public const string PdConflict = "pd_conflict";         // 写前类型冲突（P-D）
    public const string ReadOnly = "read_only";             // 工程只读拒绝写
    public const string ProjectFile = "project_file";       // 工程结构文件禁改
    public const string ToolNotFound = "tool_not_found";    // 工具不存在（ExecuteToolAsync 层）
}
