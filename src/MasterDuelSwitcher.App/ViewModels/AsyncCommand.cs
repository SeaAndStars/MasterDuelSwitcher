using System.Windows.Input;

namespace MasterDuelSwitcher.App.ViewModels;

/// <summary>支持等待完成和启用状态通知的异步界面命令。</summary>
public sealed class AsyncCommand : ICommand
{
    /// <summary>实际执行的异步操作。</summary>
    private readonly Func<object?, Task> _execute;
    /// <summary>当前操作是否允许执行的判断函数。</summary>
    private readonly Func<bool> _canExecute;
    /// <summary>通知 WPF 重新检查按钮的启用状态。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>创建带有可执行条件的异步命令。</summary>
    public AsyncCommand(Func<object?, Task> execute, Func<bool> canExecute)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>检查界面命令是否允许执行。</summary>
    public bool CanExecute(object? parameter) => _canExecute();

    /// <summary>由 WPF 命令绑定启动异步操作；业务错误由视图模型统一处理。</summary>
    public void Execute(object? parameter) => _ = ExecuteAsync(parameter);

    /// <summary>执行命令并提供可等待的任务，禁用时保持无操作。</summary>
    public Task ExecuteAsync(object? parameter = null) => CanExecute(parameter) ? _execute(parameter) : Task.CompletedTask;

    /// <summary>在忙碌状态变化后更新绑定按钮。</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
