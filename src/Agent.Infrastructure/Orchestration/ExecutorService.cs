// ============================================================
// ExecutorService.cs
// 用 Coder-7B 为单个文件生成或修复代码。
// ============================================================

using System.Text.RegularExpressions;
using Agent.Core.Models;
using Agent.Infrastructure.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace Agent.Infrastructure.Orchestration;

/// <summary>
/// 执行器实现。
/// 为单个文件生成代码，或在编译失败时修复代码。
/// </summary>
public sealed class ExecutorService : IExecutorService
{
    private readonly Kernel _kernel;
    private readonly ILogger<ExecutorService> _logger;

    public ExecutorService(Kernel kernel, ILogger<ExecutorService> logger)
    {
        _kernel = kernel;
        _logger = logger;
    }

    // ============================================================
    // 生成文件
    // ============================================================

    /// <summary>
    /// 生成文件内容。
    /// </summary>
    public async Task<string> GenerateFileAsync(
        FileSpec spec,
        Plan plan,
        IReadOnlyDictionary<string, string> generatedFiles,
        CancellationToken ct)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>(RoutingDecision.Small);

        var history = new ChatHistory();

        // ═══════════ System Prompt ═══════════
        history.AddSystemMessage("""
你是代码生成器。请为指定的文件生成完整内容。

## 【最高优先级】任务完整性
- 必须真正实现用户需求，不能只输出"能跑通"的模板
- 禁止只输出 Hello World——用户要什么就实现什么
- 禁止输出占位符（如 // TODO、pass、空方法体）
- 交付前自检："用户要的功能真的实现了吗？"

## 输出要求
- 只输出文件内容本身
- 不要用 markdown 代码块包裹
- 不要输出任何解释、开场白、总结
- 不要输出"好的"、"以下是"这类文字
- 代码要完整可运行

## 语言规则
- Python：不用第三方库；用 import 组织
- C#：不用第三方包；using 只引用 System.* 命名空间
- JavaScript：用 Node.js 内置模块

## 【硬性禁止】第三方库
C# 代码绝对不能使用以下命名空间（会编译失败）：
- Newtonsoft.Json / Json.NET
- Microsoft.EntityFrameworkCore / EF Core
- AutoMapper
- Serilog / NLog
- Dapper

只能用 .NET BCL 里的类型：
- System.Text.Json（JSON 序列化）
- System.Collections.Generic（List/Dictionary）
- System.IO（文件读写）
- System.Net.Http（HTTP 请求）

Python 代码同理：
- 不用 numpy、pandas、flask、django、matplotlib（非标准库）
- 只用标准库：json、os、sys、re、math、sqlite3、urllib、http.client、csv、argparse
- 若任务附带了[环境约束]（外部数据任务），可按约束使用 requests 访问外部 HTTP 服务（必须带 timeout）

## 【C# 类型规则】
- 字面量类型必须匹配变量声明：
  - 错误：int x = 1.5;    正确：double x = 1.5;
  - 错误：float y = 1.5;  正确：float y = 1.5f;
- 数值类型转换必须显式：
  - 错误：int a = 3.14;
  - 正确：int a = (int)3.14;
- 常见错误：CS0266 无法将类型"double"隐式转换为"int" → 加 (int) 强制转换

## 【C# csproj 模板】
如果生成 .csproj 文件，严格使用这个模板：

<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>

- TargetFramework 必须是 net10.0（不要 net5.0 / net6.0 / net8.0）
- 不要写 <Compile Include="xxx.cs" />（.NET SDK 自动包含）

## 【重要】C# 文件组织规则
- **每个类只在一个文件里定义**——不要重复定义
- **入口文件（Program.cs）只放 Program 类 + Main 方法**
- 业务类（如 Calculator、UserManager）单独在 xxx.cs 里定义
- **不要写 `using <项目名>;`**——项目名不是命名空间

如果 Plan 只规划了 1 个 .cs 文件：
- 把所有类写在这 1 个文件里
- 类顺序：自定义异常 → 业务类 → Program

如果 Plan 规划了多个 .cs 文件：
- 每个文件只定义 Plan 里指定职责的类
- 入口 Program.cs 通过 `new Calculator()` 直接使用其他文件里的类（不需要 using）

**单文件示例**：
```csharp
using System;

class Calculator { ... }
class DivisionError : Exception { ... }
class Program { static void Main() { ... } }

// 1. 自定义异常类
class DivisionError : Exception
{
    public DivisionError(string message) : base(message) { }
}

// 2. 业务类
class Calculator
{
    public int Add(int a, int b) => a + b;
    public int Subtract(int a, int b) => a - b;
    public int Multiply(int a, int b) => a * b;
    public double Divide(int a, int b)
    {
        if (b == 0) throw new DivisionError("除数不能为零");
        return (double)a / b;
    }
}

// 3. 入口类
class Program
{
    static void Main()
    {
        var calc = new Calculator();
        Console.WriteLine("10 + 5 = " + calc.Add(10, 5));
        Console.WriteLine("10 - 5 = " + calc.Subtract(10, 5));
        Console.WriteLine("10 * 5 = " + calc.Multiply(10, 5));
        Console.WriteLine("10 / 5 = " + calc.Divide(10, 5));

        try
        {
            calc.Divide(10, 0);
        }
        catch (DivisionError ex)
        {
            Console.WriteLine("错误：" + ex.Message);
        }
    }
}

禁止：
- 创建多个 .cs 文件（如 Calculator.cs + Program.cs + DivisionError.cs）
- 在入口文件里写 using <项目名>;
- 用 using CalculatorProject; 这种写法引用自己的类

## 【重要】Python import 检查
生成 Python 代码时，必须检查所有用到的模块是否已 import：
- csv.reader → import csv
- json.loads → import json
- os.path → import os
- sys.argv → import sys
- re.match → import re
- math.sqrt → import math
- sqlite3.connect → import sqlite3
- argparse.ArgumentParser → import argparse

## 【重要】Python argparse 规则
如果使用 argparse：
- 所有参数必须有 default 值，确保无参数调用也能运行
- 禁止 required=True
- 示例：
  parser.add_argument("--input", default="sample.csv")
  parser.add_argument("--action", default="list")

## 【重要】接口一致性
- 如果下面的"已生成文件的实际内容"里已经定义了函数/类，
  你的代码必须使用完全相同的名字调用
- 不要凭想象 import 或调用不存在的函数
- 函数名和文件名避开语言内置名（如 Python 的 abs、pow、sum、requests 等）

## 【重要】示例数据要包含"可验证的案例"
如果当前文件的职责是创建示例数据（如 CSV / JSON / 数据库）：
- 示例数据必须包含 2-3 个"正面案例"
- 让最终输出有内容，不是全 0 / 全 False
- 例：
  - 统计空值 → 数据里要有空单元格
  - 判断回文 → 数据里要有回文和非回文各 1-2 个
  - 检测重复 → 数据里要有重复项和非重复项

## 【重要】入口脚本约束
- 入口脚本（main.py / Program.cs / index.js）无参数调用时必须能运行
- 如果读取外部文件（CSV/JSON），第一行必须检测文件存在性并创建示例数据
- 示例：

  import os
  import csv

  DATA_FILE = "sample.csv"
  if not os.path.exists(DATA_FILE):
      with open(DATA_FILE, 'w', encoding='utf-8', newline='') as f:
          writer = csv.writer(f)
          writer.writerow(["group", "value"])
          writer.writerow(["groupA", 10])
          writer.writerow(["groupA", 20])
          writer.writerow(["groupB", 30])

## 【重要】build vs run
- dotnet build 只编译，不运行程序
- dotnet run 才运行程序，才能看到输出
- 用户要求"比较性能 / 看输出 / 演示 / 打印结果"时，入口脚本的 Main 必须真正执行功能
- 禁止只写空方法体，必须真的调用并打印结果
""");

        // ═══════════ 项目上下文 ═══════════
        var contextLines = new List<string>
        {
            "## 项目",
            $"整体目标：{plan.Summary}",
            $"语言：{plan.Language}",
            "",
            "## 本项目包含以下文件"
        };

        foreach (var f in plan.Files)
        {
            var marker = f.Path == spec.Path ? "（当前文件）" : "";
            contextLines.Add($"- {f.Path}：{f.Purpose} {marker}");
        }

        // 已生成文件的实际内容
        if (generatedFiles.Count > 0)
        {
            contextLines.Add("");
            contextLines.Add("## 已生成文件的实际内容（供参考接口）");

            foreach (var (path, content) in generatedFiles)
            {
                contextLines.Add($"### {path}");
                contextLines.Add("```");

                var preview = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(content, head: 1500, tail: 0);

                contextLines.Add(preview);
                contextLines.Add("```");
            }
        }

        // 环境约束（外部数据任务时注入）
        if (!string.IsNullOrEmpty(plan.EnvironmentConstraint))
        {
            contextLines.Add("");
            contextLines.Add(plan.EnvironmentConstraint);
        }

        // ═══════════ 当前文件信息 ═══════════
        contextLines.Add("");
        contextLines.Add("## 当前要生成的文件");
        contextLines.Add($"路径：{spec.Path}");
        contextLines.Add($"职责：{spec.Purpose}");
        contextLines.Add($"要求：{spec.Requirements}");

        if (!string.IsNullOrEmpty(spec.Skeleton))
        {
            contextLines.Add($"参考骨架：{spec.Skeleton}");
        }

        contextLines.Add("");
        contextLines.Add($"现在输出 {spec.Path} 的完整内容：");

        history.AddUserMessage(string.Join("\n", contextLines));

        // ═══════════ 调用 LLM ═══════════
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            ExtensionData = new Dictionary<string, object>
            {
                ["think"] = false
            }
        };

        var raw = await CallAsync(chat, history, settings, ct);
        var cleaned = CleanOutput(raw);

        // ═══════════ 后处理 ═══════════
        cleaned = EnsureArgparseDefaults(cleaned, plan.Language, spec.Path);
        cleaned = EnsureCsprojTargetFramework(cleaned, spec.Path);

        return cleaned;
    }

    // ============================================================
    // 修复文件
    // ============================================================

    /// <summary>
    /// 修复文件的编译/运行错误。
    /// </summary>
    public async Task<string> FixFileAsync(
        FileSpec spec,
        string currentContent,
        string errorMessage,
        Plan plan,
        IReadOnlyDictionary<string, string> allFiles,
        CancellationToken ct)
    {
        // 【A：修复输出相同升级链（§14-3l）】修复模型（small）能力不足时可能无效输出原文——
        // 检测"输出与原文相同"→ 升级 small→agent_pro→large 重试，每层至多一次（模型能力层兜底，非提示词）。
        var fixTiers = new[] { RoutingDecision.Small, RoutingDecision.AgentPro, RoutingDecision.Large };
        for (var tierIndex = 0; tierIndex < fixTiers.Length; tierIndex++)
        {
            var chat = _kernel.GetRequiredService<IChatCompletionService>(fixTiers[tierIndex]);
            var history = new ChatHistory();

            // ─────────── System Prompt ───────────
        history.AddSystemMessage("""
            你是代码修复器。根据错误信息，修复给出的代码文件。

            ## 【最高优先级】任务完整性
            - 必须真正实现用户需求，不能只输出"能跑通"的模板
            - 禁止只输出 Hello World
            - 禁止输出占位符（如 // TODO、pass、空方法体）

            ## 输出要求
            - 只输出修复后的完整文件内容
            - 不要用 markdown 代码块包裹
            - 不要输出任何解释、错误分析、修复建议
            - 不要输出"我修改了"、"以下是修复后"这类文字
            - 直接输出完整文件内容

            ## 【修复前的定位】
            - 必读完整文件：错误行号和列号是最直接的线索，先找到那一行
            - 不要随便改方法签名：如果错误信息指向 line 9，就改第 9 行附近的代码
            - 错误信息里有行列号时，先定位到那一行
            - 示例：
                Program.cs(9,26): error CS0266
                → 直接定位 Program.cs 第 9 行第 26 列，改那里

            ## 修复原则
            - 仔细阅读"项目里所有文件"里的实际代码，确保接口一致
            - 不要输出与原文相同的内容——如果没找到修复点，说明你没看懂错误
            - 修复时优先在入口文件加"数据文件不存在则创建示例数据"的逻辑

            ## 【关键】修改已有工程文件的两条铁律（10-14 实测 ShippingService 失败根因）
            - ① 增量修改，禁止全量重写：只改错误行号附近的代码，其余内容原样保留。
              绝不把"项目里所有文件"中**其他文件已定义的类型**（class/interface/struct/record/enum）
              重新声明进当前文件——那会产生 CS0101 重复定义，直接被系统拒绝。
              要复用其他文件的类型，直接引用它（如 `new TaxService()`），不要重新写一遍 class 定义。
            - ② 只引用真实存在的成员：引用任何类型/成员前，先回"项目里所有文件"确认它确实存在。
              绝不凭空引用不存在的成员或类型（如 `Order.Items`、`OrderItem`）——那会编译失败（CS0117/CS0246）。
              需要新成员/新类型时，在对应文件里新建它，而不是假设它已存在。

            ## 【C# 类型规则】
            - 字面量类型必须匹配变量声明：
              - 错误：int x = 1.5;    正确：double x = 1.5;
            - 数值类型转换必须显式：
              - 错误：int a = 3.14;   正确：int a = (int)3.14;
            - CS0266 错误：加 (int) / (double) 强制转换

            ## 常见错误诊断

            ### 1. 找不到名字（NameError）
            - NameError: name 'xxx' is not defined → 变量未定义或漏 import
            - NameError: name 'csv' is not defined → 补 import csv
            - NameError: name 'json' is not defined → 补 import json
            - NameError: name 'os' is not defined → 补 import os
            - NameError: name 'abs' is not defined → 自定义函数名遮蔽内置，改用 builtins.abs

            ### 2. 导入错误
            - ModuleNotFoundError: No module named 'xxx' → import 路径错或文件名冲突
            - ImportError: cannot import name 'xxx' from 'yyy' → yyy.py 里没有 xxx

            ### 3. 属性错误
            - AttributeError: module 'xxx' has no attribute 'yyy' → 函数名不一致

            ### 4. 文件不存在（FileNotFoundError）
            - FileNotFoundError: File not found: xxx.csv
            - No such file or directory

            修复方式：在入口脚本顶部加创建示例数据的逻辑：
                import os
                DATA_FILE = "sample.csv"
                if not os.path.exists(DATA_FILE):
                    # 创建示例数据...

            ### 5. I/O 操作（with 块作用域）
            - ValueError: I/O operation on closed file

            原因：with open(...) as f: 块已退出，但 reader 还在使用。

            修复方式：
                # 错误
                def read_csv(path):
                    with open(path) as f:
                        reader = csv.DictReader(f)
                    return reader   # 错！

                # 正确
                def read_csv(path):
                    with open(path) as f:
                        reader = csv.DictReader(f)
                        return list(reader)   # 返回 list

            ### 6. argparse 错误
            - error: the following arguments are required: --xxx
            - unrecognized arguments: --xxx

            修复方式：给所有 add_argument 加 default 值：
                parser.add_argument("--action", default="list")
                parser.add_argument("--input", default="sample.csv")

            禁止 required=True。

            ### 7. NETSDK 错误
            - NETSDK1138: 目标框架"net5.0"不受支持
            - NETSDK1022: 包含了重复的 Compile 项

            修复方式：把 csproj 的 TargetFramework 改成 net10.0：
                <TargetFramework>net10.0</TargetFramework>

            ### 8. C# 编译错误
            - CS0103 名称不存在 → 检查 using / 类名拼写
            - CS0246 类型/命名空间找不到 → 补 using 或不用第三方包
            - CS0266 类型不能隐式转换 → 加 (int) / (double) 强制转换
            - CS5001 无 Main 方法 → 添加 static void Main

            ### 9. matplotlib 错误
            - 如果用了 plt.bar，改用 ASCII 柱状图：
                for group, avg in results.items():
                    print(f"{group}: {'*' * int(avg)} {avg}")

            ### C# 重复定义错误（CS0101 / CS0111）
            - `CS0101: 命名空间已经包含"xxx"的定义`
            - `CS0111: 类型"xxx"已定义了一个名为"yyy"的具有相同参数类型的成员`

            **原因**：同一个类（如 Calculator）被定义在两个文件里。

            **修复方式**：
            1. 看"项目里所有文件"部分，找到**同时**定义了该类的两个文件
            2. **判断哪个文件应该保留该类**：
               - 有具体方法实现的（如 `Calculator.cs`）→ 保留
               - 入口文件（如 `Program.cs`）→ **删除重复的类，只保留 Main 方法**
            3. 修改"待修复"文件时，**只保留 Program 的 Main 方法**，删掉重复的类定义
            4. 修改后 `Main` 里仍能通过 `new Calculator()` 调用另一个文件里的类

            **示例**：
            - 错误：`Program.cs` 和 `Calculator.cs` 都有 `class Calculator`
            - 正确：**删除 `Program.cs` 里的 `Calculator` 类**，保留 `Calculator.cs` 里的
            - `Program.cs` 只保留 `Program` 类和 `Main` 方法
            """);

        // ─────────── 项目上下文 ───────────
        var contextLines = new List<string>
        {
            "## 项目",
            $"{plan.Summary}（{plan.Language}）",
            "",
            "## 项目里所有文件"
        };

        foreach (var (path, content) in allFiles)
        {
            var marker = path.Equals(spec.Path, StringComparison.OrdinalIgnoreCase)
                ? "  ← 待修复"
                : "";

            contextLines.Add("");
            contextLines.Add($"### {path}{marker}");
            contextLines.Add("```");

            var preview = Agent.Infrastructure.Context.ToolResultNormalizer.Truncate(content, head: 2000, tail: 0);

            contextLines.Add(preview);
            contextLines.Add("```");
        }

        // 环境约束（外部数据任务时注入）
        if (!string.IsNullOrEmpty(plan.EnvironmentConstraint))
        {
            contextLines.Add("");
            contextLines.Add(plan.EnvironmentConstraint);
        }

        // ─────────── 错误信息 ───────────
        contextLines.Add("");
        contextLines.Add("## 错误信息");
        contextLines.Add(errorMessage);
        contextLines.Add("");
        contextLines.Add("## 任务");
        contextLines.Add($"请修复 {spec.Path}，只输出修复后的完整文件内容。");
        contextLines.Add("不要解释，不要 markdown 包裹，不要输出与原文相同的内容。");

        history.AddUserMessage(string.Join("\n", contextLines));

        // ─────────── 调用 LLM ───────────
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            ExtensionData = new Dictionary<string, object>
            {
                ["think"] = false
            }
        };

            var raw = await CallAsync(chat, history, settings, ct);
            var cleaned = CleanOutput(raw);

            // ─────────── 后处理 ───────────
            cleaned = EnsureArgparseDefaults(cleaned, plan.Language, spec.Path);
            cleaned = EnsureCsprojTargetFramework(cleaned, spec.Path);

            // 输出与原文相同 → 升级模型重试（每层至多一次）
            if (!IsSameOutput(cleaned, currentContent))
                return cleaned;

            var nextTier = tierIndex + 1 < fixTiers.Length ? fixTiers[tierIndex + 1] : null;
            if (nextTier is null)
            {
                _logger.LogWarning(
                    "修复输出与原文相同（已到最高层 {Tier}），返回原样，由调用方判修复失败。",
                    fixTiers[tierIndex]);
                return cleaned;
            }

            _logger.LogWarning(
                "修复输出与原文相同（{Tier}），升级到 {Next} 重试。",
                fixTiers[tierIndex], nextTier);
        }

        return currentContent; // 不可达（循环内必 return）
    }

    // ============================================================
    // 后处理
    // ============================================================

    /// <summary>修复输出与原文相同判定（Trim 后精确比较，忽略末尾空白差异）。</summary>
    private static bool IsSameOutput(string cleaned, string original) =>
        string.Equals(cleaned.Trim(), original.Trim(), StringComparison.Ordinal);

    /// <summary>后处理：给 Python argparse 的 add_argument 强制加 default 值。</summary>
    private static string EnsureArgparseDefaults(string code, string language, string filePath)
    {
        if (language != "python")
            return code;

        if (!filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return code;

        if (!code.Contains("add_argument"))
            return code;

        var pattern = @"add_argument\(\s*[""'](--?[\w\-]+)[""']\s*,\s*([^)]*)\)";

        return Regex.Replace(code, pattern, match =>
        {
            var argName = match.Groups[1].Value;
            var restArgs = match.Groups[2].Value.Trim();

            // 已有 default= → 跳过
            if (restArgs.Contains("default=", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            // 有 required=True → 改成 False 并加 default
            if (restArgs.Contains("required=True", StringComparison.OrdinalIgnoreCase))
            {
                restArgs = Regex.Replace(
                    restArgs,
                    @"required\s*=\s*True",
                    "required=False",
                    RegexOptions.IgnoreCase);

                return $"add_argument(\"{argName}\", default=\"\", {restArgs})";
            }

            // 没有 default → 插入
            return $"add_argument(\"{argName}\", default=\"\", {restArgs})";
        });
    }

    /// <summary>后处理：csproj 的 TargetFramework 强制为 net10.0。</summary>
    private static string EnsureCsprojTargetFramework(string code, string filePath)
    {
        if (!filePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return code;

        var result = Regex.Replace(
            code,
            @"<TargetFramework>[^<]*</TargetFramework>",
            "<TargetFramework>net10.0</TargetFramework>",
            RegexOptions.IgnoreCase);

        if (!result.Contains("<TargetFramework>", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result,
                @"<PropertyGroup>",
                "<PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>",
                RegexOptions.IgnoreCase);
        }

        return result;
    }

    // ============================================================
    // 辅助
    // ============================================================

    /// <summary>单次调用 LLM（带重试）。</summary>
    private async Task<string> CallAsync(
        IChatCompletionService chat,
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        CancellationToken ct)
    {
        try
        {
            return await RetryHelper.RetryAsync(
                action: async () =>
                {
                    var response = await chat.GetChatMessageContentAsync(
                        history, settings, cancellationToken: ct);
                    return response.Content ?? "";
                },
                maxAttempts: 3,
                operationName: "Executor.Call",
                logger: _logger,
                ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Executor 调用失败（已重试 3 次）。");
            return $"[Executor 错误] {ex.Message}";
        }
    }

    /// <summary>清理输出：剥离 markdown 代码块、去首尾空白。</summary>
    private static string CleanOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        // 剥离 ```lang ... ``` 包裹
        var match = Regex.Match(
            raw,
            @"^\s*```(?:\w+)?\s*\n([\s\S]*?)\n?\s*```\s*$",
            RegexOptions.Singleline);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // 尝试剥离其它位置的 markdown 标记
        var cleaned = Regex.Replace(
            raw,
            @"```(?:\w+)?\s*\n?",
            "",
            RegexOptions.Singleline);

        cleaned = cleaned.Replace("```", "");

        return cleaned.Trim();
    }
}