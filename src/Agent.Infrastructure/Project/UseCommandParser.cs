namespace Agent.Infrastructure.Project;

/// <summary>
/// use 命令参数解析（10-08 A）。裸 use / use --write（无路径）必须被命令层拦截并打印用法提示，
/// 绝不能当任务执行——实证：裸 use 漏进任务流后 agent_pro 把 "use" 误解为"继续上一任务"跑 8 步。
/// 抽成静态解析器是为了让 CLI 命令层与 tests 共用同一判定（无 InternalsVisibleTo，保持 public）。
/// </summary>
public static class UseCommandParser
{
    /// <summary>
    /// 解析 use 命令。命中且带路径 → true（path/writeFlag 就位）；裸 use / use --write / 非 use 输入 → false。
    /// 注意与 IsUseCommand 配合：TryParse false + IsUseCommand true = "use 命令但缺路径"（命令层打印提示）。
    /// </summary>
    public static bool TryParse(string input, out string path, out bool writeFlag)
    {
        path = "";
        writeFlag = false;
        var s = (input ?? "").TrimStart('\uFEFF').Trim();
        if (!s.Equals("use", StringComparison.OrdinalIgnoreCase)
            && !s.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = s.StartsWith("use ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4).Trim() : "";
        if (rest.StartsWith("--write ", StringComparison.OrdinalIgnoreCase))
        {
            writeFlag = true;
            rest = rest.Substring(8).Trim();
        }
        else if (rest.Equals("--write", StringComparison.OrdinalIgnoreCase))
        {
            writeFlag = true;
            rest = "";
        }

        if (string.IsNullOrWhiteSpace(rest)) return false;
        path = rest;
        return true;
    }

    /// <summary>是否为 use 命令（含裸 use / use --write）：严格锚定 ^use$ 或 ^use\s，不误伤 "useful"/中文"使用"。 </summary>
    public static bool IsUseCommand(string input)
    {
        var s = (input ?? "").TrimStart('\uFEFF').Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(
            s, @"^use(\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
