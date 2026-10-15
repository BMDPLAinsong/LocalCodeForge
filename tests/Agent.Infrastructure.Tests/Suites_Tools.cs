// ============================================================
// Suites_Tools.cs
// 工具族回归（写/编辑/读/分析/符号/运行命令/读任务三件套/归因）
// 区段（原 Program.cs 行号）：
//   144 WriteFileTool timeout 静态检查 | 172 PyCodeGuard 交互拒绝 + edit 后处理
//   231 工程感知 P1：写工具双根 + 备份 + 回滚（含 P-D 9a-9h / 方案A）
//   1167 ReadFileTool 分页续读 | 1227 FailureCategorizer 失败类别归类
//   1622 工程图谱（符号索引 + find_symbol）| 1713 analyze_code 大文件
//   1800 CodeAnalyzerTool | 1867 符号地图治理 | 2046 依赖分析
//   2104 symbol 索引增量 | 2256 同文件自重复类型（P-D 扩展）
//   3591 读任务三件套 | 3656 上下文预算 read_file 短路
//   3782 write_unverified 归因 | 3788 CheckProjectName（内嵌块）
//   3803 dotnet run 多项目歧义 | 3824 可疑成功归因（装饰器）
// 依赖：using static AgentTests.TestHarness（Check / TestTmp）；StubLoop 定义于本文件末尾。
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Tools
{
    // ── 区段 144：WriteFileTool 写入时 timeout 静态检查（§14-3h） ──
    public static void Run144()
    {
        Console.WriteLine("== WriteFileTool 写入时 timeout 静态检查（§14-3h） ==");
        {
            var ws = Path.Combine(TestTmp, "tests_timeout");
            if (Directory.Exists(ws)) Directory.Delete(ws, true);
            Directory.CreateDirectory(ws);
            var tool = new Agent.Infrastructure.Tools.WriteFileTool(ws);

            string WriteAndRead(string path, string content)
            {
                var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
                var args = JsonSerializer.Deserialize<JsonElement>(json);
                tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
                return File.ReadAllText(Path.Combine(ws, path.Replace('/', Path.DirectorySeparatorChar)));
            }

            Check("requests.get 无 timeout → 补 timeout=8",
                WriteAndRead("a.py", "r = requests.get(\"http://ip-api.com/json/\")").Contains(", timeout=8"));
            var b = WriteAndRead("b.py", "r = requests.get(\"http://x\", timeout=8)");
            Check("requests.get 已带 timeout → 不动",
                b.Split("timeout=8").Length == 2);
            Check("跨行调用 → 不动",
                !WriteAndRead("c.py", "r = requests.get(\n    \"http://x\"\n)").Contains("timeout"));
            Check("urllib urlopen → 补 timeout",
                WriteAndRead("d.py", "r = urllib.request.urlopen(url)").Contains("urlopen(url, timeout=8)"));
            Check("非 .py 文件 → 不动",
                !WriteAndRead("note.txt", "r = requests.get(\"http://x\")").Contains("timeout"));
        }
    }

    // ── 区段 172：PyCodeGuard 交互拒绝 + edit_file 后处理（§14-3j） ──
    public static void Run172()
    {
        Console.WriteLine("== PyCodeGuard 交互拒绝 + edit_file 后处理（§14-3j） ==");
        {
            var ws = Path.Combine(TestTmp, "tests_guard");
            if (Directory.Exists(ws)) Directory.Delete(ws, true);
            Directory.CreateDirectory(ws);
            var wTool = new Agent.Infrastructure.Tools.WriteFileTool(ws);
            var eTool = new Agent.Infrastructure.Tools.EditFileTool(ws);

            string W(string path, string content)
            {
                var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
                var args = JsonSerializer.Deserialize<JsonElement>(json);
                return wTool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            string R(string path) =>
                File.ReadAllText(Path.Combine(ws, path.Replace('/', Path.DirectorySeparatorChar)));

            Check("write .py 含 input( → 拒绝",
                W("i.py", "x = input(\"请输入: \")").Contains("交互输入"));
            Check("write .cs 含 Console.ReadLine( → 拒绝",
                W("i.cs", "var x = Console.ReadLine();").Contains("交互输入"));
            Check("正常 .py 不被拒",
                !W("ok.py", "print(\"hello\")").Contains("错误"));

            // edit_file：.py 新内容补 timeout + 交互拒绝
            File.WriteAllText(Path.Combine(ws, "e.py"), "r = requests.get(\"http://ip-api.com/json/\")");
            var eJson = $"{{\"path\":\"e.py\",\"old_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")},\"new_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")\nx = input()")}}}";
            var eArgs = JsonSerializer.Deserialize<JsonElement>(eJson);
            var eRet = eTool.InvokeAsync(eArgs, CancellationToken.None).GetAwaiter().GetResult();
            Check("edit .py 新内容含 input( → 拒绝", eRet.Contains("交互输入"));

            var eJson2 = $"{{\"path\":\"e.py\",\"old_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")},\"new_string\":{JsonSerializer.Serialize("r = requests.get(\"http://ip-api.com/json/\")")}}}";
            var eArgs2 = JsonSerializer.Deserialize<JsonElement>(eJson2);
            eTool.InvokeAsync(eArgs2, CancellationToken.None).GetAwaiter().GetResult();
            Check("edit .py 无 timeout → 补 timeout=8",
                R("e.py").Contains(", timeout=8"));

            // 【10-08 B】同文件内自重复类型定义：沙箱模式（无工程）同样拦截——
            // 实证 LetterCaseChecker.cs 900 字符内 2×class LetterCaseChecker → CS0101。
            var selfDupRet = W("Dup.cs",
                "using System;\npublic class LetterCaseChecker\n{\n}\npublic class LetterCaseChecker\n{\n}\n");
            Check("B 沙箱 write_file 自身重复类型 → 拒绝（pd_conflict）",
                selfDupRet.Contains("自身重复定义类型") && selfDupRet.Contains("LetterCaseChecker"));
            Check("B write_file 拒绝 → 文件未创建",
                !File.Exists(Path.Combine(ws, "Dup.cs")));
            // 单类正常写入不受影响
            Check("B 沙箱 write_file 单类 → 成功",
                W("Single.cs", "public class Single { }").Contains("已写入"));
            // edit_file 新内容自身重复类型 → 拒绝
            File.WriteAllText(Path.Combine(ws, "editDup.cs"), "public class Keep { }");
            var eDupJson = $"{{\"path\":\"editDup.cs\",\"old_string\":{JsonSerializer.Serialize("public class Keep { }")},\"new_string\":{JsonSerializer.Serialize("public class Keep { }\npublic class Keep { }")}}}";
            var eDupArgs = JsonSerializer.Deserialize<JsonElement>(eDupJson);
            var eDupRet = eTool.InvokeAsync(eDupArgs, CancellationToken.None).GetAwaiter().GetResult();
            Check("B edit_file 新内容自身重复类型 → 拒绝",
                eDupRet.Contains("自身重复定义类型") && File.ReadAllText(Path.Combine(ws, "editDup.cs")).Contains("Keep { }"));
        }
    }

    // ── 区段 231：工程感知 P1：写工具双根 + 备份 + 回滚 ──
    public static void Run231()
    {
        Console.WriteLine("== 工程感知 P1：写工具双根 + 备份 + 回滚 ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_p1");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "task");      // 任务工作区（沙箱根）
            var proj = Path.Combine(baseTmp, "proj");    // 工程根
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "src", "A.cs"), "// original\n");

            string CallTool(Agent.Infrastructure.Tools.AgentToolBase tool, string json)
            {
                var args = JsonSerializer.Deserialize<JsonElement>(json);
                return tool.InvokeAsync(args, CancellationToken.None).GetAwaiter().GetResult();
            }

            // 1) 沙箱模式（无工程）：写任务根外的路径 → 越界拒绝
            var wSandbox = new Agent.Infrastructure.Tools.WriteFileTool(ws);
            Check("沙箱模式写任务根外 → 越界拒绝",
                W(wSandbox, "../proj/src/X.cs", "x").Contains("越界"));

            // 2) 工程只读（--write 未开）：写基准仍为任务目录 → 不触碰工程根
            var wRo = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, false);
            W(wRo, "src/X.cs", "x");
            Check("工程只读 → 写入任务目录而非工程",
                File.Exists(Path.Combine(ws, "src", "X.cs")) && !File.Exists(Path.Combine(proj, "src", "X.cs")));

            // 3) 工程可写：写工程根新文件 → 成功
            var wRw = new Agent.Infrastructure.Tools.WriteFileTool(ws, proj, true);
            string W(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string content)
            {
                var json = $"{{\"path\":\"{path}\",\"content\":{JsonSerializer.Serialize(content)}}}";
                return CallTool(tool, json);
            }
            string E(Agent.Infrastructure.Tools.AgentToolBase tool, string path, string oldStr, string newStr)
            {
                var json = $"{{\"path\":\"{path}\",\"old_string\":{JsonSerializer.Serialize(oldStr)},\"new_string\":{JsonSerializer.Serialize(newStr)}}}";
                return CallTool(tool, json);
            }
            Check("工程可写创建文件 → 成功",
                W(wRw, "src/B.cs", "// new\n").Contains("已写入")
                && File.Exists(Path.Combine(proj, "src", "B.cs")));

            // 4) 工程可写：覆盖已存在文件 → 自动备份 + 内容变更
            W(wRw, "src/A.cs", "// v2\n");
            var backupsA = Agent.Infrastructure.Project.BackupService.ListBackups(ws);
            Check("覆盖工程文件 → 备份生成",
                backupsA.Any(b => b.Rel.Replace('\\', '/').EndsWith("src/A.cs")));
            Check("覆盖工程文件 → 内容已变",
                File.ReadAllText(Path.Combine(proj, "src", "A.cs")).Contains("v2"));

            // 5) edit_file 工程可写：改工程文件 → 备份 + 内容变更
            var eRw = new Agent.Infrastructure.Tools.EditFileTool(ws, proj, true);
            E(eRw, "src/A.cs", "// v2", "// v3");
            Check("edit 工程文件 → 内容已变",
                File.ReadAllText(Path.Combine(proj, "src", "A.cs")).Contains("v3"));
            Check("edit 工程文件 → 备份已生成",
                Agent.Infrastructure.Project.BackupService.ListBackups(ws)
                    .Any(b => b.Rel.Replace('\\', '/').EndsWith("src/A.cs")));

            // 6) rollback → 恢复最近一次备份（v3 改前备份了 v2）
            var restored = Agent.Infrastructure.Project.BackupService.Rollback(ws, "src/A.cs", proj, "default");
            var contentAfterRollback = File.ReadAllText(Path.Combine(proj, "src", "A.cs"));
            Check("rollback 恢复 → 返回恢复路径", restored is not null);
            Check("rollback 恢复 → 内容回到修改前",
                contentAfterRollback.Contains("v2") && !contentAfterRollback.Contains("v3"));

            // 7) 工程可写：路径穿越（越出工程根）→ 越界拒绝
            Check("工程可写路径穿越 → 越界拒绝",
                W(wRw, "../../../../Windows/test.txt", "x").Contains("越界"));

            // 8) 备份落点与生产接线一致（【10-14 P4-22】）：工具与CLI rollback 收**同一个 run 根**
            //    （生产装配即如此：DependencyInjection 把 run 目录传给写工具；每任务隔离靠
            //    `.backup/<taskId>/`，不靠目录名）。原断言模拟的是 10-08 之前的旧架构
            //    （工具收 runs/<run>/task_xxx、CLI 收 runs/<run>，靠 GetRunRoot 归一），
            //    该启发式已随 P4-22 删除（15 处生产调用点无一命中）→ 此处改为验证现行接线。
            var ws2 = Path.Combine(TestTmp, "run_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(ws2);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "src", "B.cs"), "// v1");
            // 工具收 run 根（与 DependencyInjection 生产装配同参）
            var eTask = new Agent.Infrastructure.Tools.EditFileTool(ws2, proj, true);
            E(eTask, "src/B.cs", "// v1", "// v2");
            Check("工具改工程文件 → 内容已变",
                File.ReadAllText(Path.Combine(proj, "src", "B.cs")).Contains("v2"));
            var backupsRun = Agent.Infrastructure.Project.BackupService.ListBackups(ws2);
            Check("工具（run 根）写入的备份 → run 目录能列出",
                backupsRun.Any(b => b.Rel.Replace('\\', '/').EndsWith("src/B.cs")));

            // 9.5) 【任务4 备份生命周期】PruneBackups：构造 5 个任务备份目录 → 保留最近 2，删更早 3
            var pruneWs = Path.Combine(TestTmp, "run_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(pruneWs);
            for (var i = 1; i <= 5; i++)
            {
                var stampDir = Path.Combine(pruneWs, ".backup", "task_" + i, "000000000");
                Directory.CreateDirectory(stampDir);
                File.WriteAllText(Path.Combine(stampDir, "A.cs"), "// t" + i);
            }
            var pruned = Agent.Infrastructure.Project.BackupService.PruneBackups(pruneWs, 2);
            var remainTasks = Directory.GetDirectories(Path.Combine(pruneWs, ".backup"))
                .Select(Path.GetFileName).OrderByDescending(x => x).ToList();
            Check("PruneBackups 保留最近 2 个（删除 3 个）",
                pruned == 3 && remainTasks.Count == 2
                && remainTasks[0] == "task_5" && remainTasks[1] == "task_4");
            Check("PruneBackups keepLastN=0 → 全清",
                Agent.Infrastructure.Project.BackupService.PruneBackups(pruneWs, 0) == 2
                && !Directory.Exists(Path.Combine(pruneWs, ".backup", "task_5")));

            // 9) 【P-D】写前类型冲突检测：工程可写时，新内容定义工程其他文件已有类型 → 拒绝写入
            //    先给工程种下已有类型：Order.cs 定义 class Order（ShippingService 任务失败根因场景）
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "src", "Order.cs"),
                "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
            // 9a) write_file 覆盖已存在文件但新内容重复定义已有类型 → 拒绝（文件不变）
            var beforeOrderSvc = File.Exists(Path.Combine(proj, "src", "OrderService.cs"))
                ? File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")) : null;
            var pdWrite = W(wRw, "src/OrderService.cs",
                "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\nclass OrderService\n{\n}\n");
            Check("P-D write_file 重复定义已有类型 → 拒绝",
                pdWrite.Contains("类型重复定义") && pdWrite.Contains("Order"));
            Check("P-D 拒绝消息 → 附工程已有类型清单（引导能/不能定义什么）",
                pdWrite.Contains("工程已有类型") && pdWrite.Contains("Order(src/Order.cs)") && pdWrite.Contains("请勿重复定义以上类型"));
            Check("P-D write_file 拒绝 → 目标文件未被写入",
                beforeOrderSvc == null
                    ? !File.Exists(Path.Combine(proj, "src", "OrderService.cs"))
                    : File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")) == beforeOrderSvc);
            // 9b) write_file 新建文件也检测（模型可能新建同名类型文件）→ 拒绝
            var pdNew = W(wRw, "src/OrderDup.cs",
                "using System;\nclass Order\n{\n    public int Id { get; set; }\n}\n");
            Check("P-D write_file 新建文件含已有类型 → 拒绝",
                pdNew.Contains("类型重复定义"));
            Check("P-D 新建文件拒绝 → 文件未创建",
                !File.Exists(Path.Combine(proj, "src", "OrderDup.cs")));
            // 9b1) 10-04 增量修改引导：新建文件拒绝消息 → 含"是新建文件"指引（数据驱动，告诉模型下一步该做什么）
            Check("P-D 新建文件拒绝消息 → 含'是新建文件'指引",
                pdNew.Contains("是新建文件") && pdNew.Contains("OrderDup.cs") && pdNew.Contains("edit_file"));
            // 9b2) 目标文件已存在（覆盖）场景 → 消息含 read_file/edit_file 局部修改指引（不整体重写）
            W(wRw, "src/OrderService.cs", "using System;\nclass OrderService\n{\n    public int Id { get; set; }\n}\n");
            var pdExist = W(wRw, "src/OrderService.cs",
                "using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n");
            Check("P-D 已存在文件拒绝消息 → 含 read_file/edit_file 局部修改指引",
                pdExist.Contains("已存在于工程") && pdExist.Contains("read_file")
                && pdExist.Contains("edit_file") && pdExist.Contains("不要 write_file 整体重写"));
            Check("P-D 已存在文件拒绝 → 文件保持原状（未被覆盖）",
                File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")).Contains("public int Id"));
            // 9c) 目标文件自身已有该类型（partial 场景）→ 不算冲突（扫描排除目标文件）
            var pdSelf = W(wRw, "src/Order.cs",
                "using System;\nclass Order\n{\n    public int Id { get; set; }\n    public string? Status { get; set; }\n}\n");
            Check("P-D 目标文件自身含该类型 → 放行（partial 语义）",
                pdSelf.Contains("已写入"));
            // 9d) 无冲突正常写入 → 放行
            var pdOk = W(wRw, "src/NewService.cs",
                "using System;\nclass NewService\n{\n    public int Run() => 1;\n}\n");
            Check("P-D 无冲突写入 → 成功",
                pdOk.Contains("已写入") && File.Exists(Path.Combine(proj, "src", "NewService.cs")));
            // 9e) edit_file 引入已有类型定义 → 拒绝
            var pdEdit = E(eRw, "src/NewService.cs",
                "class NewService", "class Order\n{\n}\nclass NewService");
            Check("P-D edit_file 引入已有类型 → 拒绝",
                pdEdit.Contains("类型重复定义") && pdEdit.Contains("Order"));
            Check("P-D edit_file 拒绝 → 文件未变",
                File.ReadAllText(Path.Combine(proj, "src", "NewService.cs")).Contains("Run"));
            // 9f) TypeConflictDetector 单元：ExtractTypeNames 覆盖五种类型关键字 + 注释/字符串不误报
            var pdTypes = Agent.Infrastructure.Project.TypeConflictDetector.ExtractTypeNames(
                "using System;\n// class Fake\nclass Real { }\ninterface IThing { }\nenum Color { Red }\nstruct Point { }\nrecord Person(string Name);\nstring s = \"class InString\";");
            Check("P-D ExtractTypeNames → 提取 5 类 + 排除注释/字符串",
                pdTypes.Contains("Real") && pdTypes.Contains("IThing") && pdTypes.Contains("Color")
                && pdTypes.Contains("Point") && pdTypes.Contains("Person")
                && !pdTypes.Contains("Fake") && !pdTypes.Contains("InString"));
            // 9g) 【B】P-D 熔断语义化：同路径第 2 次被拒 → 确定性指令（禁止再 write_file，强制 edit_file）。
            //     实证：模型被 P-D 拒后不换 edit_file、反复 write_file → 3 连拒熔断白跑。
            //     第 2 次拒绝即返回强指令（数据驱动引导，非提示词）。
            var pdAgain = W(wRw, "src/OrderService.cs",
                "using System;\nclass Order\n{\n}\nclass OrderService\n{\n}\n");
            Check("P-D 连续拒绝 → 熔断指令（第 3 次 + 禁止 write_file + 强制 edit_file）",
                pdAgain.Contains("第 3 次") && pdAgain.Contains("禁止再次使用 write_file")
                && pdAgain.Contains("edit_file") && pdAgain.Contains("类型重复被拒绝"));
            Check("P-D 二连拒 → 文件仍未变",
                File.ReadAllText(Path.Combine(proj, "src", "OrderService.cs")).Contains("public int Id"));
            // 9h) 不同路径的拒绝计数独立（写其他文件第 1 次仍为普通拒绝）
            var pdOther1 = W(wRw, "src/OrderDup2.cs",
                "using System;\nclass Order\n{\n}\n");
            Check("P-D 其他路径第 1 次 → 普通拒绝（非熔断）",
                pdOther1.Contains("类型重复定义") && !pdOther1.Contains("第 2 次"));

            // 【方案A】工程模式写前拦截项目文件：防"侧路新建同名项目"假完成
            // （模型新建 OrderSystem.csproj + 全套文件 → dotnet run 编译通过 = 假完成，demo 工程未接入）
            var aCsproj = W(wRw, "OrderSystem.csproj", "<Project></Project>");
            Check("方案A write_file 新建 .csproj → 拒绝",
                aCsproj.Contains("工程模式禁止修改/新建项目文件") && aCsproj.Contains("退出码：126"));
            Check("方案A write_file 拒绝 → 文件未创建",
                !File.Exists(Path.Combine(proj, "OrderSystem.csproj")));
            Check("方案A edit_file 改 .csproj → 拒绝",
                E(eRw, "lawcheck_demo.csproj", "<Project>", "<Project>").Contains("工程模式禁止修改项目文件"));
            Check("方案A write_file 新建 .sln → 拒绝",
                W(wRw, "demo.sln", "x").Contains("工程模式禁止修改/新建项目文件"));
            var restoredB = Agent.Infrastructure.Project.BackupService.Rollback(ws2, "src/B.cs", proj, "default");
            Check("run 目录 rollback → 恢复工具产生的备份（同一run 根）",
                restoredB is not null && File.ReadAllText(Path.Combine(proj, "src", "B.cs")).Contains("v1"));
        }
    }

    // ── 区段 1167：1 ReadFileTool 分页续读 ──
    public static void Run1167()
    {
        Console.WriteLine("== 1 ReadFileTool 分页续读 ==");
        {
            var rfDir = Path.Combine(TestTmp, "rf_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(rfDir);
            var rfProj = Path.Combine(rfDir, "proj");
            Directory.CreateDirectory(rfProj);

            // 大文件（>200KB）：6000 行 × 填充 ≈ 600KB
            var bigPath = Path.Combine(rfProj, "big.txt");
            var bigContent = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"line{i} " + new string('x', 90)));
            File.WriteAllText(bigPath, bigContent);

            var rfTool = new Agent.Infrastructure.Tools.ReadFileTool(rfDir, rfProj);
            var ctNone2 = CancellationToken.None;

            // 【大文件首读重定向】工程模式 + 大文件 + 无 offset → 拒绝 + 定位引导（强制走 find_symbol/analyze_code）
            var jBig = "{\"path\":\"big.txt\"}";
            var argsBig = JsonSerializer.Deserialize<JsonElement>(jBig);
            var rBig = rfTool.InvokeAsync(argsBig, ctNone2).GetAwaiter().GetResult();
            Check("工程模式大文件无 offset → 拒绝 + 定位引导",
                rBig.Contains("错误：文件过大") && rBig.Contains("find_symbol") && rBig.Contains("offset/limit"));

            // 显式 offset 续读：501-1000（重定向放行——带 offset 即精确读取）
            var jOff = "{\"path\":\"big.txt\",\"offset\":501,\"limit\":500}";
            var argsOff = JsonSerializer.Deserialize<JsonElement>(jOff);
            var rOff = rfTool.InvokeAsync(argsOff, ctNone2).GetAwaiter().GetResult();
            Check("大文件 offset 续读 → 501-1000 行",
                rOff.Contains("已返回第 501-1000 行") && rOff.Contains("line501") && rOff.Contains("offset=1001"));

            // 【重定向收紧】仅 limit 无 offset → 仍拒绝（模型传 limit 绕过定位的实证修复）
            var jLim = "{\"path\":\"big.txt\",\"limit\":500}";
            var argsLim = JsonSerializer.Deserialize<JsonElement>(jLim);
            var rLim = rfTool.InvokeAsync(argsLim, ctNone2).GetAwaiter().GetResult();
            Check("大文件仅 limit 无 offset → 仍拒绝（防绕过）",
                rLim.Contains("错误：文件过大") && rLim.Contains("find_symbol"));

            // 末页：无续读提示
            var jTail = "{\"path\":\"big.txt\",\"offset\":5501,\"limit\":1000}";
            var argsTail = JsonSerializer.Deserialize<JsonElement>(jTail);
            var rTail = rfTool.InvokeAsync(argsTail, ctNone2).GetAwaiter().GetResult();
            Check("大文件末页 → 无续读提示",
                rTail.Contains("已返回第 5501-6000 行") && !rTail.Contains("offset="));

            // 非工程模式（无 projectRoot）大文件无 offset → 保持分页（向后兼容；相对路径带 proj/ 前缀）
            var rfTool2 = new Agent.Infrastructure.Tools.ReadFileTool(rfDir);
            var argsBigRel = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"proj/big.txt\"}");
            var rBig2 = rfTool2.InvokeAsync(argsBigRel, ctNone2).GetAwaiter().GetResult();
            Check("非工程模式大文件无 offset → 仍分页（兼容）",
                rBig2.Contains("共 6000 行") && rBig2.Contains("已返回第 1-500 行") && rBig2.Contains("offset=501"));

            // 小文件默认：全文无头（向后兼容）
            var smallPath = Path.Combine(rfProj, "small.txt");
            File.WriteAllText(smallPath, "hello\nworld");
            var argsSmall = JsonSerializer.Deserialize<JsonElement>("{\"path\":\"small.txt\"}");
            var rSmall = rfTool.InvokeAsync(argsSmall, ctNone2).GetAwaiter().GetResult();
            Check("小文件默认 → 全文无分页头",
                rSmall == "hello\nworld");
        }
    }

    // ── 区段 1227：3 FailureCategorizer 失败类别归类 ──
    public static void Run1227()
    {
        Console.WriteLine("== 3 FailureCategorizer 失败类别归类 ==");
        {
            var ctNone3 = CancellationToken.None;
            Check("类别：外部凭据 401", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误: 401 - {\"cod\":401, \"message\": \"Invalid API key\"}") == "external_credential");
            Check("类别：外部凭据 403", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("获取位置信息失败: 403") == "external_credential");
            Check("类别：编译错误 CS", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("error CS0272: 属性或索引器不能使用") == "compile_error");
            Check("类别：运行崩溃 Traceback", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("Traceback (most recent call last): ZeroDivisionError") == "runtime_error");
            Check("类别：超时", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：命令执行超时（>30秒）。\n退出码：124") == "timeout");
            Check("类别：工具拒绝 P-D", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：检测到类型重复定义：Order。\n请勿重复定义") == "tool_rejected");
            Check("类别：工具拒绝 白名单", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：命令 python -c 不在白名单中。\n退出码：127") == "tool_rejected");
            Check("类别：未找到", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("错误：文件不存在：pyst.py") == "not_found");
            Check("类别：空响应", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("模型连续返回空答案，任务终止") == "empty_response");
            Check("类别：其他兜底", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("未知错误 XYZ") == "other");
            Check("类别：成功不归类（空入参）", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("") == "other");
            // 批次2 数据驱动补洞：熔断/完成门实证消息
            Check("类别：写文件但验证未通过 → write_unverified", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务写入文件但验证未通过（run_command 未成功），熔断终止，防假成功。") == "write_unverified");
            Check("类别：写任务未产生写入 → write_not_attempted", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务要求修改文件但未产生任何写入（仅循环读取），熔断终止。可重试或改用 find_symbol/analyze_code 定位后编辑。") == "write_not_attempted");
            Check("类别：目标不存在 → target_mismatch", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务要求写入 X:\\a\\b.txt，但该目标不存在（本轮写入位置与用户指定不符）。") == "target_mismatch");
            Check("类别：写被拒未落地 → tool_rejected", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("写入被拒绝且本轮未成功写入任何文件（写目标未落地）。可检查路径是否越界/敏感，或改用工程内路径后重试。") == "tool_rejected");
            Check("类别：验证失败 → validator_failed", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证（重试 2 次）。最后错误：退出码：1") == "validator_failed");
            Check("类别：回填标注 → validator_failed", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证 [父任务未通过验证（回填：顶层 orchestrator 失败）]") == "validator_failed");
            Check("类别：严格校验不一致 → validator_mismatch", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("任务未通过验证（重试 0 次）。最后错误：退出码：0\n--- stdout ---\n订单总金额: 103\n[检测] 输出与计划预期不完全一致（严格模式：预期每一行须在输出中精确出现）。") == "validator_mismatch");
            Check("类别：修复输出相同 → repair_unimproved", Agent.Infrastructure.Metrics.FailureCategorizer.Categorize("修复失败：模型输出与原文相同。错误信息：退出码：0\n--- stdout ---\n城市: Kunming\n天气状况: Light rain shower") == "repair_unimproved");

            // 批次3·B：ExtractMissingType 类型缺失提取（弯/直引号）
            Check("提取：弯引号 CS0117 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("Program.cs(11,13): error CS0117: “Order”未包含“Items”的定义") == "Order");
            Check("提取：直引号 CS0117 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("Program.cs(11,13): error CS0117: \"Order\"未包含\"Items\"的定义") == "Order");
            Check("提取：CS0246 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("DiscountService.cs(5,37): error CS0246: 未能找到类型或命名空间名“Order”(是否缺少 using 指令)") == "Order");
            Check("提取：CS1061 → Order", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("OrderService.cs(15,15): error CS1061: “Order”未包含“TotalAmount”的定义") == "Order");
            Check("提取：无类型缺失 → null", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("error CS5001: 程序不包含适合于入口点的静态 Main 方法") is null);
            Check("提取：空输入 → null", Agent.Infrastructure.Orchestration.Orchestrator.ExtractMissingType("") is null);

            // 10-15 方向1：工程增量编译/运行验证命令判定（expected 无基准 → 豁免精确断言）
            var progRun = typeof(Agent.Infrastructure.Orchestration.Orchestrator)
                .GetMethod("IsProgramRunCommand", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            bool ProgRun(string cmd) => (bool)progRun.Invoke(null, new object?[] { cmd })!;
            Check("执行类判定：dotnet run → advisory（工程增量验证）", ProgRun("dotnet run --project lawcheck_demo.csproj"));
            Check("执行类判定：dotnet build/test → advisory", ProgRun("dotnet build") && ProgRun("dotnet test"));
            Check("执行类判定：python/node 运行 → advisory", ProgRun("python main.py") && ProgRun("node app.js"));
            Check("执行类判定：非执行命令 → 不豁免", !ProgRun("type index.html") && !ProgRun("git status") && !ProgRun("dir"));

            // SqliteMetricsCollector 落库 failure_category
            var fcDir = Path.Combine(TestTmp, "fc_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(fcDir);
            var fcDb = Path.Combine(fcDir, "metrics.db");
            var fcMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(fcDb);
            var ts = DateTimeOffset.UtcNow;
            fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "fc_fail_1", SessionId = "s1", UserInput = "测试编译失败", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
                Success = false, IsSuspicious = false, RetryCount = 1,
                TotalMs = 100, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "error CS0101: 重复定义", StartedAt = ts, FinishedAt = ts
            }, ctNone3).Wait();
            fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "fc_ok_1", SessionId = "s1", UserInput = "测试成功", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
                Success = true, IsSuspicious = false, RetryCount = 0,
                TotalMs = 50, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "", StartedAt = ts, FinishedAt = ts
            }, ctNone3).Wait();
            var fb = fcMetrics.GetFailureBreakdownAsync(ctNone3).Result;
            Check("失败类别落库 → compile_error:1（成功不统计）",
                fb.Count == 1 && fb[0].Category == "compile_error" && fb[0].Count == 1);

            // 可疑成功落库 + 明细读取（供 stats 可疑明细块）
            fcMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "fc_sus_1", SessionId = "s1", UserInput = "写 hello.txt 并验证", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "large", RoutingReason = "test",
                Success = true, IsSuspicious = true, RetryCount = 0,
                TotalMs = 30, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "", StartedAt = ts, FinishedAt = ts
            }, ctNone3).Wait();
            var susRecent = fcMetrics.GetRecentAsync(50, ctNone3).Result
                .Where(m => m.IsSuspicious && m.Success).ToList();
            Check("可疑成功落库 → GetRecentAsync 可过滤出明细",
                susRecent.Count == 1 && susRecent[0].TaskId == "fc_sus_1");

            // 【10-08 metrics 数据治理】E2E 标记：is_e2e 落库 + 真实统计剔除 + 单列计数
            var e2eMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(
                Path.Combine(fcDir, "e2e.db"));
            var e2eNow = DateTimeOffset.UtcNow;
            Agent.Core.Models.TaskMetric MkE2E(string tid, bool ok) => new Agent.Core.Models.TaskMetric
            {
                TaskId = tid, SessionId = "s1", UserInput = "E2E 用例", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
                Success = ok, IsSuspicious = false, IsE2E = true, RetryCount = 0,
                TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = ok ? "" : "case_incremental 故意失败", StartedAt = e2eNow, FinishedAt = e2eNow
            };
            e2eMetrics.RecordAsync(MkE2E("e2e_ok", true), ctNone3).Wait();
            e2eMetrics.RecordAsync(MkE2E("e2e_fail", false), ctNone3).Wait();
            e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "real_1", SessionId = "s1", UserInput = "真实任务", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
                Success = true, IsSuspicious = false, IsE2E = false, RetryCount = 0,
                TotalMs = 20, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "", StartedAt = e2eNow, FinishedAt = e2eNow
            }, ctNone3).Wait();
            var e2eSummary = e2eMetrics.GetSummaryAsync(null, ctNone3).Result;
            Check("E2E 治理：真实统计剔除 E2E（1 真实成功 / 0 失败）",
                e2eSummary.TotalCount == 1 && e2eSummary.SuccessCount == 1 && e2eSummary.FailureCount == 0);
            Check("E2E 治理：E2E 计数单列 = 2", e2eMetrics.GetE2ECountAsync(ctNone3).Result == 2);
            var e2eRecent = e2eMetrics.GetRecentAsync(10, ctNone3).Result;
            Check("E2E 治理：GetRecentAsync 过滤 E2E（仅真实任务）",
                e2eRecent.Count == 1 && e2eRecent[0].TaskId == "real_1");
            var e2eFail = e2eMetrics.GetRecentFailuresAsync(10, ctNone3).Result;
            Check("E2E 治理：GetRecentFailuresAsync 过滤 E2E（E2E 故意失败不计入）", e2eFail.Count == 0);

            // 【10-10 失败类别统计补洞】GetFailureBreakdownAsync 此前漏 is_e2e=0 过滤，
            // 导致 compile_error/validator_failed 被 E2E 用例严重污染（实测 21→2、19→0）。
            // 同库并存真实失败 + 2 条 E2E 失败，断言失败类别只统计真实记录。
            e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "e2e_fail_cs", SessionId = "s1", UserInput = "E2E 用例", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
                Success = false, IsSuspicious = false, IsE2E = true, RetryCount = 0,
                TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "error CS0101: E2E 重复定义", StartedAt = e2eNow, FinishedAt = e2eNow
            }, ctNone3).Wait();
            e2eMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "real_fail_1", SessionId = "s1", UserInput = "真实任务", Mode = "agent",
                RoutingTier = "Rule", ServiceId = "small", RoutingReason = "test",
                Success = false, IsSuspicious = false, IsE2E = false, RetryCount = 0,
                TotalMs = 20, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "error CS0201: 真实类型冲突", StartedAt = e2eNow, FinishedAt = e2eNow
            }, ctNone3).Wait();
            var e2eFb = e2eMetrics.GetFailureBreakdownAsync(ctNone3).Result;
            Check("失败类别统计排除 E2E：仅 1 条真实 compile_error（2 条 E2E 失败不计入）",
                e2eFb.Count == 1 && e2eFb[0].Category == "compile_error" && e2eFb[0].Count == 1);

            // 批次3·C 父失败回填：顶层失败 → Executor 子记录成功回填为失败
            var bfMetrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(
                Path.Combine(fcDir, "backfill.db"));
            var bfNow = DateTimeOffset.UtcNow;
            Agent.Core.Models.TaskMetric MkSub(string tid, bool ok, DateTimeOffset at) => new Agent.Core.Models.TaskMetric
            {
                TaskId = tid, SessionId = "", UserInput = "做一个 C# 项目", Mode = "orchestrator_sub",
                RoutingTier = "OrchestratorExecutor", ServiceId = "small", RoutingReason = "orchestrator 内部调用（子指标）",
                Success = ok, IsSuspicious = false, RetryCount = 0,
                TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "", StartedAt = at, FinishedAt = at
            };
            bfMetrics.RecordAsync(MkSub("bf_win", true, bfNow), ctNone3).Wait();
            bfMetrics.RecordAsync(MkSub("bf_fail", false, bfNow), ctNone3).Wait();
            bfMetrics.RecordAsync(MkSub("bf_old", true, bfNow.AddMinutes(-10)), ctNone3).Wait();
            var bfCount = bfMetrics.BackfillFailedSubMetricsAsync("做一个 C# 项目", bfNow.AddSeconds(-5), "父任务未通过验证（回填）", ctNone3).Result;
            Check("父失败回填：窗口内成功 Executor 子记录 1 条被回填", bfCount == 1);
            var bfWin = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_win");
            Check("回填后 success=0 + validator_failed", !bfWin.Success && bfWin.ErrorSummary.Contains("父任务未通过验证"));
            var bfOld = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_old");
            Check("窗口外历史记录不被回填", bfOld.Success);
            var bfFail = bfMetrics.GetRecentAsync(50, ctNone3).Result.First(m => m.TaskId == "bf_fail");
            Check("原本失败的记录不受影响", !bfFail.Success);

            // 【10-10 真实口径下沉】护栏：数据闭环（QueryStats）/ 时代隔离（EpochBreakdown）走 task_metrics_real 视图，
            // 不再依赖"每个查询记得加 is_e2e=0"的脆弱约定。e2e.db 共 5 条：3 条 E2E（2 失败 1 成功）+ 2 条真实（1 失败 1 成功）。
            var vwStats = e2eMetrics.GetServiceStatsAsync("small", "Rule", 10, ctNone3).Result;
            Check("真实口径视图：数据闭环排除 E2E（5 条 → 真实 2 条，成功 1）",
                vwStats.TotalCount == 2 && vwStats.SuccessCount == 1);
            var vwEpoch = e2eMetrics.GetEpochBreakdownAsync("small", e2eNow.AddSeconds(-3600), ctNone3).Result;
            Check("真实口径视图：时代隔离排除 E2E（Current 真实 2 条 / Legacy 0）",
                vwEpoch.Current.TotalCount == 2 && vwEpoch.Current.SuccessCount == 1 && vwEpoch.Legacy.TotalCount == 0);

            // 【10-10 真实口径下沉】父失败回填（UPDATE）排除 E2E 子记录：窗口内新增一条 E2E 成功子记录，
            // 修复前会被错误回填（返回 1），修复后返回 0（E2E 保持成功）。
            bfMetrics.RecordAsync(new Agent.Core.Models.TaskMetric
            {
                TaskId = "bf_e2e_sub", SessionId = "", UserInput = "做一个 C# 项目", Mode = "orchestrator_sub",
                RoutingTier = "OrchestratorExecutor", ServiceId = "small", RoutingReason = "orchestrator 内部调用（子指标）",
                Success = true, IsSuspicious = false, IsE2E = true, RetryCount = 0,
                TotalMs = 10, PlannerMs = 0, ExecutorMs = 0, ValidatorMs = 0,
                LintBlockingErrors = 0, LintWarningErrors = 0,
                ErrorSummary = "", StartedAt = bfNow, FinishedAt = bfNow
            }, ctNone3).Wait();
            var bfCount2 = bfMetrics.BackfillFailedSubMetricsAsync("做一个 C# 项目", bfNow.AddSeconds(-5), "父任务未通过验证（回填2）", ctNone3).Result;
            Check("父失败回填排除 E2E：返回计数 0（E2E 子记录保持成功，无真实可回填）", bfCount2 == 0);
        }
    }

    // ── 区段 1622：工程图谱（符号索引 + find_symbol） ──
    public static void Run1622()
    {
        Console.WriteLine("== 工程图谱（符号索引 + find_symbol） ==");
        {
            var proj = Path.Combine(TestTmp, "sym_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(proj);
            Directory.CreateDirectory(Path.Combine(proj, "src"));
            File.WriteAllText(Path.Combine(proj, "Order.cs"), """
                using System;
                public class Order
                {
                    public int Id { get; set; }
                    public string Process(int amount) { return amount.ToString(); }
                }
                """);
            File.WriteAllText(Path.Combine(proj, "src", "weather.py"), """
                import requests
                def get_weather(city):
                    return city
                class WeatherService:
                    def fetch(self, ip):
                        return ip
                """);

            // 多修饰符 + 控制流误判回归（真实工程形态）
            File.WriteAllText(Path.Combine(proj, "src", "Extra.cs"), """
                public static class Router
                {
                    public static bool TryRoute(string x)
                    {
                        if (x.Length > 0)
                            return true;
                        for (var i = 0; i < 3; i++) { }
                        return false;
                    }
                }
                public sealed record Stats(int Count, int Ok);
                """);

            var entries2 = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
            // 探针：直接正则匹配（排除 Build 逻辑干扰）
            var probe = System.Text.RegularExpressions.Regex.Match("public static class Router",
                @"^\s*(?:(?:public|private|protected|internal|static|abstract|sealed|partial|readonly)\s+)*(?:class|interface|enum|record|struct)\s+([A-Za-z_]\w*)");
            Check("探针：正则直接匹配 Router", probe.Success && probe.Groups[1].Value == "Router");
            Check("多修饰符类（public static class）", entries2.Any(e => e.Symbol == "Router" && e.Kind == "class"));
            Check("多修饰符 record", entries2.Any(e => e.Symbol == "Stats" && e.Kind == "record"));
            Check("多修饰符方法", entries2.Any(e => e.Symbol == "TryRoute" && e.Kind == "method"));
            Check("控制流 if 不误判为方法", !entries2.Any(e => e.Symbol == "if" && e.Kind == "method"));
            Check("控制流 for 不误判为方法", !entries2.Any(e => e.Symbol == "for" && e.Kind == "method"));

            var entries = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
            Check("C# 类索引", entries.Any(e => e.Symbol == "Order" && e.Kind == "class"));
            Check("C# 方法索引", entries.Any(e => e.Symbol == "Process" && e.Kind == "method"));
            Check("C# 属性索引", entries.Any(e => e.Symbol == "Id" && e.Kind == "property"));
            Check("Python 函数索引", entries.Any(e => e.Symbol == "get_weather" && e.Kind == "function"));
            Check("Python 类索引", entries.Any(e => e.Symbol == "WeatherService" && e.Kind == "class"));
            Check("Python 缩进方法索引", entries.Any(e => e.Symbol == "fetch" && e.Kind == "method"));

            // find_symbol 端到端（惰性构建 + 查询）
            var db = Path.Combine(TestTmp, "symdb_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var work = Path.Combine(TestTmp, "symwork_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(work);
            var fst = new Agent.Infrastructure.Tools.FindSymbolTool(work, proj, new Agent.Infrastructure.Project.SymbolIndexStore(db));
            var out1 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"name\":\"Order\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("find_symbol 命中 Order", out1.Contains("class Order") && out1.Contains("Order.cs"));
            var out2 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"name\":\"get_weather\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("find_symbol 命中 Python 函数", out2.Contains("function get_weather"));
            var out3 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"name\":\"NoSuchSymbol\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("find_symbol 未命中 → 提示", out3.Contains("未找到"));
            // 无工程 → 拒绝
            var fst2 = new Agent.Infrastructure.Tools.FindSymbolTool(work, null, new Agent.Infrastructure.Project.SymbolIndexStore(db));
            var out4 = fst2.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"name\":\"Order\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("无工程模式 → 拒绝", out4.Contains("工程模式"));

            // 【新鲜度】已索引后新增文件 → 增量刷新命中（旧索引查不到新符号的回归）
            // （方法必须独立成行——行首正则不捕单行类内嵌套方法）
            File.WriteAllText(Path.Combine(proj, "NewThing.cs"),
                "public class NewThing\n{\n    public void FreshMethod() {}\n}\n");
            var out5 = fst.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"name\":\"FreshMethod\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("索引后新增符号 → 增量刷新可命中", out5.Contains("FreshMethod") && out5.Contains("NewThing.cs"));

            // 【定位后执行引导】find_symbol 命中 → 返回含 offset 操作指引（数据驱动）
            Check("find_symbol 命中 → 注入 offset 操作指引",
                out5.Contains("操作指引") && out5.Contains("offset=") && out5.Contains("edit_file"));

            Directory.Delete(proj, true);
            Directory.Delete(work, true);
        }
    }

    // ── 区段 1713：analyze_code 大文件支持（重定向引导打通） ──
    public static void Run1713()
    {
        Console.WriteLine("== analyze_code 大文件支持 ==");
        {
            var acDir = Path.Combine(TestTmp, "ac_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(acDir);
            var big = Path.Combine(acDir, "Big.cs");
            // 300KB 大文件（原 200KB 限制拒绝）
            var bigContent = "using System;\npublic class BigSvc {\n" +
                string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"    public int Method{i}(int x) {{ return x + {i}; }}")) +
                "\n}\n";
            File.WriteAllText(big, bigContent);
            var acTool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(acDir, acDir);
            var acArgs = System.Text.Json.JsonDocument.Parse("{\"path\":\"Big.cs\"}").RootElement;
            var acOut = acTool.InvokeAsync(acArgs, CancellationToken.None).GetAwaiter().GetResult();
            Check("analyze_code 大文件(300KB) → 出符号地图（截断受控）",
                acOut.Contains("符号总数: 4001") && !acOut.Contains("文件过大") && acOut.Contains("... 省略"));
            Directory.Delete(acDir, true);
        }
    }

    // ── 区段 1800：CodeAnalyzerTool（确定性符号索引） ──
    public static void Run1800()
    {
        Console.WriteLine("== CodeAnalyzerTool（确定性符号索引） ==");
        {
            var anDir = Path.Combine(TestTmp, "analyzer_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(anDir);

            // C# 样本
            var csPath = Path.Combine(anDir, "Order.cs");
            File.WriteAllText(csPath, """
                using System;

                public class Order
                {
                    public int Id { get; set; }

                    public string Process(int amount)
                    {
                        return amount.ToString();
                    }
                }
                """);

            var tool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(anDir);
            var csOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"Order.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("C# 语言识别", csOut.Contains("语言: csharp"));
            Check("C# 类定位（行号 3）", csOut.Contains("[3] class Order"));
            Check("C# 属性定位", csOut.Contains("属性 Id"));
            Check("C# 方法定位", csOut.Contains("方法 Process"));

            // Python 样本
            var pyPath = Path.Combine(anDir, "weather.py");
            File.WriteAllText(pyPath, """
                import requests

                def get_weather(city):
                    return city

                class WeatherService:
                    def fetch(self, ip):
                        return ip
                """);
            var pyOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"weather.py\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("Python 语言识别", pyOut.Contains("语言: python"));
            Check("Python 函数定位", pyOut.Contains("函数 get_weather"));
            Check("Python 类定位", pyOut.Contains("class WeatherService"));
            Check("Python 缩进方法定位", pyOut.Contains("方法 fetch"));

            // 错误路径
            var missOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"no_such.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("文件不存在 → 错误", missOut.Contains("文件不存在"));
            var escOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"..\\\\..\\\\Windows\\\\x.txt\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("路径越界 → 拒绝", escOut.Contains("越界"));

            // 非代码文件
            var txtPath = Path.Combine(anDir, "readme.txt");
            File.WriteAllText(txtPath, "hello");
            var txtOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"readme.txt\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("非代码文件 → 无符号", txtOut.Contains("非代码文件"));

            Directory.Delete(anDir, true);
        }
    }

    // ── 区段 1867：符号地图治理（截断 + range） ──
    public static void Run1867()
    {
        Console.WriteLine("== 符号地图治理（截断 + range） ==");
        {
            var anDir = Path.Combine(TestTmp, "symmap_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(anDir);

            // 生成 5003 方法的大文件（复刻 BigService 规模）
            var bigPath = Path.Combine(anDir, "BigService.cs");
            var sbBig = new System.Text.StringBuilder();
            sbBig.AppendLine("using System;");
            sbBig.AppendLine("public class BigService {");
            for (var i = 0; i < 5003; i++)
                sbBig.AppendLine($"    public int Method{i}(int x) {{ return x + {i}; }}");
            sbBig.AppendLine("}");
            File.WriteAllText(bigPath, sbBig.ToString());

            var tool = new Agent.Infrastructure.Tools.CodeAnalyzerTool(anDir, anDir);

            // ① 全量模式 → 截断 + 省略行 + 定向引导 + 符号总数仍准确
            var bigOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"BigService.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("治理：全量符号地图 → 截断（省略行出现）", bigOut.Contains("... 省略"));
            Check("治理：截断提示含定向引导（find_symbol/range）",
                bigOut.Contains("find_symbol") && bigOut.Contains("range="));
            Check("治理：符号总数仍准确（5005 = 1 类 + 5003 方法 + 1 兜底）",
                bigOut.Contains("符号总数: 5004"));
            Check("治理：未输出超限符号（Method4999 不在前 120 内）", !bigOut.Contains("方法 Method4999"));
            Check("治理：输出长度受控（≤ 6KB）", bigOut.Length < 6 * 1024);

            // ② range 参数 → 只分析该行段
            var rangeOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"BigService.cs\",\"range\":\"1500-1505\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("range：只含该段符号（Method1499 行 1502 / Method1500 行 1503）",
                rangeOut.Contains("[1502] 方法 Method1499") &&
                rangeOut.Contains("[1503] 方法 Method1500") &&
                rangeOut.Contains("方法 Method1502"));
            Check("range：不含段外符号（Method0 / Method4999）",
                !rangeOut.Contains("方法 Method0") && !rangeOut.Contains("方法 Method4999"));
            Check("range：无省略行（段内符号少）", !rangeOut.Contains("... 省略"));
            Check("range：头部注明行段", rangeOut.Contains("分析行段: 1500-1505"));

            // ③ range 非法/超界 → 用法提示
            var bad1 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"BigService.cs\",\"range\":\"abc\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("range 非法格式 → 用法提示", bad1.Contains("range 格式应为"));
            var bad2 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"BigService.cs\",\"range\":\"6000-6002\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("range 起点超界 → 提示", bad2.Contains("超出文件总行数"));
            var bad3 = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"BigService.cs\",\"range\":\"99-10\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("range 起>止 → 提示", bad3.Contains("起 ≤ 止"));

            // ④ 小文件回归：无截断
            var smallPath = Path.Combine(anDir, "Small.cs");
            File.WriteAllText(smallPath, """
                public class Small
                {
                    public int A() { return 1; }
                }
                """);
            var smallOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"Small.cs\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("小文件：无截断 + 全量符号", !smallOut.Contains("... 省略") && smallOut.Contains("方法 A"));

            // ⑤ Python 同样生效
            var pyBig = Path.Combine(anDir, "pybig.py");
            var sbPy = new System.Text.StringBuilder();
            for (var i = 0; i < 350; i++)
                sbPy.AppendLine($"def fn{i}():\n    pass");
            File.WriteAllText(pyBig, sbPy.ToString());
            var pyOut = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"pybig.py\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("Python：超限截断生效", pyOut.Contains("... 省略") && pyOut.Contains("符号总数: 350"));
            var pyRange = tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"pybig.py\",\"range\":\"5-10\"}").RootElement, CancellationToken.None).GetAwaiter().GetResult();
            Check("Python：range 命中段内函数（fn4 在行 9）",
                pyRange.Contains("函数 fn4") && !pyRange.Contains("函数 fn0"));

            Directory.Delete(anDir, true);
        }
    }

    // ── 区段 2046：工程图谱增强：依赖分析（符号引用 → 文件依赖边） ──
    public static void Run2046()
    {
        Console.WriteLine("== 工程图谱增强：依赖分析 ==");
        {
            var proj = Path.Combine(TestTmp, "deps_proj_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "Order.cs"),
                "namespace Demo;\npublic class Order { public int Id { get; set; } }\n");
            File.WriteAllText(Path.Combine(proj, "OrderService.cs"),
                "namespace Demo;\npublic class OrderService { public void Process(Order o) { } }\n");
            File.WriteAllText(Path.Combine(proj, "Program.cs"),
                "namespace Demo;\nclass Program { static void Main() { var s = new OrderService(); } }\n");

            var symbols = Agent.Infrastructure.Project.SymbolIndexer.Build(proj);
            Check("符号索引含 Order/OrderService", symbols.Any(s => s.Symbol == "Order") && symbols.Any(s => s.Symbol == "OrderService"));
            var edges = Agent.Infrastructure.Project.DependencyAnalyzer.Build(proj, symbols);
            Check("依赖边 OrderService→Order", edges.Any(e => e.File == "OrderService.cs" && e.DependsOnFile == "Order.cs"));
            Check("依赖边 Program→OrderService", edges.Any(e => e.File == "Program.cs" && e.DependsOnFile == "OrderService.cs"));
            Check("反向依赖 Order.cs ← OrderService.cs",
                Agent.Infrastructure.Project.DependencyAnalyzer.GetReverseDependencies("Order.cs", edges).Contains("OrderService.cs"));

            // analyze_deps 工具端到端（惰性建索引）
            var tool = new Agent.Infrastructure.Tools.DependencyAnalyzerTool(proj, proj,
                new Agent.Infrastructure.Project.SymbolIndexStore(
                    Path.Combine(TestTmp, "sym_epoch_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db")));
            var json = System.Text.Json.JsonDocument.Parse("{\"path\":\"Order.cs\"}").RootElement;
            var outText = tool.InvokeAsync(json, CancellationToken.None).GetAwaiter().GetResult();
            Check("analyze_deps 输出反向依赖", outText.Contains("反向依赖") && outText.Contains("OrderService.cs"));
            Check("analyze_deps 非工程模式报错",
                new Agent.Infrastructure.Tools.DependencyAnalyzerTool(proj, null, new Agent.Infrastructure.Project.SymbolIndexStore(Path.Combine(TestTmp, "x.db")))
                    .InvokeAsync(json, CancellationToken.None).GetAwaiter().GetResult().Contains("工程模式"));
        }
    }

    // ── 区段 2104：P1-1 symbol 索引增量失效（mtime） ──
    public static void Run2104()
    {
        Console.WriteLine("== P1-1 symbol 索引增量失效（mtime） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_incr");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "A.cs"), "class A { }\nclass A2 { }");
            File.WriteAllText(Path.Combine(proj, "B.cs"), "class B { }");
            var db = Path.Combine(TestTmp, "sym_incr_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6] + ".db");
            var store = new Agent.Infrastructure.Project.SymbolIndexStore(db);

            var full = store.BuildAndSave(proj);
            Check("P1-1 全量构建 3 符号", full == 3);
            // 修改 A.cs（追加新类型）→ 增量应只重扫 A.cs
            var aPath = Path.Combine(proj, "A.cs");
            File.WriteAllText(aPath, "class A { }\nclass A2 { }\nclass A3 { }");
            // 【10-14 P4-17 顺带修偶发】显式抬升 mtime：写文件后若与全量构建落在同一时间粒度内
            // （部分文件系统 mtime 分辨率低于写操作耗时），增量索引会判"未变更"→
            // Rescanned=0 且新符号不可见，实测两次运行偶发 1次（2026-10-14 835→837 批次捕获）。
            // 增量失效判定的正确性属被测行为，测试不应依赖文件系统时间粒度。
            File.SetLastWriteTimeUtc(aPath, DateTime.UtcNow.AddSeconds(2));
            var inc = store.BuildAndSaveIncremental(proj);
            var allAfter = store.GetAll(proj);
            Check("P1-1 增量仅重扫 1 文件", inc.Rescanned == 1);
            Check("P1-1 新符号可见（A3）", allAfter.Any(s => s.Symbol == "A3"));
            Check("P1-1 未变文件保留（B）", allAfter.Any(s => s.Symbol == "B"));
            // 删除 B.cs → 增量应清除 B 记录（无文件需重扫，仅 stale 清理）
            File.Delete(Path.Combine(proj, "B.cs"));
            var inc2 = store.BuildAndSaveIncremental(proj);
            Check("P1-1 删除文件记录清除", inc2.Rescanned == 0 && !store.GetAll(proj).Any(s => s.Symbol == "B"));
        }
    }

    // ── 区段 2256：10-08 B 同文件内自重复类型定义检测（P-D 扩展） ──
    public static void Run2256()
    {
        Console.WriteLine("== 10-08 B 同文件内自重复类型定义检测（P-D 扩展） ==");
        {
            // 真实样本：LetterCaseChecker.cs 900 字符内 2×class LetterCaseChecker → CS0101。
            var selfDup = Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
                "using System;\npublic class LetterCaseChecker\n{\n}\npublic class LetterCaseChecker\n{\n}\n");
            Check("B DetectSelfDuplicateTypes → 检出 1 项重复（定义 2 次）",
                selfDup.Count == 1 && selfDup[0].Contains("LetterCaseChecker") && selfDup[0].Contains("2 次"));
            // 正常单类 → 空
            Check("B 单类定义 → 无自重复",
                Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
                    "public class Order { }\npublic class OrderService { }").Count == 0);
            // 注释/字符串中的类名不计
            Check("B 注释/字符串中的类名 → 不计入自重复",
                Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
                    "// class Fake\nstring s = \"class InString\";\npublic class Real { }").Count == 0);
            // 三份重复 → 定义 3 次
            var selfDup3 = Agent.Infrastructure.Project.TypeConflictDetector.DetectSelfDuplicateTypes(
                "public class X { }\npublic class X { }\npublic class X { }");
            Check("B 三份重复 → 定义 3 次",
                selfDup3.Count == 1 && selfDup3[0].Contains("3 次"));
        }
    }

    // ── 区段 3591：10-09 读任务三件套（候选路径 / 核心清单 / 步数分级） ──
    public static void Run3591()
    {
        Console.WriteLine("== 10-09 读任务三件套（候选路径 / 核心清单 / 步数分级） ==");

        // 3-1：特征分类（方案2/3 的确定性判定词表回归）
        Check("分类：分析项目 → ReadQuery", Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("分析一下这个项目") == Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
        Check("分类：解释工程结构 → ReadQuery", Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("解释一下这个工程的结构") == Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
        Check("分类：读取文件 → ReadQuery", Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("读取 BigService.cs") == Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
        Check("分类：写函数 → 非 ReadQuery", Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("写一个C#判断奇数偶数函数") != Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);
        Check("分类：多文件项目 → 非 ReadQuery", Agent.Infrastructure.Routing.TaskFeatureClassifier.Classify("用 C# 写一个图书管理系统") != Agent.Infrastructure.Routing.TaskFeatureClassifier.Feature.ReadQuery);

        // 3-2：读任务步数上限配置（方案3：ReadQuery 放大到 40，写任务保持 MaxSteps=20）
        Check("配置：ReadQueryMaxSteps 默认 40（> MaxSteps 20）", new Agent.Core.Models.AgentOptions().ReadQueryMaxSteps == 40);
        Check("配置：MaxSteps 默认保持 20", new Agent.Core.Models.AgentOptions().MaxSteps == 20);

        // 1/2 数据源：临时工程（多目录同名 + 近似名 + 入口/依赖/大源码）
        var candRoot = Path.Combine(TestTmp, "cand_proj");
        Directory.CreateDirectory(Path.Combine(candRoot, "src", "Services"));
        Directory.CreateDirectory(Path.Combine(candRoot, "tests"));
        File.WriteAllText(Path.Combine(candRoot, "Program.cs"), "class Program {}");
        File.WriteAllText(Path.Combine(candRoot, "src", "Services", "BigService.cs"), "class BigService {}");
        File.WriteAllText(Path.Combine(candRoot, "src", "Services", "OrderService.cs"), "class OrderService {}");
        File.WriteAllText(Path.Combine(candRoot, "tests", "BigService.cs"), "class BigService {}");
        File.WriteAllText(Path.Combine(candRoot, "TaskEventHub.cs"), "class TaskEventHub {}");
        File.WriteAllText(Path.Combine(candRoot, "demo.csproj"), "<Project />");

        Check("候选：精确同名（src 与 tests 两处）", Agent.Infrastructure.Project.ProjectScanner.FindFileCandidates(candRoot, "BigService.cs").Count == 2);
        Check("候选：同名列表含真实相对路径", string.Join("|", Agent.Infrastructure.Project.ProjectScanner.FindFileCandidates(candRoot, "BigService.cs")).Contains("src\\Services\\BigService.cs"));
        Check("候选：模糊包含 TaskEvent.cs → TaskEventHub.cs", Agent.Infrastructure.Project.ProjectScanner.FindFileCandidates(candRoot, "TaskEvent.cs").Contains("TaskEventHub.cs"));
        Check("候选：无匹配 → 空列表", Agent.Infrastructure.Project.ProjectScanner.FindFileCandidates(candRoot, "Nope.cs").Count == 0);

        var core = Agent.Infrastructure.Project.ProjectScanner.CoreFileLines(candRoot);
        Check("核心清单：非空", !string.IsNullOrWhiteSpace(core));
        Check("核心清单：含入口 Program.cs", core.Contains("Program.cs"));
        Check("核心清单：含依赖 csproj", core.Contains("demo.csproj"));
        Check("核心清单：含大源码文件", core.Contains("BigService.cs"));

        // 1：ReadFileTool 工程模式附候选 / 沙箱模式原样（沙箱无工程索引，保持旧行为）
        var readArgs = System.Text.Json.JsonDocument.Parse("{\"path\":\"TaskEvent.cs\"}").RootElement;
        var readToolProj = new Agent.Infrastructure.Tools.ReadFileTool(candRoot, candRoot);
        var readOutProj = readToolProj.InvokeAsync(readArgs, CancellationToken.None).Result;
        Check("read_file 工程模式：不存在 + 有候选 → 附候选路径", readOutProj.Contains("工程中相近文件") && readOutProj.Contains("TaskEventHub.cs"));
        var readToolSand = new Agent.Infrastructure.Tools.ReadFileTool(candRoot);
        var readOutSand = readToolSand.InvokeAsync(readArgs, CancellationToken.None).Result;
        Check("read_file 沙箱模式：不存在 → 原样无候选", readOutSand.Contains("文件不存在") && !readOutSand.Contains("工程中相近文件"));
    }

    // ── 区段 3656：10-13 上下文预算 read_file 短路 ──
    public static void Run3656()
    {
        Console.WriteLine("== 10-13 上下文预算 read_file 短路 ==");
        var readArgsAbs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"path\":\"x:/index.html\"}");
        var readArgsRel = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"path\":\"index.html\"}");
        var readArgsNoPath = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"offset\":1}");
        Check("短路：绝对路径命中注入文件 → true", Agent.Infrastructure.AgentLoop.TryGetReadPath(readArgsAbs, out var rp1) && Agent.Infrastructure.AgentLoop.ReadPathMatches(rp1, new Agent.Core.Models.AgentRequest("s", "t", @"X:\run", null), @"X:\index.html"));
        Check("短路：相对路径(workDir基准) ≠ 注入绝对路径 → false", Agent.Infrastructure.AgentLoop.TryGetReadPath(readArgsRel, out var rp2) && !Agent.Infrastructure.AgentLoop.ReadPathMatches(rp2, new Agent.Core.Models.AgentRequest("s", "t", @"X:\run", null), @"X:\index.html"));
        Check("短路：无 path 参数 → false", !Agent.Infrastructure.AgentLoop.TryGetReadPath(readArgsNoPath, out _));
        Check("短路：同文件不同盘符大小写 → true", Agent.Infrastructure.AgentLoop.ReadPathMatches(@"X:\INDEX.HTML", new Agent.Core.Models.AgentRequest("s", "t", @"X:\run", null), @"x:\index.html"));
        Check("短路：不同文件 → false", !Agent.Infrastructure.AgentLoop.ReadPathMatches(@"X:\other.html", new Agent.Core.Models.AgentRequest("s", "t", @"X:\run", null), @"X:\index.html"));
    }

    // ── 区段 3782：10-11 write_unverified 归因：熔断消息带 run_command 真实失败原因 ──
    public static void Run3782()
    {
        Console.WriteLine("== 10-11 write_unverified 归因 ==");
        Check("熔断消息：空摘要 → 兼容旧消息", Agent.Infrastructure.AgentLoop.BuildWriteUnverifiedReason("") == "任务写入文件但验证未通过（run_command 未成功），熔断终止，防假成功。");
        Check("熔断消息：空白摘要 → 兼容旧消息", Agent.Infrastructure.AgentLoop.BuildWriteUnverifiedReason("   ") == "任务写入文件但验证未通过（run_command 未成功），熔断终止，防假成功。");
        Check("熔断消息：带摘要 → 含失败原因", Agent.Infrastructure.AgentLoop.BuildWriteUnverifiedReason("错误：命令执行超时（>30秒）。退出码：124").Contains("命令执行超时"));
        Check("熔断消息：带摘要 → 保留基础消息", Agent.Infrastructure.AgentLoop.BuildWriteUnverifiedReason("exit code 1").StartsWith("任务写入文件但验证未通过"));
    }

    // ── 区段 3788：CheckProjectName 相对 cwd 存在性 + 递归同名（10-10 修正） ──
    public static void Run3788()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "lawcheck_" + Guid.NewGuid().ToString("N")[..8]);
        var projDir = Path.Combine(tmpRoot, "src", "Agent.Core", "Extensions");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "NumberExtensions.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(tmpRoot, "src", "Agent.Core", "Agent.Core.csproj"), "<Project />");
        Check("幻觉校验：相对 cwd 存在 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckProjectName("dotnet build src/Agent.Core/Extensions/NumberExtensions.csproj", tmpRoot) == null);
        Check("幻觉校验：递归同名 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckProjectName("dotnet run --project Agent.Core.csproj", tmpRoot) == null);
        Check("幻觉校验：幻觉项目名 → 拦截", Agent.Infrastructure.Tools.RunCommandTool.CheckProjectName("dotnet run --project OrderSystem.csproj", tmpRoot) != null);
        Check("幻觉校验：无 csproj 引用 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckProjectName("python main.py", tmpRoot) == null);
        Directory.Delete(tmpRoot, recursive: true);
    }

    // ── 区段 3803：10-11 ③ 多 csproj 歧义：dotnet run 不带 --project 且目录多个项目 → 预检拦截 ──
    public static void Run3803()
    {
        Console.WriteLine("== 10-11 dotnet run 多项目歧义 ==");
        {
            var tmpRoot = Path.Combine(Path.GetTempPath(), "lawamb_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmpRoot);
            File.WriteAllText(Path.Combine(tmpRoot, "A.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(tmpRoot, "B.csproj"), "<Project />");
            Check("歧义：dotnet run 多项目 → 拦截", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run", tmpRoot) != null);
            Check("歧义：拦截消息含项目清单", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run", tmpRoot)!.Contains("A.csproj") && Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run", tmpRoot)!.Contains("B.csproj"));
            Check("歧义：--project 指定 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run --project A.csproj", tmpRoot) == null);
            Check("歧义：显式 .csproj → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run A.csproj", tmpRoot) == null);
            Check("歧义：非 dotnet 命令 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("python main.py", tmpRoot) == null);
            Directory.Delete(tmpRoot, recursive: true);

            var singleRoot = Path.Combine(Path.GetTempPath(), "lawamb1_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(singleRoot);
            File.WriteAllText(Path.Combine(singleRoot, "Only.csproj"), "<Project />");
            Check("歧义：单项目 → 放行", Agent.Infrastructure.Tools.RunCommandTool.CheckDotnetRunAmbiguity("dotnet run", singleRoot) == null);
            Directory.Delete(singleRoot, recursive: true);
        }
    }

    // ── 区段 3824：10-11 ④ 可疑成功归因验证：装饰器把事件信号 → is_suspicious + error_summary 归因 ──
    public static void Run3824()
    {
        Console.WriteLine("== 10-11 可疑成功归因（装饰器转换层） ==");
        {
            async System.Threading.Tasks.Task<Agent.Core.Models.TaskMetric> RunDecoratorAsync(string dir, params Agent.Core.Models.AgentEvent[] events)
            {
                var metrics = new Agent.Infrastructure.Metrics.SqliteMetricsCollector(System.IO.Path.Combine(dir, "sus.db"));
                var deco = new Agent.Infrastructure.Metrics.AgentLoopMetricsDecorator(
                    new StubLoop(events), metrics,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Agent.Infrastructure.Metrics.AgentLoopMetricsDecorator>.Instance);
                var req = new Agent.Core.Models.AgentRequest(Guid.NewGuid().ToString("N"), "测试", dir);
                await foreach (var _ in deco.RunAsync(req, System.Threading.CancellationToken.None)) { }
                return (await metrics.GetRecentAsync(50, System.Threading.CancellationToken.None)).First();
            }

            var dir1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lawsus1_" + Guid.NewGuid().ToString("N")[..8]);
            var dir2 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lawsus2_" + Guid.NewGuid().ToString("N")[..8]);
            var dir3 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lawsus3_" + Guid.NewGuid().ToString("N")[..8]);
            var dir4 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lawsus4_" + Guid.NewGuid().ToString("N")[..8]);

            var m1 = RunDecoratorAsync(dir1,
                new Agent.Core.Models.AgentEvent.ToolResultEvent("run_command", "退出码：0\n--- stdout ---\nHello, World!\n[检测] 输出疑似模板内容。", true),
                new Agent.Core.Models.AgentEvent.FinalAnswer("完成")).Result;
            Check("归因：模板检测信号 → is_suspicious=1", m1.IsSuspicious);
            Check("归因：模板检测信号 → error_summary=输出疑似模板", m1.ErrorSummary == "输出疑似模板");

            var m2 = RunDecoratorAsync(dir2,
                new Agent.Core.Models.AgentEvent.ToolResultEvent("run_command", "[边缘成功] 输出含失败信号（xx）但存在成功证据（演示分支/成功报告），按成功处理。", true),
                new Agent.Core.Models.AgentEvent.FinalAnswer("完成")).Result;
            Check("归因：边缘成功信号 → is_suspicious=1", m2.IsSuspicious);
            Check("归因：边缘成功信号 → error_summary=边缘成功", m2.ErrorSummary == "边缘成功（失败信号+成功证据）");

            var m3 = RunDecoratorAsync(dir3,
                new Agent.Core.Models.AgentEvent.Error("任务未真正实现：输出只是 Hello World 模板，未实现用户需求。")).Result;
            Check("归因：未真正实现 → is_suspicious=1", m3.IsSuspicious);
            Check("归因：未真正实现 → error_summary 含真实原因", m3.ErrorSummary.Contains("未真正实现"));

            var m4 = RunDecoratorAsync(dir4,
                new Agent.Core.Models.AgentEvent.Routing("small", "Rule", "force_code_simple"),
                new Agent.Core.Models.AgentEvent.ToolResultEvent("write_file", "已写入 test.py，共 10 字符。", true),
                new Agent.Core.Models.AgentEvent.FinalAnswer("完成")).Result;
            Check("归因：干净流 → is_suspicious=0", !m4.IsSuspicious);
            Check("归因：干净流 → error_summary 空", string.IsNullOrEmpty(m4.ErrorSummary));

            foreach (var d in new[] { dir1, dir2, dir3, dir4 })
            {
                try { System.IO.Directory.Delete(d, recursive: true); } catch { }
            }
        }
    }
}

/// <summary>事件桩（3824 区段装饰器测试用；原 Program.cs 3937 行迁移）。</summary>
sealed class StubLoop : Agent.Core.Abstractions.IAgentLoop
{
    private readonly Agent.Core.Models.AgentEvent[] _events;
    public StubLoop(params Agent.Core.Models.AgentEvent[] events) { _events = events; }
    public async System.Collections.Generic.IAsyncEnumerable<Agent.Core.Models.AgentEvent> RunAsync(
        Agent.Core.Models.AgentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct)
    {
        foreach (var e in _events) yield return e;
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
