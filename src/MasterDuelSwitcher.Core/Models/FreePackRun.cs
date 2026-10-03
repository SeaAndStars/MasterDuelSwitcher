namespace MasterDuelSwitcher.Core.Models;

/// <summary>免费开包当前阶段与累计计数，供界面显示而不包含截图或账号信息。</summary>
public sealed class FreePackProgress
{
    /// <summary>当前执行阶段的中文说明。</summary>
    public string Stage { get; init; } = "";
    /// <summary>已首次检查的不同卡包数量。</summary>
    public int ScannedPacks { get; init; }
    /// <summary>已识别结果并点击确认的免费卡包数量。</summary>
    public int OpenedPacks { get; init; }
}

/// <summary>免费卡包扫描的最终计数与明确结束原因。</summary>
public sealed class FreePackRunResult
{
    /// <summary>本轮已检查的不同卡包数量。</summary>
    public int ScannedPacks { get; init; }
    /// <summary>本轮已确认开包结果的免费卡包数量。</summary>
    public int OpenedPacks { get; init; }
    /// <summary>完成、取消、识别不确定或窗口异常的中文说明。</summary>
    public string Reason { get; init; } = "";
    /// <summary>用户停止、F8 或调用方取消时为真。</summary>
    public bool IsCancelled { get; init; }
}
