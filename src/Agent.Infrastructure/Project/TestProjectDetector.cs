// ============================================================
// TestProjectDetector.cs
// 工程完成门：确定性检测工程是否含测试项目/入口。
// 无 LLM、纯文件系统检查；用于"改了代码没跑测试"完成门。
// ============================================================

using System.Text;

namespace Agent.Infrastructure.Project;

/// <summary>
/// 检测工程是否含测试项目/入口（完成门依据）。
/// </summary>
public static class TestProjectDetector
{
    /// <summary>
    /// 任一命中即视为有测试：
    ///  1. 工程根下存在 tests/ test/ Tests/ Test 目录；
    ///  2. 工程根下存在文件名含 Test(s) 的 .csproj（*.Tests.csproj 等）；
    ///  3. 存在 pytest.ini / pyproject.toml（含 pytest）；
    ///  4. package.json 含 "test" script。
    /// </summary>
    public static bool HasTests(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return false;

        try
        {
            // 1) 测试目录
            foreach (var dir in new[] { "tests", "test", "Tests", "Test" })
            {
                if (Directory.Exists(Path.Combine(projectRoot, dir)))
                    return true;
            }

            // 2) 测试工程文件（文件名含 Test(s)）
            var csprojs = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.AllDirectories);
            foreach (var p in csprojs)
            {
                if (Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 3) pytest 配置
            if (File.Exists(Path.Combine(projectRoot, "pytest.ini")))
                return true;
            var pyproject = Path.Combine(projectRoot, "pyproject.toml");
            if (File.Exists(pyproject))
            {
                var text = File.ReadAllText(pyproject);
                if (text.Contains("pytest", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 4) package.json test script
            var pkg = Path.Combine(projectRoot, "package.json");
            if (File.Exists(pkg))
            {
                var text = File.ReadAllText(pkg);
                if (text.Contains("\"test\"", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
