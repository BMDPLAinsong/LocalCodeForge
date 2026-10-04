// ============================================================
// Rendering / IRenderer.cs
// 渲染抽象：CLI 壳（Agent.Cli）与未来 UI 宿主共用的渲染契约。
// 设计原则（沿袭内核"单一来源防双路径漂移"惯例）：
//   · 事件渲染逻辑唯一实现在 RendererBase.FormatEvent（纯函数，可测）
//   · 命令块（plan/memory/stats/rollback/use/健康检查）P0 仍由 Program.cs 直出 Console，
//     不在此抽象内——P1 面板化时再下沉。
//   · 颜色语义映射：Info=DarkGray / Notice=Magenta / Warn=Yellow / Error=Red / System=无色
// ============================================================
using Agent.Core.Models;

namespace Agent.Infrastructure.Rendering;

public interface IRenderer
{
    /// <summary>启动横幅（无色 4 行 + 空行）</summary>
    void Banner(string workspace, string dbPath);

    /// <summary>实际生效配置块（无色）</summary>
    void ConfigBlock(AgentOptions options);

    /// <summary>普通/灰色状态行（原 [工作目录] 用 DarkGray）</summary>
    void Info(string text);

    /// <summary>品红提示行（原 [自动编排] 用 Magenta）</summary>
    void Notice(string text);

    /// <summary>黄色警示行（原 [回滚] 用 Yellow）</summary>
    void Warn(string text);

    /// <summary>红色错误行</summary>
    void Error(string text);

    /// <summary>无色系统行（原 [系统] 无色）</summary>
    void System(string text);

    /// <summary>输出输入提示符 "> "</summary>
    void Prompt();

    /// <summary>读取一行输入（交互态/无头态一致，由工厂保证选择正确实现）</summary>
    string? ReadLine();

    /// <summary>
    /// 渲染一个 Agent 事件（七类：Routing/PlanGenerated/FileWritten/ToolCall/ToolResultEvent/FinalAnswer/Error）。
    /// 异步签名：Spectre 实现需要 LiveDisplay 的 StartAsync/StopAsync 生命周期。
    /// </summary>
    Task RenderEventAsync(AgentEvent evt);
}
