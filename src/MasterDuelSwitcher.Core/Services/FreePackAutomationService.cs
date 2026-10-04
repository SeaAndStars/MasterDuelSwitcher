using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>提供已核验游戏窗口的截图、输入与停止边界，便于隔离验证自动化流程。</summary>
public interface IGameAutomationPlatform
{
    /// <summary>激活当前游戏窗口，未找到或不可用时抛出异常。</summary>
    void ActivateGame();
    /// <summary>只尝试重新激活本轮原窗口与原进程，临时不可用返回假，身份或几何变化抛出异常。</summary>
    bool TryRecoverGame();
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
    private static readonly TimeSpan ObservationDelay = TimeSpan.FromMilliseconds(80);
    /// <summary>页面未产生有效进展时允许的最长等待。</summary>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(60);
    /// <summary>失焦后允许用户短暂切换窗口，再主动激活原游戏窗口。</summary>
    private static readonly TimeSpan RecoveryPause = TimeSpan.FromSeconds(3);
    /// <summary>单轮允许首次检查的最多卡包数量。</summary>
    private const int MaximumPacks = 200;
    /// <summary>单个结果页允许成功发送的独立确认次数，包含首次确认。</summary>
    private const int MaximumResultAttempts = 30;
    /// <summary>结果确认的递增间隔上限，避免动画未响应后长时间等待下一次点击。</summary>
    private const int MaximumResultIntervalMilliseconds = 600;

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
            logger.LogDebug("FreePackRunStarted RunId={RunId} ObservationMilliseconds={ObservationMilliseconds} MaximumWaitMilliseconds={MaximumWaitMilliseconds} MaximumPacks={MaximumPacks}",
                context.RunId, ObservationDelay.TotalMilliseconds, MaximumWait.TotalMilliseconds, MaximumPacks);
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
            finally
            {
                Volatile.Write(ref running, 0);
                logger.LogDebug("FreePackRunEnded RunId={RunId} Phase={Phase} Frames={Frames} Actions={Actions} LastSuccessfulActionSequence={LastSuccessfulActionSequence} ScannedPacks={ScannedPacks} OpenedPacks={OpenedPacks}",
                    context.RunId, context.Phase.ToString(), context.FrameSequence, context.ActionSequence, context.LastSuccessfulActionSequence, context.Visited.Count, context.OpenedPacks);
            }
        }
    }

    /// <summary>按免费授权、开包结果及返回原包阶段执行一次有限扫描。</summary>
    private async Task<FreePackRunResult> RunCoreAsync(RunContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var observation = CaptureObservation(context, cancellationToken);
                EnsureWithinWait(context);
                if (observation.Screen == PackScreen.Unknown)
                {
                    if (context.Phase == RunPhase.Opening && context.SkipTarget is not null)
                        await SkipAnimationAsync(context, observation, cancellationToken).ConfigureAwait(false);
                    else await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (observation.Screen != PackScreen.Opening &&
                    !(context.Phase == RunPhase.AwaitReturn && observation.Screen == PackScreen.Results) &&
                    context.LastAction is { } previous && SameActionScene(previous, observation))
                {
                    await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (observation.Screen == PackScreen.PackDetails &&
                    (context.Phase == RunPhase.InspectDetails || context.Phase == RunPhase.AwaitNext && !SameIdentity(context.CurrentPack!, observation)))
                {
                    if (string.IsNullOrWhiteSpace(observation.PackTitle) && string.IsNullOrWhiteSpace(observation.Fingerprint))
                        return Finish(context, "卡包图像身份尚未识别，已停止。", true);
                    if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                    logger.LogInformation("卡包当前标题 {CurrentTitle}，首次标题 {FirstTitle}。", observation.PackTitle, context.Visited.FirstOrDefault()?.PackTitle ?? observation.PackTitle);
                    if (context.Visited.Any(pack => SameIdentity(pack, observation)))
                    {
                        logger.LogInformation("卡包重复标题 {RepeatedTitle}，首次标题 {FirstTitle}。", observation.PackTitle, context.Visited[0].PackTitle);
                        return SameIdentity(context.Visited[0], observation)
                            ? Finish(context, "已完成一轮卡包扫描。", false)
                            : Finish(context, "检测到非首包重复导航，已停止。", true);
                    }
                    if (observation.FreeOffer)
                    {
                        if (observation.PrimaryTarget is not { } target)
                            return Finish(context, "免费入口按钮未识别，已停止。", true);
                        Click(context, observation, target, "点击免费入口", cancellationToken);
                        context.Phase = RunPhase.AwaitFreeConfirmation;
                    }
                    else
                    {
                        if (context.Visited.Count == MaximumPacks - 1)
                        {
                            context.Visited.Add(observation);
                            return Finish(context, "已达到 200 包扫描上限。", false);
                        }
                        if (observation.NextTarget is not { } target)
                            return Finish(context, "下一包按钮未识别，已停止。", true);
                        Click(context, observation, target, "切换下一包", cancellationToken);
                        context.Phase = RunPhase.AwaitNext;
                    }
                    context.Visited.Add(observation);
                    context.CurrentPack = observation;
                    context.ReturnCounted = false;
                    context.SkipTarget = null;
                    context.SkipFrame = null;
                    context.SkipClicks = 0;
                    context.SkipInterval = TimeSpan.FromMilliseconds(160);
                    Report(context, observation.FreeOffer ? "等待免费购买确认" : "等待下一卡包详情");
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
                if (context.Phase == RunPhase.Opening && observation.Screen == PackScreen.Opening)
                {
                    await SkipAnimationAsync(context, observation, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (observation.Screen == PackScreen.Results && context.Phase is RunPhase.Opening or RunPhase.AwaitReturn)
                {
                    if (context.Phase == RunPhase.AwaitReturn &&
                        (context.ResultAttempts >= MaximumResultAttempts || RepeatedActionElapsed(context) < context.ResultInterval))
                    {
                        logger.LogDebug("FreePackResultRetryBlocked RunId={RunId} FrameSequence={FrameSequence} Phase={Phase} Attempts={Attempts} Reason={Reason} RequiredIntervalMilliseconds={RequiredIntervalMilliseconds} RequestedWaitMilliseconds={RequestedWaitMilliseconds} FrameElapsedMilliseconds={FrameElapsedMilliseconds}",
                            context.RunId, context.FrameSequence, context.Phase.ToString(), context.ResultAttempts, context.ResultAttempts >= MaximumResultAttempts ? "AttemptLimit" : "Backoff",
                            context.ResultInterval.TotalMilliseconds, context.RepeatDelayElapsed.TotalMilliseconds, (context.LastFrame!.CapturedAtUtc - context.RepeatStartedFrameAt!.Value).TotalMilliseconds);
                        await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (observation.PrimaryTarget is not { } target)
                    {
                        logger.LogDebug("FreePackResultTargetMissing RunId={RunId} FrameSequence={FrameSequence} Phase={Phase} Confidence={Confidence}",
                            context.RunId, context.FrameSequence, context.Phase.ToString(), observation.Confidence);
                        await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                    var firstConfirmation = context.Phase == RunPhase.Opening;
                    logger.LogDebug("FreePackResultConfirmationRequested RunId={RunId} FrameSequence={FrameSequence} Phase={Phase} Attempt={Attempt} ClientX={ClientX} ClientY={ClientY} RequiredIntervalMilliseconds={RequiredIntervalMilliseconds}",
                        context.RunId, context.FrameSequence, context.Phase.ToString(), firstConfirmation ? 1 : context.ResultAttempts + 1, target.X, target.Y, context.ResultInterval.TotalMilliseconds);
                    Click(context, observation, target, "确认卡包结果", cancellationToken, firstConfirmation);
                    context.ResultAttempts = firstConfirmation ? 1 : context.ResultAttempts + 1;
                    context.ResultInterval = firstConfirmation ? TimeSpan.FromMilliseconds(200)
                        : TimeSpan.FromMilliseconds(Math.Min(context.ResultInterval.TotalMilliseconds * 2, MaximumResultIntervalMilliseconds));
                    context.Phase = RunPhase.AwaitReturn;
                    Report(context, "等待返回原卡包详情");
                    continue;
                }
                if (observation.Screen == PackScreen.PackDetails && context.Phase == RunPhase.AwaitReturn)
                {
                    if (!SameIdentity(context.CurrentPack!, observation))
                        return Finish(context, "结果页未返回原卡包详情，已停止。", true);
                    if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) continue;
                    if (!context.ReturnCounted)
                    {
                        context.OpenedPacks++;
                        context.ReturnCounted = true;
                        Report(context, "已确认返回原卡包详情");
                    }
                    if (context.Visited.Count == MaximumPacks) return Finish(context, "已达到 200 包扫描上限。", false);
                    if (observation.NextTarget is not { } target)
                        return Finish(context, "下一包按钮未识别，已停止。", true);
                    Click(context, observation, target, "切换下一包", cancellationToken);
                    context.Phase = RunPhase.AwaitNext;
                    continue;
                }
                if (observation.Screen == PackScreen.PackDetails &&
                    (context.Phase == RunPhase.AwaitNext ||
                     context.Phase == RunPhase.Opening && SameIdentity(context.CurrentPack!, observation)))
                {
                    await DelayAsync(context, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                return Finish(context, "当前画面与免费开包阶段不符，已停止。", true);
            }
            catch (GameWindowTemporarilyUnavailableException exception)
            {
                logger.LogWarning(exception, "游戏窗口暂时不可用，保留当前开包阶段并等待恢复。");
                await RecoverGameAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>在每次捕获前响应停止，并记录最后有效帧和分类匹配分数。</summary>
    private PackObservation CaptureObservation(RunContext context, CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        context.LastFrame = platform.Capture();
        context.FrameSequence++;
        if (context.RecoveryStartedTimestamp is not null)
        {
            EnsureRecoveryWithinWait(context);
            if (context.RecoveryFrame is { } recoveredFirst)
            {
                EnsureSameGeometry(recoveredFirst, context.LastFrame);
                context.RecoveryStartedTimestamp = null;
                context.RecoveryDelayElapsed = TimeSpan.Zero;
                context.RecoveryFrame = null;
            }
            else context.RecoveryFrame = context.LastFrame;
        }
        context.WaitStartedFrameAt ??= context.LastFrame.CapturedAtUtc - context.PreservedFrameElapsed;
        context.RepeatStartedFrameAt ??= context.LastFrame.CapturedAtUtc - context.PreservedRepeatFrameElapsed;
        var observation = recognizer.Recognize(context.LastFrame);
        logger.LogDebug("识别界面 {Screen}，评分 {Confidence}。", observation.Screen, observation.Confidence);
        logger.LogDebug("FreePackObservation RunId={RunId} FrameSequence={FrameSequence} Phase={Phase} Screen={Screen} Confidence={Confidence} FreeOffer={FreeOffer} PrimaryTarget={PrimaryTarget} LastSuccessfulActionSequence={LastSuccessfulActionSequence} LastActionScreen={LastActionScreen} ResultAttempts={ResultAttempts} CapturedAtUtc={CapturedAtUtc}",
            context.RunId, context.FrameSequence, context.Phase.ToString(), observation.Screen, observation.Confidence, observation.FreeOffer, observation.PrimaryTarget,
            context.LastSuccessfulActionSequence, context.LastAction?.Screen, context.ResultAttempts, context.LastFrame.CapturedAtUtc);
        if (observation.Screen == PackScreen.UnverifiedPurchaseDialog ||
            observation.Screen == PackScreen.FreePurchaseDialog && !observation.FreeOffer)
            throw new InvalidOperationException("购买确认未验证免费条件，已停止。");
        return observation;
    }

    /// <summary>使用第二帧核验窗口几何和全部动作条件，任何不同均放弃本次点击。</summary>
    private async Task<bool> IsStableAsync(RunContext context, PackObservation first, CancellationToken cancellationToken)
    {
        var frame = context.LastFrame!;
        var firstFrameSequence = context.FrameSequence;
        await DelayAsync(context, cancellationToken).ConfigureAwait(false);
        var second = CaptureObservation(context, cancellationToken);
        var current = context.LastFrame!;
        logger.LogDebug("FreePackStabilityGeometry RunId={RunId} FirstFrameSequence={FirstFrameSequence} SecondFrameSequence={SecondFrameSequence} FirstWindow={FirstWindow} SecondWindow={SecondWindow} FirstWidth={FirstWidth} SecondWidth={SecondWidth} FirstHeight={FirstHeight} SecondHeight={SecondHeight} FirstX={FirstX} SecondX={SecondX} FirstY={FirstY} SecondY={SecondY}",
            context.RunId, firstFrameSequence, context.FrameSequence, frame.WindowHandle, current.WindowHandle, frame.Width, current.Width, frame.Height, current.Height, frame.ScreenX, current.ScreenX, frame.ScreenY, current.ScreenY);
        EnsureSameGeometry(frame, current);
        EnsureWithinWait(context);
        var stable = SameObservation(first, second);
        logger.LogDebug("FreePackStabilityResult RunId={RunId} FirstFrameSequence={FirstFrameSequence} SecondFrameSequence={SecondFrameSequence} Stable={Stable} FirstScreen={FirstScreen} SecondScreen={SecondScreen} FirstTarget={FirstTarget} SecondTarget={SecondTarget} FirstFreeOffer={FirstFreeOffer} SecondFreeOffer={SecondFreeOffer}",
            context.RunId, firstFrameSequence, context.FrameSequence, stable, first.Screen, second.Screen, first.PrimaryTarget, second.PrimaryTarget, first.FreeOffer, second.FreeOffer);
        return stable;
    }

    /// <summary>动作条件一致才能视为稳定，不以评分的微小波动替代画面身份校验。</summary>
    private static bool SameObservation(PackObservation first, PackObservation second)
        => first.Screen == second.Screen && first.FreeOffer == second.FreeOffer &&
           SameStableIdentity(first, second) && first.PrimaryTarget == second.PrimaryTarget &&
           first.NextTarget == second.NextTarget && first.AnimationSkipTarget == second.AnimationSkipTarget;

    /// <summary>有真实标题时精确校验标题，没有标题的兼容截图仍允许双帧图像轻微抖动。</summary>
    private static bool SameStableIdentity(PackObservation first, PackObservation second)
        => first.PackTitle.Length != 0 || second.PackTitle.Length != 0
            ? string.Equals(first.PackTitle, second.PackTitle, StringComparison.Ordinal)
            : SameFingerprint(first.Fingerprint, second.Fingerprint);

    /// <summary>轮次与当前包使用精确标题；旧测试截图缺标题时精确比较哈希，避免相似卡图被合并。</summary>
    private static bool SameIdentity(PackObservation first, PackObservation second)
        => first.PackTitle.Length != 0 || second.PackTitle.Length != 0
            ? string.Equals(first.PackTitle, second.PackTitle, StringComparison.Ordinal)
            : string.Equals(first.Fingerprint, second.Fingerprint, StringComparison.Ordinal);

    /// <summary>保证新双帧与本轮已验证Skip坐标仍属于相同窗口和客户区几何。</summary>
    private static void EnsureSameGeometry(GameFrame first, GameFrame second)
    {
        if (first.WindowHandle != second.WindowHandle || first.Width != second.Width || first.Height != second.Height ||
            first.ScreenX != second.ScreenX || first.ScreenY != second.ScreenY)
            throw new InvalidOperationException("游戏窗口在双帧核验间发生变化。");
    }

    /// <summary>购买后只复用本包双帧验证的Skip坐标，隐藏按钮点击按递增间隔节流且不续期。</summary>
    private async Task SkipAnimationAsync(RunContext context, PackObservation observation, CancellationToken cancellationToken)
    {
        var candidate = observation.PrimaryTarget ?? observation.AnimationSkipTarget ?? context.SkipTarget;
        if (candidate is not { } target)
        {
            await DelayAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }
        // 空坐标已经在上方守卫排除，后续使用确定的物理坐标值比较与发送输入。
        if (context.SkipTarget is { } cached && observation.PrimaryTarget != observation.AnimationSkipTarget &&
            SameActionScene(context.LastAction!, observation)) target = observation.AnimationSkipTarget ?? cached;
        var repeatSkip = context.SkipTarget is { } previousSkip && target == previousSkip ||
            observation.AnimationSkipTarget is { } currentSkip && target == currentSkip;
        if (repeatSkip && context.SkipClicks > 0 && RepeatedActionElapsed(context) < context.SkipInterval ||
            !repeatSkip && context.LastAction is { } previous && SameActionScene(previous, observation))
        {
            await DelayAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!await IsStableAsync(context, observation, cancellationToken).ConfigureAwait(false)) return;
        if (context.SkipFrame is { } verified) EnsureSameGeometry(verified, context.LastFrame!);
        if (observation.AnimationSkipTarget is { } skip)
        {
            context.SkipTarget = skip;
            context.SkipFrame = context.LastFrame;
        }
        var isSkip = context.SkipTarget is { } verifiedSkip && target == verifiedSkip;
        Click(context, observation, target, "跳过开包动画", cancellationToken, !isSkip || context.SkipClicks == 0);
        if (isSkip)
        {
            context.SkipClicks++;
            if (context.SkipClicks > 1) context.SkipInterval = TimeSpan.FromMilliseconds(Math.Min(context.SkipInterval.TotalMilliseconds * 2, 800));
        }
    }

    /// <summary>取请求等待、截图经过时间与单调经过时间的最大值，避免冻结时钟导致重试不前进。</summary>
    private TimeSpan RepeatedActionElapsed(RunContext context)
    {
        var frameElapsed = context.LastFrame!.CapturedAtUtc - context.RepeatStartedFrameAt!.Value;
        var elapsed = timeProvider.GetElapsedTime(context.RepeatStartedTimestamp);
        return new[] { context.RepeatDelayElapsed, frameElapsed, elapsed }.Max();
    }

    /// <summary>失焦后先可取消暂停三秒，再于统一六十秒期限内只恢复原窗口，成功后丢弃旧帧。</summary>
    private async Task RecoverGameAsync(RunContext context, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var delayed = TimeSpan.Zero;
        context.RecoveryStartedTimestamp ??= started;
        context.RecoveryFrame = null;
        context.PreservedFrameElapsed = context.LastFrame is { } frame && context.WaitStartedFrameAt is { } waitAt
            ? frame.CapturedAtUtc - waitAt : context.PreservedFrameElapsed;
        context.PreservedRepeatFrameElapsed = context.LastFrame is { } repeatedFrame && context.RepeatStartedFrameAt is { } repeatedAt
            ? repeatedFrame.CapturedAtUtc - repeatedAt : context.PreservedRepeatFrameElapsed;
        Report(context, "游戏暂时失焦，等待恢复原窗口");
        while (true)
        {
            ThrowIfStopped(cancellationToken);
            var elapsed = timeProvider.GetElapsedTime(started);
            EnsureRecoveryWithinWait(context);
            if ((delayed >= RecoveryPause || elapsed >= RecoveryPause) && platform.TryRecoverGame())
            {
                EnsureRecoveryWithinWait(context);
                var pausedTicks = timeProvider.GetTimestamp() - started;
                context.WaitStartedTimestamp += pausedTicks;
                context.RepeatStartedTimestamp += pausedTicks;
                context.WaitStartedFrameAt = null;
                context.RepeatStartedFrameAt = null;
                context.LastFrame = null;
                logger.LogInformation("游戏原窗口已恢复，重新双帧核验当前阶段 {Phase}。", context.Phase);
                Report(context, "原游戏窗口已恢复，重新识别当前阶段");
                return;
            }
            await platform.DelayAsync(ObservationDelay, cancellationToken).ConfigureAwait(false);
            delayed += ObservationDelay;
            context.RecoveryDelayElapsed += ObservationDelay;
        }
    }

    /// <summary>连续失焦直到原窗口新双帧捕获前共用六十秒期限，激活成功本身不重置期限。</summary>
    private void EnsureRecoveryWithinWait(RunContext context)
    {
        if (context.RecoveryDelayElapsed >= MaximumWait || timeProvider.GetElapsedTime(context.RecoveryStartedTimestamp!.Value) >= MaximumWait)
            throw new TimeoutException("游戏窗口恢复超过 60 秒，已停止。");
    }

    /// <summary>动作锁把动画按钮最多四像素的定位抖动视作原画面，明显不同位置仍可对应开包与跳过按钮。</summary>
    private static bool SameActionScene(PackObservation first, PackObservation second)
    {
        if (first.Screen == PackScreen.Opening && second.Screen == PackScreen.Opening &&
            second.PrimaryTarget is { } current && first.PrimaryTarget is { } previous)
        {
            return Math.Abs((long)previous.X - current.X) <= 4 && Math.Abs((long)previous.Y - current.Y) <= 4;
        }
        return SameObservation(first, second) &&
            (first.Screen != PackScreen.PackDetails || SameIdentity(first, second));
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
    private void Click(RunContext context, PackObservation observation, PixelPoint point, string stage, CancellationToken cancellationToken, bool resetWait = true)
    {
        ThrowIfStopped(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        double? sincePreviousCompletion = context.LastDiagnosticActionTimestamp is { } previous
            ? Stopwatch.GetElapsedTime(previous, started).TotalMilliseconds : null;
        context.ActionSequence++;
        logger.LogDebug("FreePackActionRequested RunId={RunId} ActionSequence={ActionSequence} FrameSequence={FrameSequence} Phase={Phase} Stage={Stage} Screen={Screen} ClientX={ClientX} ClientY={ClientY} Window={Window} FrameAgeMilliseconds={FrameAgeMilliseconds} SincePreviousCompletionMilliseconds={SincePreviousCompletionMilliseconds}",
            context.RunId, context.ActionSequence, context.FrameSequence, context.Phase.ToString(), stage, observation.Screen, point.X, point.Y, context.LastFrame!.WindowHandle,
            (DateTimeOffset.UtcNow - context.LastFrame.CapturedAtUtc).TotalMilliseconds, sincePreviousCompletion);
        try { platform.Click(context.LastFrame, point); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "FreePackActionFailed RunId={RunId} ActionSequence={ActionSequence} FrameSequence={FrameSequence} Phase={Phase} Stage={Stage} Screen={Screen} InputMilliseconds={InputMilliseconds}",
                context.RunId, context.ActionSequence, context.FrameSequence, context.Phase.ToString(), stage, observation.Screen, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        context.LastDiagnosticActionTimestamp = Stopwatch.GetTimestamp();
        context.LastSuccessfulActionSequence = context.ActionSequence;
        logger.LogDebug("FreePackActionCompleted RunId={RunId} ActionSequence={ActionSequence} FrameSequence={FrameSequence} Phase={Phase} Stage={Stage} Screen={Screen} InputMilliseconds={InputMilliseconds}",
            context.RunId, context.ActionSequence, context.FrameSequence, context.Phase.ToString(), stage, observation.Screen, Stopwatch.GetElapsedTime(started, context.LastDiagnosticActionTimestamp.Value).TotalMilliseconds);
        context.LastAction = observation;
        var timestamp = timeProvider.GetTimestamp();
        if (resetWait)
        {
            context.WaitStartedFrameAt = context.LastFrame!.CapturedAtUtc;
            context.WaitStartedTimestamp = timestamp;
            context.DelayElapsed = TimeSpan.Zero;
        }
        context.RepeatStartedFrameAt = context.LastFrame!.CapturedAtUtc;
        context.RepeatStartedTimestamp = timestamp;
        context.RepeatDelayElapsed = TimeSpan.Zero;
        logger.LogDebug("动作 {Stage}，客户区坐标 {X}, {Y}。", stage, point.X, point.Y);
        Report(context, stage);
    }

    /// <summary>按短间隔等待并累计等待预算，不因未知画面或动画分类变化延长期限。</summary>
    private async Task DelayAsync(RunContext context, CancellationToken cancellationToken)
    {
        ThrowIfStopped(cancellationToken);
        await platform.DelayAsync(ObservationDelay, cancellationToken).ConfigureAwait(false);
        context.DelayElapsed += ObservationDelay;
        context.RepeatDelayElapsed += ObservationDelay;
        if (context.RecoveryStartedTimestamp is not null) context.RecoveryDelayElapsed += ObservationDelay;
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
        /// <summary>关联本轮阶段、帧和动作日志的独立标识。</summary>
        public readonly string RunId = Guid.NewGuid().ToString("N");
        /// <summary>本轮成功捕获的截图序号，包括稳定核验的第二帧。</summary>
        public long FrameSequence;
        /// <summary>本轮已请求的动作序号，失败输入也保留诊断记录。</summary>
        public long ActionSequence;
        /// <summary>最近一次系统输入成功返回的动作序号，仅用于日志关联。</summary>
        public long LastSuccessfulActionSequence;
        /// <summary>最近一次输入完成的诊断时间戳，与业务等待时钟独立。</summary>
        public long? LastDiagnosticActionTimestamp;
        /// <summary>可选界面进度接收器。</summary>
        public readonly IProgress<FreePackProgress>? Progress;
        /// <summary>本轮已检查的卡包观察，轮次仅按精确标题或兼容哈希比较。</summary>
        public readonly List<PackObservation> Visited = [];
        /// <summary>当前免费授权或返回校验所属的卡包标题与兼容图像身份。</summary>
        public PackObservation? CurrentPack;
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
        /// <summary>恢复之前阶段已消耗的截图时间，恢复后用于重建阶段起点。</summary>
        public TimeSpan PreservedFrameElapsed;
        /// <summary>最近成功输入的截图时间，结果重试与Skip节流不借用旧动作锁。</summary>
        public DateTimeOffset? RepeatStartedFrameAt;
        /// <summary>最近成功输入的单调时间戳。</summary>
        public long RepeatStartedTimestamp;
        /// <summary>最近成功输入后累计请求的观察延迟。</summary>
        public TimeSpan RepeatDelayElapsed;
        /// <summary>恢复之前最近成功输入后已消耗的截图时间。</summary>
        public TimeSpan PreservedRepeatFrameElapsed;
        /// <summary>本包免费购买后经双帧验证的Skip位置，跨包即清除。</summary>
        public PixelPoint? SkipTarget;
        /// <summary>验证本包Skip时的窗口几何，仅用于复验隐藏按钮的位置。</summary>
        public GameFrame? SkipFrame;
        /// <summary>当前包成功发送Skip的次数，用于确定节流间隔。</summary>
        public int SkipClicks;
        /// <summary>下一次Skip允许发送前的等待间隔，最高八百毫秒。</summary>
        public TimeSpan SkipInterval = TimeSpan.FromMilliseconds(160);
        /// <summary>当前结果页面成功发送确认的次数，最多三十次，每次均独立按下并松开。</summary>
        public int ResultAttempts;
        /// <summary>下一次结果确认前的递增间隔，最高六百毫秒，期间仍逐帧观察。</summary>
        public TimeSpan ResultInterval;
        /// <summary>当前包已双帧返回原详情并计数，下一包输入暂时失败也不重复累加。</summary>
        public bool ReturnCounted;
        /// <summary>本次连续失焦的起始单调时间，只有原窗口新双帧捕获后清除。</summary>
        public long? RecoveryStartedTimestamp;
        /// <summary>连续恢复期间累计请求的延迟，冻结时钟也保持六十秒上限。</summary>
        public TimeSpan RecoveryDelayElapsed;
        /// <summary>恢复后首个有效窗口帧，第二个同几何新帧才结束连续恢复预算。</summary>
        public GameFrame? RecoveryFrame;
        /// <summary>保存进度接收器与初始单调时间。</summary>
        public RunContext(IProgress<FreePackProgress>? progress, long timestamp) { Progress = progress; WaitStartedTimestamp = timestamp; }
    }
}
