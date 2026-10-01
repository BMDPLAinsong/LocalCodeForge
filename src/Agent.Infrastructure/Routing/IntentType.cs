// 命名空间
namespace Agent.Infrastructure.Routing;

/// <summary>
/// 用户意图类型。
/// 由本地 0.8B 分类器输出，决定任务走本地还是云端。
/// </summary>
public enum IntentType
{
    /// <summary>简单问答。闲聊、概念解释、简单咨询。走本地 7B。</summary>
    SimpleQA,

    /// <summary>代码生成。写新代码或修改已有代码。走本地 7B。</summary>
    CodeGenerate,

    /// <summary>文件处理。列目录、读写文件、整理目录。走本地工具。</summary>
    FileProcess,

    /// <summary>联网搜索。需要实时信息、最新数据。走云端。</summary>
    Search,

    /// <summary>复杂推理。架构设计、多步规划、复杂分析。走云端。</summary>
    ComplexReason,

    /// <summary>未知意图。分类失败或无法判断。走云端兜底。</summary>
    Unknown
}