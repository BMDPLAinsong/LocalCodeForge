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

    /// <summary>
    /// use &lt;path&gt; &lt;任务&gt; 一步到位（10-08 C）：从"路径 + 任务"混合串拆出路径与任务。
    /// 路径 = Directory.Exists 校验通过的最长前缀（路径可含空格如 "D:\My Projects\MyApp"，按段边界切不误伤）；
    /// 剩余段 = 自动执行的任务。任务为空返回 false（纯路径）。
    /// 切分性质：目录存在性有传递性（父目录存在才可能子目录存在）→ 从左往右探测、首个不存在即停，
    /// 停点之后全是任务段（多 token 任务同样正确）。
    /// 已知边界（文档注明）：路径打错时回退到最近存在的祖先（如盘根 "X:"），剩余全部当任务——
    /// 表现为 use 到上级目录 + 任务错位，输出可见可纠正。
    /// </summary>
    public static bool TrySplitPathTask(string pathish, out string path, out string task)
    {
        path = "";
        task = "";
        var s = (pathish ?? "").Trim();
        if (s.Length == 0) return false;

        // 候选边界：每个 '\' 后（真实目录边界）+ 每个空格后（可能属于路径，也可能分隔任务）+ 整串末尾。
        // 存在性传递只对真实目录边界成立（父存在才可能子存在）；空格边界不存在时跳过——
        // 路径内空格（如 "D:\My Projects"）会让空格后候选（"D:\My"）不存在，但空格属于路径、不能断链。
        var cuts = new List<int>();
        for (var k = 1; k < s.Length; k++)
            if (s[k - 1] == '\\' || s[k - 1] == ' ') cuts.Add(k);
        cuts.Add(s.Length);

        string? best = null;
        var bestLen = 0;
        foreach (var i in cuts)
        {
            var cand = s.Substring(0, i).TrimEnd('\\', ' ');
            if (cand.Length == 0) continue;
            if (Directory.Exists(cand))
            {
                best = cand;
                bestLen = cand.Length;
            }
            else if (i == s.Length) break;            // 整串不存在 → 后面无更多候选，停
            else if (s[i - 1] == '\\') break;         // 真实目录边界不存在 → 路径断链，剩余全是任务
            // 空格边界不存在：跳过继续（空格可能属于路径，如 "D:\My Projects"）
        }
        if (best is null) { path = s; return false; }   // 全串无目录前缀：保持原行为（ApplyUse 报"目录不存在"）

        // 【10-08 盘符归一化】盘符形态 "X:" → "X:\"：.NET 的 Path.GetFullPath("X:") 按盘符相对语义
        // 解析成"X 盘上的当前目录"（cwd），而非盘根——Directory.Exists("X:") 为 true 会掩盖错位。
        if (best.Length == 2 && best[1] == ':') { best += "\\"; bestLen += 1; }

        path = best;
        task = s.Substring(bestLen).TrimStart('\\', ' ');
        return task.Length > 0;
    }

    /// <summary>
    /// 【10-12 B】use 一步到位：从文本提取第一个盘根（"x:\" / "X:"），用于 use 参数非路径开头时
    /// （如 "use --write 读取 x:\ 盘下 index.html 文件…"）自动 use 盘根 + 整段作任务。
    /// 仅当盘根真实存在时返回 true。
    /// 【10-14 P1-4】实现归位 FileGrammar.TryExtractLiteralDiskRoot（单源），此处保留公共 API 委托。
    /// </summary>
    public static bool TryExtractDiskRoot(string input, out string root)
        => FileGrammar.TryExtractLiteralDiskRoot(input, out root);
}
