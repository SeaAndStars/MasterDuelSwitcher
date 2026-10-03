namespace MasterDuelSwitcher.Core.Services;

/// <summary>表示原游戏窗口仍存在，但暂时失焦、隐藏或最小化，需要暂停后恢复。</summary>
public sealed class GameWindowTemporarilyUnavailableException : InvalidOperationException
{
    /// <summary>以临时不可用原因创建游戏窗口暂停异常。</summary>
    public GameWindowTemporarilyUnavailableException(string message) : base(message) { }
}
