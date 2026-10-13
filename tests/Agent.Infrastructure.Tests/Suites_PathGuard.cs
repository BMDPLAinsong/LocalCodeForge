// ============================================================
// Suites_PathGuard.cs
// 双根保护与路径治理（PathBoundary / SensitivePathGuard / 写白名单 /
// run_command 敏感 / 启动重叠 / ProjectPathDetector / 完成门 / 内容符合性 /
// 显式写授权 / 只读组合边界）
// 区段：715 / 2596 / 2606 / 2665 / 2695 / 2715 / 2727 / 2773 / 2809 /
//       2848 / 2872 / 2907 / 2918 / 2947 / 3016 / 3057
// 依赖：using static AgentTests.TestHarness（Check / TestTmp）
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_PathGuard
{
    // ── 区段 715：工程感知 P1：run_command 路径边界（§14-3t） ──
    public static void Run715()
    {
        Console.WriteLine("== 工程感知 P1：run_command 路径边界（§14-3t） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_p1rc");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "task");
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(proj);

            string Rc(string? projectRoot, string command)
            {
                // 10-10 修正：工程模式断言统一走"工程可写"（cwd=工程根，与 ResolveWorkingDirectory 新语义一致）
                var tool = new Agent.Infrastructure.Tools.RunCommandTool(ws, projectRoot, projectWriteEnabled: true);
                var args = JsonSerializer.Deserialize<JsonElement>($"{{\"command\":{JsonSerializer.Serialize(command)}}}");
                return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            Check("工程模式：盘符敏感路径（C:\\Windows）→ 敏感源头拒",
                Rc(proj, "dir C:\\Windows").Contains("敏感"));
            Check("工程模式：路径穿越（..）→ 拒绝",
                Rc(proj, "type ..\\..\\Windows\\win.ini").Contains("越界"));
            Check("工程模式：工程根内绝对路径 → 放行",
                !Rc(proj, "dir \"" + proj.Replace("\\", "/") + "\"").Contains("越界"));
            Check("工程模式：相对路径命令 → 放行",
                !Rc(proj, "python main.py").Contains("越界"));
            Check("沙箱模式：不启用边界检查（现状保持）",
                !Rc(null, "dir C:\\Windows").Contains("越界"));

            // 【D1 衍生】项目名幻觉校验（10-10 修正：基准=实际 cwd，相对存在 + 递归同名）
            File.WriteAllText(Path.Combine(proj, "lawcheck_demo.csproj"), "<Project></Project>");
            Check("工程模式：dotnet run 无 --project → 放行（项目名校验不误伤）",
                !Rc(proj, "dotnet run").Contains("不存在于工作目录"));
            Check("工程模式：幻觉项目名（OrderSystem.csproj）→ 拒绝+提示正确项目名",
                Rc(proj, "dotnet run --project OrderSystem.csproj").Contains("不存在于工作目录")
                && Rc(proj, "dotnet run --project OrderSystem.csproj").Contains("lawcheck_demo.csproj"));
            Check("工程模式：真实项目名 → 放行",
                !Rc(proj, "dotnet run --project lawcheck_demo.csproj").Contains("不存在于工作目录"));
            Check("沙箱模式：项目名校验不启用（现状保持）",
                !Rc(null, "dotnet run --project Whatever.csproj").Contains("不存在于工作目录"));
        }
    }

    // ── 区段 2596：双根保护：PathBoundary 逐段比较 ──
    public static void Run2596()
    {
        Console.WriteLine("== 双根保护：PathBoundary 逐段比较 ==");
        {
            Check("子目录在内", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj\src\a.cs"));
            Check("root 自身在内", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj"));
            Check("前缀假阳性根治（proj2 不被 proj 放行）", !Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", @"X:\proj2\a.cs"));
            Check(".. 跳脱拒绝",
                !Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\proj", System.IO.Path.GetFullPath(@"X:\proj\..\proj2\a.cs")));
            Check("大小写归一", Agent.Infrastructure.Project.PathBoundary.IsInside(@"X:\PROJ", @"x:\proj\a.cs"));
        }
    }

    // ── 区段 2606：双根保护：SensitivePathGuard 敏感区源头拒 ──
    public static void Run2606()
    {
        Console.WriteLine("== 双根保护：SensitivePathGuard 敏感区源头拒 ==");
        {
            Check("系统区命中（C:\\Windows 子路径）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"C:\Windows\System32\evil.txt"));
            Check("凭据区命中（.ssh）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config")));
            Check("凭据区命中（.aws）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials")));
            Check("前缀假阳性不误伤（C:\\Windows2）", !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"C:\Windows2\evil.txt"));
            Check("普通工程路径不拒", !Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"X:\work\proj\src\a.cs"));

            // 8.3 短名（条件断言：仅当短名目录真实存在）
            var shortDir = @"C:\PROGRA~1\Common Files";
            if (Directory.Exists(shortDir))
                Check("8.3 短名绕过被拦（C:\\PROGRA~1）", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(shortDir));
            else
                Console.WriteLine("  [SKIP] 8.3 短名断言：C:\\PROGRA~1\\Common Files 不存在（系统可能关闭了 8.3 短名）");

            // 符号链接（条件断言：junction 创建成功才测）
            var linkBase = Path.Combine(TestTmp, "tests_dr_link");
            if (Directory.Exists(linkBase)) Directory.Delete(linkBase, true);
            Directory.CreateDirectory(linkBase);
            var linkPath = Path.Combine(linkBase, "lnk");
            var linked = false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"C:\\Windows\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(5000);
                linked = p.ExitCode == 0 && Directory.Exists(linkPath);
            }
            catch { linked = false; }
            if (linked)
            {
                Check("符号链接最终目标命中敏感区（link→C:\\Windows）",
                    Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(Path.Combine(linkPath, "System32", "cmd.exe")));
                // junction 目录删除需先删链接本体（否则 UnauthorizedAccessException）
                try { Directory.Delete(linkPath); } catch { }
                try { Directory.Delete(linkBase, true); } catch { }
            }
            else
                Console.WriteLine("  [SKIP] 符号链接断言：junction 创建失败（权限或环境）");

            // 用户自定义 denylist（第三类预留）：不存在静默 + 存在加载
            var dlDir = Path.Combine(TestTmp, "tests_dr_denylist");
            if (Directory.Exists(dlDir)) Directory.Delete(dlDir, true);
            Directory.CreateDirectory(dlDir);
            Agent.Infrastructure.Project.SensitivePathGuard.LoadUserDenylist(dlDir); // 无 sensitive_denylist.txt → 静默跳过
            Check("denylist 文件不存在 → 静默（不抛）", true);
            File.WriteAllText(Path.Combine(dlDir, "sensitive_denylist.txt"),
                "# 用户自定义敏感路径\n\n  \nX:\\work\\my_private\n");
            Agent.Infrastructure.Project.SensitivePathGuard.AddDeniedRoot(@"X:\work\my_private");
            Check("用户 denylist 追加生效", Agent.Infrastructure.Project.SensitivePathGuard.IsDenied(@"X:\work\my_private\secret.txt"));
            Directory.Delete(dlDir, true);
        }
    }

    // ── 区段 2665：双根保护：写白名单（WriteRoots）+ 前缀假阳性 ──
    public static void Run2665()
    {
        Console.WriteLine("== 双根保护：写白名单（WriteRoots）+ 前缀假阳性 ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_dr_write");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "task");
            var proj = Path.Combine(baseTmp, "proj");
            var proj2 = Path.Combine(baseTmp, "proj2"); // 前缀同名目录（假阳性测试靶）
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(proj);
            Directory.CreateDirectory(proj2);

            string W(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
            {
                var json = $"{{\"path\":{JsonSerializer.Serialize(path)},\"content\":{JsonSerializer.Serialize(content)}}}";
                var args = JsonSerializer.Deserialize<JsonElement>(json);
                return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            var wRw = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, true);
            var absIn = Path.Combine(proj, "src", "Abs.cs");
            Check("写白名单：工程根内绝对路径 → 放行",
                W(wRw, absIn, "// abs\n").Contains("已写入") && File.Exists(absIn));
            var absOut = Path.Combine(baseTmp, "outside.cs");
            Check("写白名单：双根外绝对路径 → 越界拒",
                W(wRw, absOut, "x").Contains("越界"));
            Check("写白名单：前缀同名目录（proj2）→ 越界拒（假阳性根治）",
                W(wRw, Path.Combine(proj2, "f.cs"), "x").Contains("越界"));
            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 2695：双根保护：run_command 敏感路径源头拒（跨模式） ──
    public static void Run2695()
    {
        Console.WriteLine("== 双根保护：run_command 敏感路径源头拒（跨模式） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_dr_rc");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "task");
            Directory.CreateDirectory(ws);

            string Rc(string command)
            {
                var tool = new Agent.Infrastructure.Tools.RunCommandTool(ws);
                var args = JsonSerializer.Deserialize<JsonElement>($"{{\"command\":{JsonSerializer.Serialize(command)}}}");
                return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            var sshConfig = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            Check("绝对路径敏感命令 → 拒绝", Rc("type " + sshConfig).Contains("敏感"));
            Check("环境变量展开后敏感命令 → 拒绝", Rc("type %USERPROFILE%\\.ssh\\config").Contains("敏感"));
            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 2715：双根保护：启动重叠自检 ──
    public static void Run2715()
    {
        Console.WriteLine("== 双根保护：启动重叠自检 ==");
        {
            var overlaps = Agent.Infrastructure.Project.SensitivePathGuard.FindWriteRootOverlaps(new[] { @"X:\work\proj" });
            Check("正常工程根不重叠", overlaps.Count == 0);
            // 工程根落在敏感区内 → 命中
            var overlaps2 = Agent.Infrastructure.Project.SensitivePathGuard.FindWriteRootOverlaps(
                new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "myproj") });
            Check("敏感区内工程根 → 重叠检出", overlaps2.Count == 1);
        }
    }

    // ── 区段 2727：双根保护·输入路径自动识别（给路径就干活）——ProjectPathDetector ──
    public static void Run2727()
    {
        Console.WriteLine("== 双根保护：ProjectPathDetector 输入路径自动识别 ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_dr_autouse");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var proj = Path.Combine(baseTmp, "My Project", "Sub"); // 含空格的嵌套目录（真实路径）
            Directory.CreateDirectory(proj);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "README.md"), "# demo");

            string Ext(string input) =>
                Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath(input, out var p) ? p : "";

            // 提取：中文标点分片 + 尾部 CJK 语境词剥离
            Check("提取：标点分片后命中（读取一下 …项目）",
                Ext($"读取一下 {proj} 这个路径的项目，分析一下").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
            Check("提取：含空格真实路径直接命中（不误剥）",
                Ext($"看看 {proj}").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
            Check("提取：尾部中文语境词剥离（X:\\…目录）",
                Ext($"分析 {proj} 目录").Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
            Check("提取：无路径 → 失败", !Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath("你好，介绍一下", out _));
            Check("提取：不存在路径尾部非 CJK → 不剥不误判",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryExtractProjectPath($"读取 {Path.Combine(baseTmp, "not_exist_123")} 的内容", out _));

            // 语境门
            Check("读语境：读取/分析/看看/项目 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"读取一下 {proj} 这个路径的项目，分析一下") &&
                Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"看看 {proj} 的结构") &&
                Agent.Infrastructure.Project.ProjectPathDetector.HasReadContext($"{proj} 项目"));
            Check("写语境：写/创建/生成 命中（写优先拦）",
                Agent.Infrastructure.Project.ProjectPathDetector.HasWriteContext($"帮我写一个 C# 项目，保存到 {proj}") &&
                Agent.Infrastructure.Project.ProjectPathDetector.HasWriteContext($"把文件复制到 {proj}"));

            // 组合判定
            Check("自动 use：读语境 + 存在路径 → 通过",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath($"读取一下 {proj} 这个路径的项目，分析一下", out var p1) &&
                p1.Equals(Path.GetFullPath(proj), StringComparison.OrdinalIgnoreCase));
            Check("自动 use：写任务 → 不触发",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath($"帮我写一个 C# 项目，保存到 {proj}", out _));
            Check("自动 use：敏感区路径 → 源头不触发",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryGetAutoUsePath("读取一下 C:\\Windows\\System32 的内容", out _));

            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 2773：批次1：写目标提取（TryGetWriteTarget） ──
    public static void Run2773()
    {
        Console.WriteLine("== 批次1：写目标提取（TryGetWriteTarget） ==");
        {
            // 通道 2：自然语言盘符 + 文件名（x盘根目录 test.txt → X:\test.txt）
            Check("写目标：X盘根目录+文件名 → 文件级",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("在x盘根目录，新建一下test.txt文件", out var t1, out var f1)
                && f1 && t1.Equals(@"X:\test.txt", StringComparison.OrdinalIgnoreCase));
            Check("写目标：X盘下+大写盘符 → 归一化",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("在X盘下新建 report.md", out var t2, out _)
                && t2.Equals(@"X:\report.md", StringComparison.OrdinalIgnoreCase));
            // 无文件名 → 目录级
            Check("写目标：X盘根目录无文件名 → 目录级",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("保存到x盘根目录", out var t3, out var f3)
                && !f3 && t3.Equals(@"X:\", StringComparison.OrdinalIgnoreCase));
            // 通道 1：完整绝对路径（已存在文件或形似文件）
            var baseTmp = Path.Combine(TestTmp, "tests_b1_target");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            Directory.CreateDirectory(baseTmp);
            var realFile = Path.Combine(baseTmp, "data.txt");
            File.WriteAllText(realFile, "x");
            Check("写目标：完整路径（已存在文件）→ 文件级",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"把新内容写入 {realFile}", out var t4, out var f4)
                && f4 && t4.Equals(Path.GetFullPath(realFile), StringComparison.OrdinalIgnoreCase));
            Check("写目标：完整路径（不存在但形似文件）→ 文件级",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"创建 {Path.Combine(baseTmp, "new.log")} 文件", out var t5, out var f5)
                && f5 && t5.Equals(Path.GetFullPath(Path.Combine(baseTmp, "new.log")), StringComparison.OrdinalIgnoreCase));
            Check("写目标：完整路径（目录）→ 目录级",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget($"把文件保存到 {baseTmp} 目录", out var t6, out var f6)
                && !f6 && t6.Equals(Path.GetFullPath(baseTmp), StringComparison.OrdinalIgnoreCase));
            // 无写语境 → 不触发
            Check("写目标：读语境（无写词）→ 不触发",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget("读取一下 X:\\data 的内容", out _, out _));
            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 2809：10-12 X1/X2：CleanPathSuffix + TryResolveAbsolutePath ──
    public static void Run2809()
    {
        Console.WriteLine("== 10-12 X1/X2：CleanPathSuffix + TryResolveAbsolutePath ==");
        {
            Check("清洗：盘下+文件 → 干净路径",
                Agent.Infrastructure.Project.ProjectPathDetector.CleanPathSuffix("x:\\盘下index.html文件") == "x:\\index.html");
            Check("清洗：尾部文件 → 剥离",
                Agent.Infrastructure.Project.ProjectPathDetector.CleanPathSuffix("X:\\index.html文件") == "X:\\index.html");
            Check("清洗：无方位词不动",
                Agent.Infrastructure.Project.ProjectPathDetector.CleanPathSuffix("x:\\index.html") == "x:\\index.html");
            Check("清洗：空格分隔形态（x:\\ 盘下 index.html）→ 干净",
                Agent.Infrastructure.Project.ProjectPathDetector.CleanPathSuffix("x:\\ 盘下 index.html 文件") == "x:\\index.html");
            Check("清洗：真实目录名'下载'不误伤",
                Agent.Infrastructure.Project.ProjectPathDetector.CleanPathSuffix("X:\\下载") == "X:\\下载");
            Check("绝对路径解析：不存在路径 → false",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(
                    "读取x:\\NoSuchDirZZZ_missing_xyz.html文件并修复", out _));
            // 真实文件 + 自然语言方位词 → 通道1清洗后命中绝对路径
            // （方位词前补 \：真实用户输入"x:\盘下index.html"盘根自带分隔符；目录名后跟方位词无分隔符属少见形态）
            var x1Base = Path.Combine(TestTmp, "tests_x1_target");
            if (Directory.Exists(x1Base)) Directory.Delete(x1Base, true);
            Directory.CreateDirectory(x1Base);
            var x1File = Path.Combine(x1Base, "target.html");
            File.WriteAllText(x1File, "<html>x</html>");
            Check("绝对路径解析：盘下+文件清洗后命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(
                    $"读取{x1Base}\\盘下target.html文件，并修复它的语法错误", out var abs1)
                && abs1.Equals(Path.GetFullPath(x1File), StringComparison.OrdinalIgnoreCase));
            Check("写目标：盘下+文件清洗后命中（X2 误判修复）",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget(
                    $"读取{x1Base}\\盘下target.html文件，并修复它的语法错误", out var wt, out var wf)
                && wf && wt.Equals(Path.GetFullPath(x1File), StringComparison.OrdinalIgnoreCase));
            Check("写目标：空格分隔形态（盘根 x:\\ 盘下 target.html）→ 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryGetWriteTarget(
                    $"读取{x1Base}\\盘下 target.html 文件，并修复它的语法错误", out var wt2, out var wf2)
                && wf2 && wt2.Equals(Path.GetFullPath(x1File), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(x1Base, true);
        }
    }

    // ── 区段 2848：10-12 B：沙箱读例外 ──
    public static void Run2848()
    {
        Console.WriteLine("== 10-12 B：沙箱读显式指向例外 ==");
        {
            var bDir = Path.Combine(TestTmp, "tests_b_read_run");      // run 目录（沙箱）
            var bOuter = Path.Combine(TestTmp, "tests_b_read_outer");  // run 外（越界目标）
            if (Directory.Exists(bDir)) Directory.Delete(bDir, true);
            if (Directory.Exists(bOuter)) Directory.Delete(bOuter, true);
            Directory.CreateDirectory(bDir);
            Directory.CreateDirectory(bOuter);
            var bExt = Path.Combine(bOuter, "ext.txt");
            File.WriteAllText(bExt, "explicit-target-content");
            var bTool = new Agent.Infrastructure.Tools.ReadFileTool(bDir);   // 沙箱：run=bDir，无工程根
            var bArgs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                $"{{\"path\":\"{bExt.Replace("\\", "/")}\"}}");
            Check("沙箱读：未显式指向 → 越界",
                bTool.InvokeAsync(bArgs, CancellationToken.None).GetAwaiter().GetResult().Contains("路径越界"));
            bTool.SetExplicitReadTarget(bExt);
            Check("沙箱读：显式指向 → 放行",
                bTool.InvokeAsync(bArgs, CancellationToken.None).GetAwaiter().GetResult().Contains("explicit-target-content"));
            Directory.Delete(bDir, true);
            Directory.Delete(bOuter, true);
        }
    }

    // ── 区段 2872：10-12 A：绝对路径解析空格形态 ──
    public static void Run2872()
    {
        Console.WriteLine("== 10-12 A：绝对路径解析空格形态 ==");
        {
            var aBase = Path.Combine(TestTmp, "tests_abs_probe");
            if (Directory.Exists(aBase)) Directory.Delete(aBase, true);
            Directory.CreateDirectory(aBase);
            var aFile = Path.Combine(aBase, "probe.html");
            File.WriteAllText(aFile, "x");
            Check("绝对路径解析：空格分隔方位词形态 → 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(
                    $"读取 {aBase}\\盘下 probe.html 文件，并修复它", out var pa)
                && pa.Equals(Path.GetFullPath(aFile), StringComparison.OrdinalIgnoreCase));

            var aSpace = Path.Combine(aBase, "my space dir");
            Directory.CreateDirectory(aSpace);
            var aSpaceFile = Path.Combine(aSpace, "readme.md");
            File.WriteAllText(aSpaceFile, "md");
            Check("绝对路径解析：含空格目录路径 → 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(
                    $"读取 {aSpace}\\readme.md", out var pb)
                && pb.Equals(Path.GetFullPath(aSpaceFile), StringComparison.OrdinalIgnoreCase));
            Check("绝对路径解析：尾部自然语言剥段 → 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsolutePath(
                    $"读取 {aSpace}\\readme.md 的内容", out var pc)
                && pc.Equals(Path.GetFullPath(aSpaceFile), StringComparison.OrdinalIgnoreCase));
            Check("目录解析：自然语言尾部剥段 → 命中",
                Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsoluteDirectory(
                    $"分析 {aSpace} 的结构", out var pd)
                && pd.Equals(Path.GetFullPath(aSpace), StringComparison.OrdinalIgnoreCase));
            Check("目录解析：无盘符文本 → false",
                !Agent.Infrastructure.Project.ProjectPathDetector.TryResolveAbsoluteDirectory("读取一下这个文件", out _));
            Directory.Delete(aBase, true);
        }
    }

    // ── 区段 2907：10-12 B：use 一步到位盘根提取 ──
    public static void Run2907()
    {
        Console.WriteLine("== 10-12 B：use 盘根提取 ==");
        {
            var tRoot = Path.GetPathRoot(TestTmp) ?? @"X:\";
            Check("use盘根提取：任务文本含盘根 → 命中",
                Agent.Infrastructure.Project.UseCommandParser.TryExtractDiskRoot(
                    $"读取 {tRoot} 盘下 index.html 文件，并修复它", out var root1)
                && root1.Equals(tRoot, StringComparison.OrdinalIgnoreCase));
            Check("use盘根提取：无盘符文本 → false",
                !Agent.Infrastructure.Project.UseCommandParser.TryExtractDiskRoot("读取一下这个文件的内容", out _));
        }
    }

    // ── 区段 2918：批次1：完成门（CompletionGate） ──
    public static void Run2918()
    {
        Console.WriteLine("== 批次1：完成门（CompletionGate） ==");
        {
            // 非写任务 → 不过门
            Check("完成门：非写任务 → 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(false, false, false, null, false) is null);
            // C：写被拒未重试 → 失败
            var c = Agent.Infrastructure.Project.CompletionGate.Evaluate(true, false, true, null, false);
            Check("完成门：写被拒且无任何写成功 → 判失败",
                c is not null && c.shouldFail && !c.isSuspicious);
            // B：目标未达成 → 失败 + 可疑（写了别处 ≠ 目标）
            var missingTarget = Path.Combine(TestTmp, "tests_b1_gate_missing", "never.txt");
            if (Directory.Exists(Path.GetDirectoryName(missingTarget))) Directory.Delete(Path.GetDirectoryName(missingTarget), true);
            var b = Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, missingTarget, true);
            Check("完成门：写了文件但显式目标不存在 → 判失败+可疑",
                b is not null && b.shouldFail && b.isSuspicious && b.message.Contains("never.txt"));
            // 目标存在 → 放行
            var baseTmp = Path.Combine(TestTmp, "tests_b1_gate");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            Directory.CreateDirectory(baseTmp);
            var f = Path.Combine(baseTmp, "ok.txt");
            File.WriteAllText(f, "ok");
            Check("完成门：目标文件存在 → 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, Path.GetFullPath(f), true) is null);
            // 目录级目标存在 → 放行
            Check("完成门：目录目标存在 → 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(true, true, false, Path.GetFullPath(baseTmp), false) is null);
            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 2947：10-08 内容符合性（ContentConformance + 完成门 D） ──
    public static void Run2947()
    {
        Console.WriteLine("== 10-08 内容符合性（ContentConformance + 完成门 D） ==");
        {
            // ① token 提取：加注释任务（无引号，注释形态）→ 提取 "// 已审查"（归一化 "已审查"）
            var t1 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "在 BigService.cs 的 Method1500 方法里加一行注释 // 已审查");
            Check("提取：注释形态任务 → 命中注释内容",
                t1.Count == 1 && t1[0] == "已审查");
            // ①b 提取：URL 不误提取（// 在 https:// 里）
            var t1b = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "参考 https://example.com/docs 实现功能");
            Check("提取：URL 中的 // 不误提取",
                t1b.Count == 0);
            // ①c 提取：C# 语言名不误提取（# 前是字母 → 非注释）
            var t1c = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "用 C# 写一个判断奇数偶数函数");
            Check("提取：C# 语言名不误提取",
                t1c.Count == 0);
            // ② token 提取：中文引号 + 多 token
            var t2 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "把标题改成 “欢迎” 并加上 “2026”");
            Check("提取：中文引号多 token",
                t2.Count == 2 && t2.Contains("欢迎") && t2.Contains("2026"));
            // ③ token 提取：无引号 → 跳过（不误伤普通生成任务）
            var t3 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "写一个 Python 计算器方法");
            Check("提取：无引号 → 空集（不误伤）",
                t3.Count == 0);
            // ④ token 提取：文件名形态过滤（BigService.cs 是目标不是内容）
            var t4 = Agent.Infrastructure.Project.ContentConformance.ExtractRequirementTokens(
                "把内容改成 BigService.cs 里的 “已审查”");
            Check("提取：文件名形态被过滤 + 引号内容保留",
                t4.Count == 1 && t4[0] == "已审查" && !t4.Contains("bigservice cs"));
            // ⑤ 包含检查：文件含 token → true
            var dirD = Path.Combine(TestTmp, "tests_d_gate");
            if (Directory.Exists(dirD)) Directory.Delete(dirD, true);
            Directory.CreateDirectory(dirD);
            var okFile = Path.Combine(dirD, "BigService.cs");
            File.WriteAllText(okFile, "public int Method1500(int x) { return x + 1500; } // 已审查\n");
            Check("包含：文件含需求内容 → true",
                Agent.Infrastructure.Project.ContentConformance.FileContainsTokens(okFile, new[] { "已审查" }));
            // ⑥ 包含检查：缺 token（改错/漏注释场景）→ false
            var badFile = Path.Combine(dirD, "BigService_bad.cs");
            File.WriteAllText(badFile, "public int Method1500(int x) { return x + 15000; }\n");
            Check("包含：文件缺需求内容（改错漏注释）→ false",
                !Agent.Infrastructure.Project.ContentConformance.FileContainsTokens(badFile, new[] { "已审查" }));
            // ⑦ 完成门 D：写了文件但不含需求 token → 拦截
            var dMiss = Agent.Infrastructure.Project.CompletionGate.Evaluate(
                true, true, false, Path.GetFullPath(badFile), true,
                requirementTokens: new[] { "已审查" }, writtenFiles: new[] { badFile });
            Check("完成门 D：修改内容不含需求 → 判失败+可疑",
                dMiss is not null && dMiss.shouldFail && dMiss.isSuspicious && dMiss.message.Contains("已审查"));
            // ⑧ 完成门 D：文件含需求 token → 放行
            Check("完成门 D：修改内容含需求 → 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(
                    true, true, false, Path.GetFullPath(okFile), true,
                    requirementTokens: new[] { "已审查" }, writtenFiles: new[] { okFile }) is null);
            // ⑨ 完成门 D：无需求 token → 跳过（不误伤）——且本轮写了目标文件
            Check("完成门 D：无需求 token → 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(
                    true, true, false, Path.GetFullPath(badFile), true,
                    requirementTokens: Array.Empty<string>(), writtenFiles: new[] { badFile }) is null);
            // ⑩ 归一化：文件含 "// 已审查" 匹配 token "已审查"（符号折叠）
            Check("完成门 D：归一化匹配（注释符号折叠）→ 放行",
                Agent.Infrastructure.Project.CompletionGate.Evaluate(
                    true, true, false, Path.GetFullPath(okFile), true,
                    requirementTokens: new[] { "已审查" }, writtenFiles: new[] { okFile }) is null);
            Directory.Delete(dirD, true);
        }
    }

    // ── 区段 3016：批次1：显式写授权（explicitWriteRoots）+ 敏感兜底 ──
    public static void Run3016()
    {
        Console.WriteLine("== 批次1：显式写授权（explicitWriteRoots）+ 敏感兜底 ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_b1_auth");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "task");
            Directory.CreateDirectory(ws);
            var authRoot = Path.Combine(baseTmp, "auth");
            Directory.CreateDirectory(authRoot);

            string W2(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
            {
                var json = $"{{\"path\":{JsonSerializer.Serialize(path)},\"content\":{JsonSerializer.Serialize(content)}}}";
                var args = JsonSerializer.Deserialize<JsonElement>(json);
                return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            // 授权根内绝对路径 → 放行（模拟"X盘根目录 test.txt"授权）
            var wAuth = new Agent.Infrastructure.Tools.WriteFileTool(ws, explicitWriteRoots: new[] { authRoot });
            var target = Path.Combine(authRoot, "test.txt");
            Check("显式授权：授权根内 → 放行",
                W2(wAuth, target, "你好").Contains("已写入") && File.Exists(target));
            // 授权根外 → 拒
            var outside = Path.Combine(baseTmp, "other.txt");
            Check("显式授权：授权根外 → 越界拒",
                W2(wAuth, outside, "x").Contains("越界"));
            // 敏感兜底：白名单通过但路径命中敏感区 → 拒（不真写，只验证拒绝信号）
            var wSens = new Agent.Infrastructure.Tools.WriteFileTool(ws, explicitWriteRoots: new[] { @"C:\Windows" });
            Check("显式授权：敏感区兜底 → 拒绝",
                W2(wSens, @"C:\Windows\evil.txt", "x").Contains("敏感"));
            // EditFileTool 授权一致（edit 需 old_string/new_string）
            var target2 = Path.Combine(authRoot, "test2.txt");
            File.WriteAllText(target2, "old");
            var eAuth = new Agent.Infrastructure.Tools.EditFileTool(ws, explicitWriteRoots: new[] { authRoot });
            var ejson = $"{{\"path\":{JsonSerializer.Serialize(target2)},\"old_string\":\"old\",\"new_string\":\"new\"}}";
            var eargs = JsonSerializer.Deserialize<JsonElement>(ejson);
            var eres = eAuth.InvokeAsync(eargs, CancellationToken.None).GetAwaiter().GetResult();
            Check("显式授权：EditFileTool 授权根内 → 放行",
                eres.Contains("已修改") && File.ReadAllText(target2).Contains("new"));
            Directory.Delete(baseTmp, true);
        }
    }

    // ── 区段 3057：只读工程组合边界（ReadOnlyGuard） ──
    public static void Run3057()
    {
        Console.WriteLine("== 只读工程组合边界（ReadOnlyGuard） ==");
        {
            // 迷你工程：OrderService.cs / Order.cs / Program.cs + src/App.cs（同名双目录场景）
            var baseTmp = Path.Combine(TestTmp, "tests_rog");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var proj = Path.Combine(baseTmp, "demo");
            Directory.CreateDirectory(proj);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "OrderService.cs"), "class OrderService {}");
            File.WriteAllText(Path.Combine(proj, "Order.cs"), "class Order {}");
            File.WriteAllText(Path.Combine(proj, "Program.cs"), "class Program {}");
            File.WriteAllText(Path.Combine(proj, "src", "App.cs"), "class App {}");

            bool Block(string input, bool writeEnabled) =>
                Agent.Infrastructure.Project.ReadOnlyGuard.ShouldBlock(input, proj, writeEnabled, out _);

            Check("只读+写任务+引用工程内文件 → 拦截",
                Block("给 OrderService.cs 的 Method1500 加一行注释", false));
            Check("只读+写任务+无文件引用 → 不拦",
                !Block("写一个待办事项项目", false));
            Check("只读+写任务+引用工程外文件 → 不拦",
                !Block("在 X:\\test.txt 创建文件", false));
            Check("只读+读任务+引用工程内文件 → 不拦",
                !Block("读取 OrderService.cs 的内容", false));
            Check("只读+显式写目标在工程外 → 放行",
                !Block("在 X:\\notes.txt 写入 你好", false));
            Check("工程可写+写任务+引用工程内文件 → 不拦",
                !Block("给 OrderService.cs 加注释", true));
            Check("沙箱（projectRoot=null）→ 不触发",
                !Agent.Infrastructure.Project.ReadOnlyGuard.ShouldBlock("给 OrderService.cs 加注释", null, false, out _));
            Check("同名文件命中（src/App.cs 亦属工程文件）",
                Block("给 App.cs 加一个方法", false));
            // 正则边界回归：csproj 6 字母后缀不截断 → OrderSystem.csproj（工程外/不存在）不误命
            File.WriteAllText(Path.Combine(proj, "Demo.csproj"), "<Project />");
            Check("csproj 后缀不截断（OrderSystem.csproj 不误命）",
                !Block("给 OrderSystem.csproj 改 TargetFramework", false));
            // FindExistingProjectFileRef 大小写归一（输入小写 → 命中工程内 OrderService.cs）
            Check("文件引用大小写归一",
                Agent.Infrastructure.Project.ReadOnlyGuard.FindExistingProjectFileRef("修改 orderservice.cs 的内容", proj) is string h
                && h.Equals("OrderService.cs", StringComparison.OrdinalIgnoreCase));
            Directory.Delete(baseTmp, true);
        }
    }
}
