// ============================================================
// Suites_DiffSpectre.cs
// Diff 引擎（FileDiffService / PlainDiffFormatter）与终端渲染器（RendererBase / Spectre）
// 区段：2461 / 2530
// 依赖：using static AgentTests.TestHarness（Check）
// ============================================================
using System.Text.Json;
using static AgentTests.TestHarness;

namespace AgentTests;

public static class Suites_DiffSpectre
{
    // ── 区段 2461：Diff P0 FileDiffService（Diff/） ──
    public static void Run2461()
    {
        Console.WriteLine("== Diff P0 FileDiffService（Diff/） ==");
        {
            // ── 1. 增删改混合 ──
            var before = new[] { "a", "b", "c", "d", "e" };
            var after = new[] { "a", "x", "c", "d", "f" };
            var r = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(before, after);
            Check("混合 diff 非 TooLarge", !r.TooLarge);
            Check("混合 diff 有 1 个 hunk（上下文相连）", r.Hunks.Count == 1);
            var lines = r.Hunks[0].Lines;
            Check("混合 diff 含增删改行", lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add && l.Text == "x")
                && lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Remove && l.Text == "b")
                && lines.Any(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add && l.Text == "f"));
            Check("Same 行带新旧行号", lines.First(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Same && l.Text == "c").OldNo == 3
                && lines.First(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Same && l.Text == "c").NewNo == 3);

            // ── 2. 上下文裁剪：改 1 行 → 只输出变更附近 ±3 行 ──
            var big = Enumerable.Range(1, 5000).Select(i => $"line{i}").ToArray();
            var bigAfter = big.ToArray();
            bigAfter[2499] = "CHANGED"; // 改第 2500 行
            var r2 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(big, bigAfter);
            Check("5000 行改 1 行 → 非降级且可行", !r2.TooLarge && r2.Hunks.Count >= 1);
            var total = r2.Hunks.Sum(h => h.Lines.Count);
            Check($"5000 行改 1 行 → 输出远小于全文（{total} 行 ≤ 20）", total <= 20);
            Check("裁剪含变更行与 ±3 上下文", r2.Hunks[0].Lines.Any(l => l.Text == "CHANGED")
                && r2.Hunks[0].Lines.Any(l => l.Text == "line2497") && r2.Hunks[0].Lines.Any(l => l.Text == "line2502"));
            Check("hunk 头计数正确", r2.Hunks[0].OldCount >= 6 && r2.Hunks[0].NewCount >= 6);

            // ── 3. 边界：相同 / 新建 / 全删 ──
            var same = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(new[] { "a", "b" }, new[] { "a", "b" });
            Check("相同文件 → 无 hunk", same.Hunks.Count == 0);
            var created = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(Array.Empty<string>(), new[] { "n1", "n2", "n3" });
            Check("新建（空→3 行）→ 全 Add 单 hunk", created.Hunks.Count == 1
                && created.Hunks[0].Lines.All(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Add));
            var deleted = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(new[] { "a", "b" }, Array.Empty<string>());
            Check("全删 → 全 Remove 单 hunk", deleted.Hunks.Count == 1
                && deleted.Hunks[0].Lines.All(l => l.Type == Agent.Infrastructure.Diff.DiffLineType.Remove));

            // ── 4. 大文件降级（只针对"全不同/大变化段"，改 1 行的大文件可行） ──
            var hugeA = Enumerable.Range(1, 3000).Select(i => $"a{i}").ToArray();
            var hugeB = Enumerable.Range(1, 3000).Select(i => $"b{i}").ToArray(); // 全部不同 → 变化段 3000×3000
            var r4 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(hugeA, hugeB);
            Check("3000 行全不同 → 明确降级 TooLarge + 原因", r4.TooLarge && !string.IsNullOrEmpty(r4.TooLargeReason));
            var r4b = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(hugeA, hugeA.Select(x => x == "a1" ? "CHANGED" : x).ToArray());
            Check("3000 行改 1 行 → 非降级且只输出 ±3 行", !r4b.TooLarge && r4b.Hunks.Sum(h => h.Lines.Count) <= 20);

            // ── 5. 相邻改动合并（gap ≤ 2×3=6 合并；>6 分块） ──
            var m = Enumerable.Range(1, 30).Select(i => $"line{i}").ToArray();
            var m2 = m.ToArray();
            m2[4] = "C1"; m2[8] = "C2"; // 改动 gap 3 行 → 合并为 1 hunk
            var r5 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(m, m2);
            Check("gap≤6 两处改动合并为 1 hunk", r5.Hunks.Count == 1 && r5.Hunks[0].Lines.Any(l => l.Text == "C1") && r5.Hunks[0].Lines.Any(l => l.Text == "C2"));
            var m3 = m.ToArray();
            m3[4] = "C1"; m3[20] = "C2"; // gap 15 行 → 2 hunks
            var r6 = Agent.Infrastructure.Diff.FileDiffService.ComputeLines(m, m3);
            Check("gap>6 两处改动分为 2 hunks", r6.Hunks.Count == 2);

            // ── 5b. 【10-14 P4-17】ComputeWithText：一次读盘出行序列 + 全文，且与 Compute 逐行等价 ──
            {
                var dt = Path.Combine(TestTmp, "difftext_" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(dt);
                // 刻意包含：CRLF 混合行尾、**中间空行**、末尾换行——正是 ReadAllLines 与
                // 朴素 Split('\n') 会产生差异的三个点，用来锁死切行语义等价。
                File.WriteAllText(Path.Combine(dt, "a.txt"), "l1\r\nl2\n\nl4\nl5");
                File.WriteAllText(Path.Combine(dt, "b.txt"), "l1\nl2\n\nCHANGED\nl5");
                var oldWay = Agent.Infrastructure.Diff.FileDiffService.Compute(
                    Path.Combine(dt, "a.txt"), Path.Combine(dt, "b.txt"));
                var newWay = Agent.Infrastructure.Diff.FileDiffService.ComputeWithText(
                    Path.Combine(dt, "a.txt"), Path.Combine(dt, "b.txt"));
                Check("ComputeWithText：diff 结果与 Compute 完全一致（切行语义等价：CRLF/中间空行/末尾换行）",
                    oldWay.TooLarge == newWay.Result.TooLarge
                    && oldWay.Hunks.Count == newWay.Result.Hunks.Count
                    && oldWay.Hunks.SelectMany(h => h.Lines)
                        .SequenceEqual(newWay.Result.Hunks.SelectMany(h => h.Lines)));
                Check("ComputeWithText：同时返回两侧全文（与 ReadAllText 一致）",
                    newWay.Original == File.ReadAllText(Path.Combine(dt, "a.txt"))
                    && newWay.Modified == File.ReadAllText(Path.Combine(dt, "b.txt")));
                var bigA = string.Join("\n", Enumerable.Range(0, 3000).Select(i => "a" + i));
                File.WriteAllText(Path.Combine(dt, "big1.txt"), bigA);
                File.WriteAllText(Path.Combine(dt, "big2.txt"), bigA.Replace("a1\n", "CHANGED\n"));
                var bigWay = Agent.Infrastructure.Diff.FileDiffService.ComputeWithText(
                    Path.Combine(dt, "big1.txt"), Path.Combine(dt, "big2.txt"));
                Check("ComputeWithText：TooLarge → 全文返 null（不把超大文本塞进 JSON，与原宿主语义一致）",
                    !bigWay.Result.TooLarge || (bigWay.Original is null && bigWay.Modified is null));
                Directory.Delete(dt, true);
            }

            // ── 6. 无头文本渲染（PlainDiffFormatter，diff 命令 PlainText 通道） ──
            var fmt = Agent.Infrastructure.Diff.PlainDiffFormatter.Format(r);
            Check("PlainDiffFormatter 含 hunk 头与 +/- 行", fmt.Contains("@@ ") && fmt.Contains("\n- ") && fmt.Contains("\n+ "));
            Check("PlainDiffFormatter 无 TooLarge 文本", !fmt.Contains("文件过大"));
        }
    }

    // ── 区段 2530：Spectre P0 渲染器（Rendering/） ──
    public static void Run2530()
    {
        Console.WriteLine("== Spectre P0 渲染器（Rendering/） ==");
        {
            // ── 1. 工厂选择 ──
            var plain = Agent.Infrastructure.Rendering.RendererFactory.Create(false);
            var spectre = Agent.Infrastructure.Rendering.RendererFactory.Create(true);
            Check("无头 → PlainTextRenderer", plain is Agent.Infrastructure.Rendering.PlainTextRenderer);
            Check("交互 → SpectreRenderer（构造不抛）", spectre is Agent.Infrastructure.Rendering.SpectreRenderer);

            // ── 1b. 终端增强（10-07）：TaskPrefix / Verbose 默认值与类型（交互态专用，PlainText 不受影响）──
            var sp = (Agent.Infrastructure.Rendering.SpectreRenderer)spectre;
            Check("Spectre TaskPrefix 默认空", sp.TaskPrefix == "");
            Check("Spectre Verbose 默认 false", sp.Verbose == false);

            // ── 2. FormatEvent 七事件映射（与旧 RenderEvent 文本格式逐字节一致）──
            var rt1 = new Agent.Core.Models.AgentEvent.Routing("large", "L0.5", "写任务", "写任务");
            Check("Routing 带意图", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt1) == "[路由] L0.5 [写任务] → large");
            var rt2 = new Agent.Core.Models.AgentEvent.Routing("large", "L0.5", "写任务", null);
            Check("Routing 无意图", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt2) == "[路由] L0.5 → large");
            var pg = new Agent.Core.Models.AgentEvent.PlanGenerated("构建订单系统", "csharp", new List<string> { "Order.cs", "Program.cs" });
            Check("PlanGenerated 多行文件清单",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(pg) == "[规划] 构建订单系统（csharp，2 个文件）\n  - Order.cs\n  - Program.cs");
            var fw = new Agent.Core.Models.AgentEvent.FileWritten("x.cs", 123);
            Check("FileWritten", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fw) == "[写入] x.cs（123 字符）");
            var tcArgs = JsonDocument.Parse("{\"command\":\"dotnet run\"}").RootElement;
            var tc = new Agent.Core.Models.AgentEvent.ToolCall("_-run_command", tcArgs);
            Check("ToolCall 保留原名+参数格式化",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tc) == "[工具调用] _-run_command  command=\"dotnet run\"");
            var tr = new Agent.Core.Models.AgentEvent.ToolResultEvent("read_file", "文件内容", true);
            Check("ToolResult 成功",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr) == "[工具结果] read_file: 文件内容");
            var longOut = new string('x', 400);
            var tr2 = new Agent.Core.Models.AgentEvent.ToolResultEvent("read_file", longOut, false);
            Check("ToolResult 截断 300", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr2).EndsWith("..."));
            var fa = new Agent.Core.Models.AgentEvent.FinalAnswer("任务完成");
            Check("FinalAnswer", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fa) == "[完成] 任务完成");
            var err = new Agent.Core.Models.AgentEvent.Error("出错");
            Check("Error", Agent.Infrastructure.Rendering.RendererBase.FormatEvent(err) == "[错误] 出错");

            // ── 3. Truncate / FormatToolArgs 边界 ──
            Check("Truncate 短串不变",
                Agent.Infrastructure.Rendering.RendererBase.Truncate("abc", 5) == "abc");
            Check("Truncate 长串截断",
                Agent.Infrastructure.Rendering.RendererBase.Truncate("abcdef", 3) == "abc...");
            var wfArgs = JsonDocument.Parse("{\"path\":\"p.py\",\"content\":\"0123456789\"}").RootElement;
            Check("FormatToolArgs write_file",
                Agent.Infrastructure.Rendering.RendererBase.FormatToolArgs("write_file", wfArgs) == "path=\"p.py\", content=10 字符");
            var ldArgs = JsonDocument.Parse("{}").RootElement;
            Check("FormatToolArgs list_dir 默认 path",
                Agent.Infrastructure.Rendering.RendererBase.FormatToolArgs("list_dir", ldArgs) == "path=\".\"");

            // ── 4. 兼容抽样：旧 CLI 格式精确相等 ──
            Check("兼容抽样 FileWritten",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(fw) == "[写入] x.cs（123 字符）");
            Check("兼容抽样 Routing",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(rt2) == "[路由] L0.5 → large");
            Check("PlainText RenderEventAsync 不抛",
                plain.RenderEventAsync(fa).IsCompletedSuccessfully);

            // ── 5. 双路径漂移回归：orch 命令与任务路径同源（ToolResult 统一截断 300）──
            Check("ToolResult 截断统一 300（orch 路径原 500 已收敛）",
                Agent.Infrastructure.Rendering.RendererBase.FormatEvent(tr2).Length <= 300 + " [工具结果] read_file: ".Length + "...".Length);
        }
    }
}
