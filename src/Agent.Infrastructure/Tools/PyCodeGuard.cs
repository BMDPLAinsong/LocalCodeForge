// ============================================================
// PyCodeGuard.cs
// Python 代码写入/编辑时的确定性安全改写与拦截（单一来源）：
//   1. EnsureHttpTimeout  —— 外部 HTTP 调用无 timeout → 自动补 timeout=8
//   2. DetectInteractive  —— 交互输入调用（input / Console.ReadLine）→ 拒绝
// WriteFileTool 与 EditFileTool 共用，避免双路径重复实现（§12-19 教训）。
// ============================================================

using System.Text.RegularExpressions;

namespace Agent.Infrastructure.Tools;

/// <summary>
/// Python 代码写入/编辑时的确定性后处理与拦截。
/// </summary>
public static class PyCodeGuard
{
    /// <summary>
    /// 外部 HTTP 调用强制补 timeout（默认 8 秒）。
    /// 确定性行级改写：无 timeout 的 requests / urllib / http.client 调用补 ", timeout=8"；
    /// 已带 timeout、跨行调用（行内无闭合括号）、非 Python 文件一律不动（保守防破坏）。
    /// 目的：外部服务（ip-api.com / wttr.in 等）在用户网络可能瞬时抖动，
    /// 无 timeout 的请求会无限挂起（10/01 14:22 实证 30s kill）——
    /// 补 timeout 后挂起形态变成秒级快速失败，被运行输出检查链接住（§14-3h）。
    /// </summary>
    public static string EnsureHttpTimeout(string code)
    {
        var callPatterns = new[]
        {
            "requests.get(", "requests.post(", "requests.put(", "requests.delete(",
            "requests.patch(", "requests.head(", "requests.options(", "requests.request(",
            "urlopen(", "HTTPConnection(", "HTTPSConnection("
        };

        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // 已带 timeout 的行一律不动
            if (line.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var pattern in callPatterns)
            {
                var idx = line.IndexOf(pattern, StringComparison.Ordinal);
                if (idx < 0)
                    continue;

                // 调用括号必须在本行闭合，否则是跨行调用，保守跳过
                var closeIdx = line.IndexOf(')', idx);
                if (closeIdx < 0)
                    continue;

                lines[i] = line.Insert(closeIdx, ", timeout=8");
                break;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 检测交互输入调用。验证环境（run_command）无终端输入，
    /// 含 input() / Console.ReadLine() 的程序会挂起等待 → 30s 超时 kill。
    /// 确定性拦截（内容特征），命中返回拒绝文案；未命中返回 null。
    /// </summary>
    public static string? DetectInteractive(string content, string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();

        if (ext == ".py" && Regex.IsMatch(content, @"\binput\s*\("))
        {
            return "错误：程序包含交互输入 input()，当前验证环境无法提供终端输入，程序会挂起等待。"
                + "请改为：使用命令行参数（sys.argv）或内置固定数据，确保无参数运行即可输出结果。";
        }

        if (ext == ".cs" && Regex.IsMatch(content, @"Console\.ReadLine\s*\("))
        {
            return "错误：程序包含交互输入 Console.ReadLine()，当前验证环境无法提供终端输入，程序会挂起等待。"
                + "请改为：使用命令行参数或内置固定数据，确保无参数运行即可输出结果。";
        }

        return null;
    }
}
