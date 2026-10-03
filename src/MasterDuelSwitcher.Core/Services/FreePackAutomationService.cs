using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Numerics;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>提供已核验游戏窗口的截图、输入与停止边界，便于隔离验证自动化流程。</summary>
public interface IGameAutomationPlatform
{
    /// <summary>激活当前游戏窗口，未找到或不可用时抛出异常。</summary>
    void ActivateGame();
    /// <summary>捕获保持前台和原位置的游戏客户区，异常时拒绝继续自动化。</summary>
    GameFrame Capture();
    /// <summary>再次核验截图所属窗口与几何状态后点击客户区坐标。</summary>
    void Click(GameFrame frame, PixelPoint point);
    /// <summary>用户按下 F8 或系统边界要求停止时为真。</summary>
    bool IsStopRequested { get; }
    /// <summary>在两次观察之间可取消地等待。</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    /// <summary>将最后有效截图和停止原因保存到受容量限制的诊断目录。</summary>
    void SaveDiagnostic(GameFrame frame, string reason);
    /// <summary>结束本次输入会话并卸载停止热键，激活部分失败时仍执行清理。</summary>
    void EndAutomation();
}

/// <summary>扫描当前账号卡包，仅执行已确认免费入口和免费购买对话框的开包流程。</summary>
public interface IFreePackAutomationService
{
    /// <summary>运行一轮有限扫描，通过进度报告阶段与计数，取消返回正常停止结果。</summary>
    Task<FreePackRunResult> RunAsync(IProgress<FreePackProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>以稳定双帧、免费授权和页面转换约束驱动可取消的免费开包流程。</summary>
public sealed class FreePackAutomationService : IFreePackAutomationService
{
    /// <summary>识别已捕获截图的保守模板分类器。</summary>
    private readonly IPackRecognizer recognizer;
    /// <summary>与真实窗口交互的可替换系统边界。</summary>
    private readonly IGameAutomationPlatform platform;
    /// <summary>记录阶段、匹配分数、动作与停止异常的日志。</summary>
    private readonly ILogger<FreePackAutomationService> logger;
    /// <summary>提供可验证的单调时间，真实截图或处理耗时仍计入等待期限。</summary>
    private readonly TimeProvider timeProvider;
    /// <summary>同一服务只允许一个运行者持有的原子门禁。</summary>
    private int running;
    /// <summary>两次画面观察之间的短等待间隔。</summary>
    private static readonly TimeSpan ObservationDelay = TimeSpan.FromMilliseconds(250);
    /// <summary>页面未产生有效进展时允许的最长等待。</summary>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    /// <summary>单轮允许首次检查的最多卡包数量。</summary>
    private const int MaximumPacks = 200;

    /// <summary>创建依赖截图识别与已核验游戏窗口边界的免费开包服务。</summary>
    public FreePackAutomationService(IPackRecognizer recognizer, IGameAutomationPlatform platform, ILogger<FreePackAutomationService>? logger = null, TimeProvider? timeProvider = null)
    {
        this.recognizer = recognizer;
        this.platform = platform;
        this.logger = logger ?? NullLogger<FreePackAutomationService>.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>运行有限免费开包扫描，返回计数和停止原因。</summary>
    public async Task<FreePackRunResult> RunAsync(IProgress<FreePackProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            return new FreePackRunResult { Reason = "免费开包正在执行，请先停止当前任务。" };
        var context = new RunContext(progress, 0);
        try
        {
            context.WaitStartedTimestamp = timeProvider.GetTimestamp();
            ThrowIfStopped(cancellationToken);
            logger.LogInformation("开始免费卡包扫描。");
            Report(context, "正在识别卡包详情");
            return await Task.Run(async () =>
            {
                ThrowIfStopped(cancellationToken);
                platform.ActivateGame();
                return await RunCoreAsync(context, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("免费卡包扫描已取消，扫描 {ScannedPacks} 包，确认 {OpenedPacks} 包。", context.Visited.Count, context.OpenedPacks);
            return Result(context, "用户已停止自动开包。", true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "免费卡包扫描因窗口、识别或输入异常停止。");
            var reason = "自动开包已停止：" + exception.Message;
            SaveDiagnostic(context, reason);
            return Result(context, reason);
        }
        finally
        {
            try { platform.EndAutomation(); }
            catch (Exception exception) { logger.LogWarning(exception, "免费开包会话清理失败，保留原结束结果。"); }
            finally { Volatile.Write(ref running, 0); }
        }
    }

    /// <summary>按免费授权、开包结果及返回原包阶段执行一次有限扫描。</summary>
    private async Task<FreePackRunResult> RunCoreAsync(RunContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            var observation = CaptureObservation(context, cancellationToken);
            EnsureWithinWait(context);
            if (observation.Screen == PackScreen.Unknown ||
                context.LastAction is { } previous && SameActionScene(previous, observation))
            {
                await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (context.Phase == RunPhase.AwaitNext && observation.Screen == PackScreen.PackDetails &&
                !SameFingerprint(context.CurrentFingerprint, observation.Fingerprint))
                context.Phase = RunPhase.InspectDetails;

            if (observation.Screen == PackScreen.PackDetails && context.Phase == RunPhase.InspectDetails)
            {
                if (string.IsNullOrWhiteSpace(observation.Fingerprint))
                    return Finish(context, "卡包图像身份尚未识别，已停止。", true);
                if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                if (context.Visited.Any(fingerprint => SameFingerprint(fingerprint, observation.Fingerprint)))
                    return Finish(context, "已完成一轮卡包扫描。", false);
                context.Visited.Add(observation.Fingerprint);
                context.CurrentFingerprint = observation.Fingerprint;
                if (observation.FreeOffer)
                {
                    if (observation.PrimaryTarget is not { } target)
                        return Finish(context, "免费入口按钮未识别，已停止。", true);
                    Click(context, observation, target, "点击免费入口", cancellationToken);
                    context.Phase = RunPhase.AwaitFreeConfirmation;
                }
                else
                {
                    if (context.Visited.Count == MaximumPacks) return Finish(context, "已达到 200 包扫描上限。", false);
                    if (observation.NextTarget is not { } target)
                        return Finish(context, "下一包按钮未识别，已停止。", true);
                    Click(context, observation, target, "切换下一包", cancellationToken);
                    context.Phase = RunPhase.AwaitNext;
                }
                continue;
            }
            if (observation.Screen == PackScreen.FreePurchaseDialog && context.Phase == RunPhase.AwaitFreeConfirmation)
            {
                if (observation.PrimaryTarget is not { } target)
                    return Finish(context, "免费购买按钮未识别，已停止。", true);
                if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                Click(context, observation, target, "确认免费购买", cancellationToken);
                context.Phase = RunPhase.Opening;
                continue;
            }
            if (context.Phase == RunPhase.Opening && observation.Screen is PackScreen.Opening or PackScreen.Results)
            {
                if (observation.PrimaryTarget is not { } target)
                {
                    await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                if (observation.Screen == PackScreen.Results)
                {
                    Click(context, observation, target, "确认卡包结果", cancellationToken);
                    context.OpenedPacks++;
                    context.Phase = RunPhase.AwaitReturn;
                    Report(context, "等待返回原卡包详情");
                }
                else Click(context, observation, target, "跳过开包动画", cancellationToken);
                continue;
            }
            if (observation.Screen == PackScreen.PackDetails && context.Phase == RunPhase.AwaitReturn)
            {
                if (!SameFingerprint(context.CurrentFingerprint, observation.Fingerprint))
                    return Finish(context, "结果页未返回原卡包详情，已停止。", true);
                if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                if (context.Visited.Count == MaximumPacks) return Finish(context, "已达到 200 包扫描上限。", false);
                if (observation.NextTarget is not { } target)
                    return Finish(context, "下一包按钮未识别，已停止。", true);
                Click(context, observation, target, "切换下一包", cancellationToken);
                context.Phase = RunPhase.AwaitNext;
                continue;
            }
            if (observation.Screen == PackScreen.PackDetails && context.Phase == RunPhase.AwaitNext ||
                observation.Screen == PackScreen.Results && context.Phase == RunPhase.AwaitReturn)
            {
                await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                continue;
            }
            return Finish(context, "当前画面与免费开包阶段不符，已停止。", true);
        }
    }

    /// <summary>在每次捕获前响应停止，并记录最后有效帧和分类匹配分数。</summary>
    private PackObservation CaptureObservation(RunContext context, CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        context.LastFrame = platform.Capture();
        context.WaitStartedFrameAt ??= context.LastFrame.CapturedAtUtc;
        var observation = recognizer.Recognize(context.LastFrame);
        logger.LogDebug("识别界面 {Screen}，评分 {Confidence}。", observation.Screen, observation.Confidence);
        if (observation.Screen == PackScreen.UnverifiedPurchaseDialog ||
            observation.Screen == PackScreen.FreePurchaseDialog && !observation.FreeOffer)
            throw new InvalidOperationException("购买确认未验证免费条件，已停止。");
        return observation;
    }

    /// <summary>使用第二帧核验窗口几何和全部动作条件，任何不同均放弃本次点击。</summary>
    private async Task<bool> IsStableAsync(RunContext context, PackObservation first, CancellationToken cancellationToken)
    {
        var frame = context.LastFrame!;
        await DelayAsync(context, cancellationToken).ConfigureAwait(false);
        var second = CaptureObservation(context, cancellationToken);
        var current = context.LastFrame!;
        if (frame.WindowHandle != current.WindowHandle || frame.Width != current.Width || frame.Height != current.Height ||
            frame.ScreenX != current.ScreenX || frame.ScreenY != current.ScreenY)
            throw new InvalidOperationException("游戏窗口在双帧核验间发生变化。");
        EnsureWithinWait(context);
        return SameObservation(first, second);
    }

    /// <summary>动作条件一致才能视为稳定，不以评分的微小波动替代画面身份校验。</summary>
    private static bool SameObservation(PackObservation first, PackObservation second)
        => first.Screen == second.Screen && first.FreeOffer == second.FreeOffer &&
           SameFingerprint(first.Fingerprint, second.Fingerprint) && first.PrimaryTarget == second.PrimaryTarget && first.NextTarget == second.NextTarget;

    /// <summary>动作锁把动画按钮最多四像素的定位抖动视作原画面，明显不同位置仍可对应开包与跳过按钮。</summary>
    private static bool SameActionScene(PackObservation first, PackObservation second)
    {
        if (first.Screen == PackScreen.Opening && second.Screen == PackScreen.Opening && second.PrimaryTarget is { } current)
        {
            var previous = first.PrimaryTarget!.Value;
            return Math.Abs((long)previous.X - current.X) <= 4 && Math.Abs((long)previous.Y - current.Y) <= 4;
        }
        return SameObservation(first, second);
    }

    /// <summary>十六位卡图差异哈希允许最多四位动效抖动，其他标识按原文精确比较。</summary>
    private static bool SameFingerprint(string first, string second)
    {
        if (first.Length == 16 && second.Length == 16 &&
            ulong.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var left) &&
            ulong.TryParse(second, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var right))
            return BitOperations.PopCount(left ^ right) <= 4;
        return string.Equals(first, second, StringComparison.Ordinal);
    }

    /// <summary>在稳定核验后再次响应停止，成功点击才记忆动作并重置无进展期限。</summary>
    private void Click(RunContext context, PackObservation observation, PixelPoint point, string stage, CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        platform.Click(context.LastFrame!, point);
        context.LastAction = observation;
        context.WaitStartedFrameAt = context.LastFrame!.CapturedAtUtc;
        context.WaitStartedTimestamp = timeProvider.GetTimestamp();
        context.DelayElapsed = TimeSpan.Zero;
        logger.LogDebug("动作 {Stage}，客户区坐标 {X}, {Y}。", stage, point.X, point.Y);
        Report(context, stage);
    }

    /// <summary>按短间隔等待并累计等待预算，不因未知画面或动画分类变化延长期限。</summary>
    private async Task DelayAsync(RunContext context, CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        await platform.DelayAsync(ObservationDelay, cancellationToken).ConfigureAwait(false);
        context.DelayElapsed += ObservationDelay;
    }

    /// <summary>累计延迟、截图 UTC 差值与单调实际时间任一路径达到六十秒均中止。</summary>
    private void EnsureWithinWait(RunContext context)
    {
        if (context.DelayElapsed >= MaximumWait ||
            context.LastFrame!.CapturedAtUtc - context.WaitStartedFrameAt!.Value >= MaximumWait ||
            timeProvider.GetElapsedTime(context.WaitStartedTimestamp) >= MaximumWait)
            throw new TimeoutException("页面等待超过 60 秒，已停止。");
    }

    /// <summary>调用方取消或 F8 停止均走正常取消出口，不再发送点击。</summary>
    private void ThrowIfStopped(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (platform.IsStopRequested) throw new OperationCanceledException("F8 已请求停止。");
    }

    /// <summary>报告当前中文阶段和计数，不暴露截图正文或账号信息。</summary>
    private static void Report(RunContext context, string stage)
        => context.Progress?.Report(new FreePackProgress { Stage = stage, ScannedPacks = context.Visited.Count, OpenedPacks = context.OpenedPacks });

    /// <summary>结束扫描，按需保存诊断并报告最终计数和原因。</summary>
    private FreePackRunResult Finish(RunContext context, string reason, bool diagnostic)
    {
        if (diagnostic) SaveDiagnostic(context, reason);
        logger.LogInformation("免费卡包扫描结束，扫描 {ScannedPacks} 包，确认 {OpenedPacks} 包：{Reason}", context.Visited.Count, context.OpenedPacks, reason);
        Report(context, reason);
        return Result(context, reason);
    }

    /// <summary>使用最后有效截图保存诊断，诊断自身失败只记录日志而不恢复输入。</summary>
    private void SaveDiagnostic(RunContext context, string reason)
    {
        if (context.LastFrame is not { } frame) return;
        try { platform.SaveDiagnostic(frame, reason); }
        catch (Exception exception) { logger.LogWarning(exception, "免费开包诊断保存失败，任务仍保持停止。"); }
    }

    /// <summary>创建独立最终快照，取消与异常都保留已成功确认的计数。</summary>
    private static FreePackRunResult Result(RunContext context, string reason, bool cancelled = false)
        => new() { ScannedPacks = context.Visited.Count, OpenedPacks = context.OpenedPacks, Reason = reason, IsCancelled = cancelled };

    /// <summary>免费开包所需的有限业务阶段。</summary>
    private enum RunPhase
    {
        /// <summary>首次检查当前详情并选择免费入口或下一包。</summary>
        InspectDetails,
        /// <summary>持有当前包免费入口授权，等待验证免费购买框。</summary>
        AwaitFreeConfirmation,
        /// <summary>购买授权已消耗，只允许动画跳过或确认结果。</summary>
        Opening,
        /// <summary>结果已确认，只允许返回当前卡包详情。</summary>
        AwaitReturn,
        /// <summary>已请求下一包，必须等待不同卡图出现。</summary>
        AwaitNext
    }

    /// <summary>仅属于一次扫描的身份、动作锁与无进展计时状态。</summary>
    private sealed class RunContext
    {
        /// <summary>可选界面进度接收器。</summary>
        public readonly IProgress<FreePackProgress>? Progress;
        /// <summary>本轮已检查的卡包指纹，按哈希容差比较。</summary>
        public readonly List<string> Visited = [];
        /// <summary>当前免费授权或返回校验所属的卡包指纹。</summary>
        public string CurrentFingerprint = "";
        /// <summary>已经确认返回的开包结果数量。</summary>
        public int OpenedPacks;
        /// <summary>最新有效截图，异常时作为诊断依据。</summary>
        public GameFrame? LastFrame;
        /// <summary>上次点击的观察签名，相同页面禁止重复动作。</summary>
        public PackObservation? LastAction;
        /// <summary>当前有限业务阶段，默认检查详情。</summary>
        public RunPhase Phase;
        /// <summary>上次动作或运行起点的帧时间。</summary>
        public DateTimeOffset? WaitStartedFrameAt;
        /// <summary>上次动作或运行起点的单调时间戳。</summary>
        public long WaitStartedTimestamp;
        /// <summary>上次动作以来累计请求的等待时间。</summary>
        public TimeSpan DelayElapsed;
        /// <summary>保存进度接收器与初始单调时间。</summary>
        public RunContext(IProgress<FreePackProgress>? progress, long timestamp) { Progress = progress; WaitStartedTimestamp = timestamp; }
    }
}
