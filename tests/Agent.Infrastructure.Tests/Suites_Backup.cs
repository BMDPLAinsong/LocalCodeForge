// ============================================================
// Suites_Backup.cs
// 回滚体系（BackupService：任务级回滚 / 工程失败自动回滚 / taskId 隔离 / dry-run）
// 区段：1591 / 2076 / 2304 / 2330 / 2353 / 2432
// 依赖：using static AgentTests.TestHarness（Check / TestTmp）
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_Backup
{
    // ── 区段 1591：任务级回滚（多文件变更单元） ──
    public static void Run1591()
    {
        Console.WriteLine("== 任务级回滚（多文件变更单元） ==");
        {
            var proj = Path.Combine(TestTmp, "rb_" + Guid.NewGuid().ToString("N")[..6]);
            var work = Path.Combine(TestTmp, "rbwork_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(proj);
            Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(proj, "a.cs"), "class A { }");
            File.WriteAllText(Path.Combine(proj, "b.cs"), "class B { }");

            // 模拟任务内两次修改（自动备份）
            var tool = new Agent.Infrastructure.Tools.EditFileTool(work, proj, projectWriteEnabled: true);
            tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"a.cs\",\"old_string\":\"class A\",\"new_string\":\"class A2\"}").RootElement,
                CancellationToken.None).GetAwaiter().GetResult();
            tool.InvokeAsync(System.Text.Json.JsonDocument.Parse(
                "{\"path\":\"b.cs\",\"old_string\":\"class B\",\"new_string\":\"class B2\"}").RootElement,
                CancellationToken.None).GetAwaiter().GetResult();
            Check("修改后文件已变更", File.ReadAllText(Path.Combine(proj, "a.cs")).Contains("class A2")
                  && File.ReadAllText(Path.Combine(proj, "b.cs")).Contains("class B2"));

            // 任务级回滚（rollback all 语义）——P-A：取最早备份=任务前状态
            var restored = Agent.Infrastructure.Project.BackupService.RollbackTask(work, proj, "default");
            Check("任务级回滚恢复 2 个文件", restored == 2);
            Check("a.cs 已恢复", File.ReadAllText(Path.Combine(proj, "a.cs")).Contains("class A { }"));
            Check("b.cs 已恢复", File.ReadAllText(Path.Combine(proj, "b.cs")).Contains("class B { }"));

            Directory.Delete(proj, true);
            Directory.Delete(work, true);
        }
    }

    // ── 区段 2076：P0-1 工程失败自动回滚（RollbackTaskFull） ──
    public static void Run2076()
    {
        Console.WriteLine("== P0-1 工程失败自动回滚（RollbackTaskFull） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_rollback");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "run_task123");   // run 级工作目录（task_ 前缀）
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(proj);

            var orderCs = Path.Combine(proj, "Order.cs");
            File.WriteAllText(orderCs, "class Order { public int Id { get; set; } }");
            var oldContent = File.ReadAllText(orderCs);

            // 模拟：改已存在文件前备份 + 写入（P-B：任务隔离 taskA）
            Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskA");
            File.WriteAllText(orderCs, "class Order { public int Id { get; set; } public double Amount { get; set; } }");
            // 模拟：新建工程文件（标记 .new）
            var taxCs = Path.Combine(proj, "TaxService.cs");
            File.WriteAllText(taxCs, "class TaxService { }");
            Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, taxCs, proj, "taskA");

            var (restored, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskA");
            Check("P0-1 恢复已存在文件备份", restored == 1 && File.ReadAllText(orderCs) == oldContent);
            Check("P0-1 清理新建文件", removed == 1 && !File.Exists(taxCs));
            // .new 目录不作为 stamp 目录（ListBackups/RollbackTask 遍历排除）
            Check("P0-1 新文件不残留在备份列表", !Agent.Infrastructure.Project.BackupService.ListBackups(ws).Any(b => b.Rel.StartsWith(".new", StringComparison.OrdinalIgnoreCase)));
        }
    }

    // ── 区段 2304：H1 跨任务误删防护（ClearNewMarkers，P-B taskId 隔离） ──
    public static void Run2304()
    {
        Console.WriteLine("== H1 跨任务误删防护（ClearNewMarkers，P-B taskId 隔离） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_h1");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "run_taskA");
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(proj);

            // 任务 A 成功：新建 TaxService.cs + 标记 → 成功收尾 ClearNewMarkers（模拟 CLI 成功分支，P-B 只清 taskA）
            var taxCs = Path.Combine(proj, "TaxService.cs");
            File.WriteAllText(taxCs, "class TaxService { }");
            Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, taxCs, proj, "taskA");
            Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");

            // 任务 B（同会话）失败：新建 Broken.cs + 标记 → 回滚（P-B 只回滚 taskB）
            var brokenCs = Path.Combine(proj, "Broken.cs");
            File.WriteAllText(brokenCs, "class Broken { }");
            Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, brokenCs, proj, "taskB");
            var (restored, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
            Check("H1 任务 A 成功建的文件保留（不被 B 失败回滚误删）", File.Exists(taxCs));
            Check("H1 任务 B 新建文件被清理", removed == 1 && !File.Exists(brokenCs));
            Check("H1 回滚后 A 的备份目录不残留", !Directory.Exists(Path.Combine(ws, ".backup", "taskA")));
        }
    }

    // ── 区段 2330：P-A 回滚取最早备份（防恢复到任务中间状态） ──
    public static void Run2330()
    {
        Console.WriteLine("== P-A 回滚取最早备份（防恢复到任务中间状态） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tests_pa");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "run_taskA");
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(proj);
            var orderCs = Path.Combine(proj, "OrderService.cs");
            File.WriteAllText(orderCs, "// 任务前：干净内容 v0");

            // 任务内两次写入：初写（备份干净 v0）→ 写入坏内容 v1 → 修复重写（备份坏 v1）→ 写入 v2
            Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskX");
            File.WriteAllText(orderCs, "// 第一次写入（坏中间状态）v1");
            Agent.Infrastructure.Project.BackupService.Backup(ws, orderCs, proj, "taskX");
            File.WriteAllText(orderCs, "// 第二次写入（最终坏状态）v2");

            var restored = Agent.Infrastructure.Project.BackupService.RollbackTask(ws, proj, "taskX");
            Check("P-A 回滚取最早备份（恢复任务前干净状态，非中间状态）",
                restored == 1 && File.ReadAllText(orderCs).Contains("v0") && !File.ReadAllText(orderCs).Contains("v1"));
        }
    }

    // ── 区段 2353：跨任务端到端（P-B 方案 A 三场景） ──
    public static void Run2353()
    {
        Console.WriteLine("== 跨任务端到端（taskId 隔离三场景） ==");
        {
            // 场景 1：A 成功新建 → B 失败 → A 的新文件还在
            {
                var baseTmp = Path.Combine(TestTmp, "ts1");
                if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
                var ws = Path.Combine(baseTmp, "run");
                var proj = Path.Combine(baseTmp, "proj");
                Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);

                // A 成功：新建 AFile.cs + 成功收尾清 taskA
                var aFile = Path.Combine(proj, "AFile.cs");
                File.WriteAllText(aFile, "class AFile { }");
                Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, aFile, proj, "taskA");
                Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");

                // B 失败：新建 BFile.cs → 回滚 taskB
                var bFile = Path.Combine(proj, "BFile.cs");
                File.WriteAllText(bFile, "class BFile { }");
                Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, bFile, proj, "taskB");
                var (_, removed) = Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
                Check("场景1 A 新文件保留", File.Exists(aFile));
                Check("场景1 B 新文件清理", removed == 1 && !File.Exists(bFile));
            }

            // 场景 2：A 成功 → A 再改同文件 → B 失败 → A 的第二次改动还在
            {
                var baseTmp = Path.Combine(TestTmp, "ts2");
                if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
                var ws = Path.Combine(baseTmp, "run");
                var proj = Path.Combine(baseTmp, "proj");
                Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);
                var file = Path.Combine(proj, "OrderService.cs");
                File.WriteAllText(file, "// 初始状态 v0");

                // A 成功：第一次改动 v1 → 成功收尾清 taskA（备份随任务成功清除）
                Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskA");
                File.WriteAllText(file, "// A 第一次改动 v1");
                Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskA");
                // A 再改同文件（第二次改动 v2，同任务 taskA 新备份）
                Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskA");
                File.WriteAllText(file, "// A 第二次改动 v2");

                // B 失败：改同文件（备份=含 v2 的任务前）→ 回滚 taskB（只恢复 B 的备份=v2）
                Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskB");
                File.WriteAllText(file, "// B 的改动（坏）v3");
                Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskB");
                Check("场景2 A 第二次改动保留（B 回滚不碰 A 的备份）",
                    File.ReadAllText(file).Contains("v2") && !File.ReadAllText(file).Contains("v3"));
            }

            // 场景 3：A 失败清空 → B 成功新建同名 → C 失败 → B 的文件还在
            {
                var baseTmp = Path.Combine(TestTmp, "ts3");
                if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
                var ws = Path.Combine(baseTmp, "run");
                var proj = Path.Combine(baseTmp, "proj");
                Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);

                // A 失败：新建 X.cs + 回滚 taskA（清理 A 的 X.cs 与标记）
                var xFile = Path.Combine(proj, "X.cs");
                File.WriteAllText(xFile, "class X { }");
                Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, xFile, proj, "taskA");
                Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskA");
                Check("场景3 A 失败回滚删除其 X.cs", !File.Exists(xFile));

                // B 成功：新建同名 X.cs + 成功收尾清 taskB
                File.WriteAllText(xFile, "class X2 { }");
                Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, xFile, proj, "taskB");
                Agent.Infrastructure.Project.BackupService.ClearNewMarkers(ws, "taskB");

                // C 失败：回滚 taskC（不碰 B 的 X.cs）
                Agent.Infrastructure.Project.BackupService.RollbackTaskFull(ws, proj, "taskC");
                Check("场景3 B 新建同名文件保留（C 回滚不误删）",
                    File.Exists(xFile) && File.ReadAllText(xFile).Contains("class X2"));
            }
        }
    }

    // ── 区段 2432：rollback --dry-run 预览（不执行） ──
    public static void Run2432()
    {
        Console.WriteLine("== rollback --dry-run 预览（不执行） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "tsdry");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);
            var ws = Path.Combine(baseTmp, "run");
            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(ws); Directory.CreateDirectory(proj);
            var file = Path.Combine(proj, "Order.cs");
            File.WriteAllText(file, "v0");
            Agent.Infrastructure.Project.BackupService.Backup(ws, file, proj, "taskD");
            File.WriteAllText(file, "v1");
            var newFile = Path.Combine(proj, "New.cs");
            File.WriteAllText(newFile, "class New { }");
            Agent.Infrastructure.Project.BackupService.MarkNewFile(ws, newFile, proj, "taskD");

            var (restoreTargets, removeTargets) = Agent.Infrastructure.Project.BackupService.DryRunRollbackTask(ws, proj, "taskD");
            Check("dry-run 列出将恢复的文件", restoreTargets.Count == 1 && restoreTargets[0].Replace('\\', '/').EndsWith("Order.cs"));
            Check("dry-run 列出将删除的新文件", removeTargets.Count == 1 && removeTargets[0].Replace('\\', '/').EndsWith("New.cs"));
            Check("dry-run 不执行任何改动", File.ReadAllText(file).Contains("v1") && File.Exists(newFile));
        }
    }

    // ── 区段 2450：GetRunRoot 命名启发式哨兵（10-14 P3-5）──
    // 契约（注释约定，代码里只有一行 StartsWith("task_")）：任务目录 task_<id> 的 .backup 落在其父
    // （run 根）；非 task_ 前缀目录（run 目录自身）则落在自身。原为注释级约定，改名即静默错位——
    // 本段以可观测行为（备份落点）锁定。
    public static void Run2450()
    {
        Console.WriteLine("== GetRunRoot 命名启发式哨兵（task_ 前缀 → 父目录为 run 根） ==");
        {
            var baseTmp = Path.Combine(TestTmp, "runroot");
            if (Directory.Exists(baseTmp)) Directory.Delete(baseTmp, true);

            var proj = Path.Combine(baseTmp, "proj");
            Directory.CreateDirectory(proj);
            var f = Path.Combine(proj, "A.cs");

            // 形态 1：workspaceRoot 为任务目录（task_ 前缀）→ .backup 落父目录
            var runRoot = Path.Combine(baseTmp, "run_20261014-120000");
            var taskDir = Path.Combine(runRoot, "task_abc12345");
            Directory.CreateDirectory(taskDir);
            File.WriteAllText(f, "v0");
            Agent.Infrastructure.Project.BackupService.Backup(taskDir, f, proj, "taskS1");
            bool InRoot(string root) => Agent.Infrastructure.Project.BackupService.ListBackups(root)
                .Any(b => b.Rel.Replace('\\', '/').EndsWith("A.cs"));
            // 断言落点用磁盘事实（.backup 建在哪一层）：ListBackups(taskDir) 按契约同样上浮到
            // run 根（GetRunRoot 的设计），故不能拿它的空/非空当"未上浮"的证据。
            Check("GetRunRoot：task_ 前缀目录 → .backup 落父目录（run 根），任务目录内无 .backup",
                InRoot(runRoot)
                && Directory.Exists(Path.Combine(runRoot, ".backup", "taskS1"))
                && !Directory.Exists(Path.Combine(taskDir, ".backup")));

            // 形态 2：workspaceRoot 非 task_ 前缀（run 目录自身）→ .backup 落自身
            var runRoot2 = Path.Combine(baseTmp, "run_plain");
            Directory.CreateDirectory(runRoot2);
            File.WriteAllText(f, "v0b");
            Agent.Infrastructure.Project.BackupService.Backup(runRoot2, f, proj, "taskS2");
            Check("GetRunRoot：非 task_ 前缀目录 → 备份落自身，不上浮",
                InRoot(runRoot2)
                && !Agent.Infrastructure.Project.BackupService.ListBackups(baseTmp)
                    .Any(b => b.TaskId == "taskS2"));

            Directory.Delete(baseTmp, true);
        }
    }
}
