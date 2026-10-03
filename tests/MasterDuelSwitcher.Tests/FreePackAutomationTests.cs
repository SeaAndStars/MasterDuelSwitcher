using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>以录制观察序列和虚构窗口输入验证免费开包，不启动或操作真实游戏。</summary>
public sealed class FreePackAutomationTests
{
    /// <summary>详情、免费确认、动画和结果组成一次免费开包，返回原包后才切换下一包。</summary>
    [Fact]
    public async Task FreePackCompletesBeforeAdvancingAndStopsAfterOneCycle()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Results(), Detail("a", true), Detail("b"), Detail("a", true));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.False(result.IsCancelled);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(new[] { PackScreen.PackDetails, PackScreen.FreePurchaseDialog, PackScreen.Opening, PackScreen.Results, PackScreen.PackDetails, PackScreen.PackDetails }, fixture.Platform.Clicks.Select(click => click.Screen));
        Assert.Contains(fixture.Progress.Values, item => item.OpenedPacks == 1 && item.ScannedPacks == 1);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("评分"));
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>购买弹窗消退后短暂显示同包详情时，只等待开包页面，不重复购买或切换下一包。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePackDetailsAfterPurchaseWaitsForOpeningAndResults(bool free)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Detail("a", free), Opening(true), Results(),
            Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(new[] { PackScreen.PackDetails, PackScreen.FreePurchaseDialog, PackScreen.Opening,
            PackScreen.Results, PackScreen.PackDetails, PackScreen.PackDetails }, fixture.Platform.Clicks.Select(click => click.Screen));
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>购买后持续停留同包详情仍遵守等待上限，授权已消耗且不会再次点击。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePackDetailsAfterPurchaseRetainsBoundedWait(bool free)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Detail("a", free));
        var result = await fixture.RunAsync();
        Assert.Contains("等待", result.Reason);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Single(fixture.Platform.Diagnostics);
        Assert.True(fixture.Platform.CaptureCount < 260);
    }

    /// <summary>购买后意外进入另一包详情仍立即停止，过渡等待只授权原卡包。</summary>
    [Fact]
    public async Task DifferentPackDetailsAfterPurchaseStopsWithoutAnyAdditionalAction()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Detail("b"));
        var result = await fixture.RunAsync();
        Assert.Contains("阶段", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>等待购买后的同包过渡时，F8仍正常取消且保留一次入口和一次确认的动作记录。</summary>
    [Fact]
    public async Task SamePackDetailsAfterPurchaseRespondsToEmergencyStop()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Detail("a"));
        fixture.Platform.StopAfterCaptures = 7;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Empty(fixture.Platform.Diagnostics);
        Assert.Equal(1, fixture.Platform.EndCalls);
    }

    /// <summary>付费详情只点击下一包按钮，免费购买授权始终不存在。</summary>
    [Fact]
    public async Task PaidPacksOnlyAdvanceWithoutPurchase()
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.All(fixture.Platform.Clicks, click => Assert.Equal(new PixelPoint(90, 50), click.Point));
    }

    /// <summary>同一卡图至多四位指纹抖动仍视为已扫描卡包，避免动效导致重复绕圈。</summary>
    [Fact]
    public async Task SmallHashChangesStillCompleteTheSameCarouselCycle()
    {
        var fixture = new Fixture(Detail("0000000000000000"), Detail("ffffffffffffffff"), Detail("000000000000000f"));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
    }

    /// <summary>同一服务同时运行第二次立即拒绝，首轮完成后门禁重新允许运行。</summary>
    [Fact]
    public async Task AServiceInstanceOnlyAllowsOneConcurrentRun()
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Platform.DelayHook = () => { entered.TrySetResult(true); return release.Task; };
        var first = fixture.RunAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await fixture.RunAsync();
        Assert.Contains("正在", rejected.Reason);
        Assert.Equal(1, fixture.Platform.Activations);
        Assert.Equal(0, fixture.Platform.EndCalls);
        release.SetResult(true);
        await first;
        Assert.Equal(1, fixture.Platform.EndCalls);
        fixture.Platform.Restart();
        var next = await fixture.RunAsync();
        Assert.Equal(2, next.ScannedPacks);
        Assert.Equal(2, fixture.Platform.Activations);
        Assert.Equal(2, fixture.Platform.EndCalls);
    }

    /// <summary>未观察免费入口或未确认免费文字的对话框均停止，不点击购买。</summary>
    [Theory]
    [InlineData(PackScreen.FreePurchaseDialog, true)]
    [InlineData(PackScreen.FreePurchaseDialog, false)]
    [InlineData(PackScreen.UnverifiedPurchaseDialog, false)]
    [InlineData(PackScreen.Results, false)]
    [InlineData(PackScreen.Opening, false)]
    public async Task InitialNonDetailScreenNeverGetsPurchasePermission(PackScreen screen, bool free)
    {
        var fixture = new Fixture(new PackObservation(screen, new PixelPoint(50, 50), null, free, "", .99));
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.ScannedPacks);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.NotEmpty(result.Reason);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>免费入口后出现未验证购买框仍立即停止，授权不覆盖付费或模糊确认。</summary>
    [Theory]
    [InlineData(PackScreen.UnverifiedPurchaseDialog, false)]
    [InlineData(PackScreen.FreePurchaseDialog, false)]
    public async Task FreeEntryDoesNotPermitUnverifiedPurchase(PackScreen screen, bool free)
    {
        var fixture = new Fixture(Detail("a", true), new PackObservation(screen, new PixelPoint(50, 50), null, free, "", .99));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>第二验证帧出现不确定购买框必须立即停止，后续免费框不得重新获得该入口授权。</summary>
    [Theory]
    [InlineData(PackScreen.UnverifiedPurchaseDialog, false)]
    [InlineData(PackScreen.FreePurchaseDialog, false)]
    public async Task AnUnverifiedSecondFrameImmediatelyStopsThePurchase(PackScreen screen, bool free)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Detail("a", true), Detail("b"), Detail("a", true));
        fixture.Platform.Observations = new[] { Detail("a", true), Detail("a", true), Dialog(),
            new PackObservation(screen, new PixelPoint(50, 60), null, free, "", .99) }
            .Concat(fixture.Platform.Observations.Skip(2)).ToArray();
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        Assert.Single(fixture.Platform.Diagnostics);
        Assert.Equal(4, fixture.Platform.CaptureCount);
    }

    /// <summary>点击后的同一页面保持不变时只点击一次，并在六十秒内停止等待。</summary>
    [Theory]
    [InlineData(PackScreen.PackDetails)]
    [InlineData(PackScreen.FreePurchaseDialog)]
    [InlineData(PackScreen.Opening)]
    [InlineData(PackScreen.Results)]
    public async Task UnchangedScreenNeverRepeatsAnAction(PackScreen screen)
    {
        var sequence = new List<PackObservation> { Detail("a", true) };
        if (screen != PackScreen.PackDetails) sequence.Add(Dialog());
        if (screen is PackScreen.Opening or PackScreen.Results) sequence.Add(Opening(true));
        if (screen == PackScreen.Results) sequence.Add(Results());
        var fixture = new Fixture(sequence.ToArray());
        var result = await fixture.RunAsync();
        Assert.Contains("等待", result.Reason);
        Assert.Equal(sequence.Count, fixture.Platform.Clicks.Count);
        Assert.Single(fixture.Platform.Diagnostics);
        Assert.True(fixture.Platform.CaptureCount < 150);
    }

    /// <summary>网络未知画面和无按钮动画交替出现也不重置无进展等待期限。</summary>
    [Fact]
    public async Task AlternatingUnknownAndAnimationCannotExtendTimeout()
    {
        var fixture = new Fixture(Detail("a", true), Dialog());
        fixture.Platform.Tail = count => count % 2 == 0 ? Unknown() : Opening(false);
        var result = await fixture.RunAsync();
        Assert.Contains("等待", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.True(fixture.Platform.CaptureCount < 100);
    }

    /// <summary>未知起始画面有限等待后停止且不输入。</summary>
    [Fact]
    public async Task UnknownScreenTimesOutWithoutInput()
    {
        var fixture = new Fixture(Unknown());
        var result = await fixture.RunAsync();
        Assert.Contains("等待", result.Reason);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>结果后返回不同卡包时停止，防止确认页识别错误导致跳包。</summary>
    [Fact]
    public async Task ResultsMustReturnToTheOriginalPackBeforeAdvancing()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Detail("b"));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(1, result.ScannedPacks);
        Assert.Equal(3, fixture.Platform.Clicks.Count);
        Assert.Contains("原卡包", result.Reason);
    }

    /// <summary>两百包扫描上限停止，不检查或点击第二百零一包。</summary>
    [Fact]
    public async Task TwoHundredPaidPacksStopAtTheLimit()
    {
        var fixture = new Fixture(Enumerable.Range(0, 201).Select(index => Detail("pack-" + index)).ToArray());
        var result = await fixture.RunAsync();
        Assert.Equal(200, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Equal(199, fixture.Platform.Clicks.Count);
        Assert.Contains("200", result.Reason);
    }

    /// <summary>第二百包为免费时完成结果并返回详情，然后停止而不点击下一包。</summary>
    [Fact]
    public async Task TwoHundredthFreePackCanFinishBeforeTheScanLimitStops()
    {
        var sequence = Enumerable.Range(0, 199).Select(index => Detail("pack-" + index))
            .Concat(new[] { Detail("last", true), Dialog(), Results(), Detail("last", true) }).ToArray();
        var fixture = new Fixture(sequence);
        var result = await fixture.RunAsync();
        Assert.Equal(200, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(202, fixture.Platform.Clicks.Count);
    }

    /// <summary>双帧间免费标志、身份或目标发生变化时拒绝原动作。</summary>
    [Theory]
    [InlineData("screen")]
    [InlineData("free")]
    [InlineData("fingerprint")]
    [InlineData("primary")]
    [InlineData("next")]
    [InlineData("next-absent")]
    [InlineData("next-appeared")]
    public async Task UnstableFramesNeverClickTheFirstObservation(string changed)
    {
        var fixture = new Fixture(Detail("a", true));
        var first = changed == "next-appeared" ? Detail("a", true) with { NextTarget = null } : Detail("a", true);
        var second = changed switch
        {
            "screen" => Unknown(),
            "free" => first with { FreeOffer = false },
            "fingerprint" => first with { Fingerprint = "b" },
            "primary" => first with { PrimaryTarget = new PixelPoint(51, 50) },
            "next-absent" => first with { NextTarget = null },
            _ => first with { NextTarget = new PixelPoint(91, 50) }
        };
        fixture.Platform.Observations = [first, second];
        fixture.Platform.StopAfterCaptures = 3;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>稳定帧之间窗口句柄、大小或位置变化时停止，避免使用旧坐标输入。</summary>
    [Theory]
    [InlineData("handle")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("x")]
    [InlineData("y")]
    public async Task WindowGeometryChangeStopsBeforeClick(string changed)
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.ChangeFrame = (count, frame) => count == 2 ? changed switch
        {
            "handle" => frame with { WindowHandle = 23 },
            "width" => frame with { Width = 101 },
            "height" => frame with { Height = 101 },
            "x" => frame with { ScreenX = 11 },
            _ => frame with { ScreenY = 11 }
        } : frame;
        var result = await fixture.RunAsync();
        Assert.Empty(fixture.Platform.Clicks);
        Assert.Contains("窗口", result.Reason);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>预取消令牌在激活窗口前正常结束。</summary>
    [Fact]
    public async Task CancelledTokenDoesNotActivateTheGame()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new Fixture(Detail("a", true));
        var result = await fixture.Service.RunAsync(null, cancellation.Token);
        Assert.True(result.IsCancelled);
        Assert.Equal(0, fixture.Platform.Activations);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>F8停止在启动、首次捕获或双帧验证后均不追加点击。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task F8StopsWithoutAdditionalInput(int captures)
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.StopAfterCaptures = captures;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>等待期间令牌取消正常返回，不执行第二帧输入。</summary>
    [Fact]
    public async Task CancellationDuringFrameDelayDoesNotClick()
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.CancelDelay = true;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>激活、捕获、识别和点击异常均记录实际异常并停止，已有帧则保存诊断。</summary>
    [Theory]
    [InlineData("activate")]
    [InlineData("capture")]
    [InlineData("recognize")]
    [InlineData("click")]
    [InlineData("delay")]
    public async Task PlatformAndRecognitionFailureStopWithDiagnostics(string operation)
    {
        var fixture = new Fixture(Detail("a", true));
        var failure = new InvalidOperationException("测试窗口边界异常");
        fixture.Platform.Failure = operation == "recognize" ? null : (operation, failure);
        fixture.Recognizer.Failure = operation == "recognize" ? failure : null;
        var result = await fixture.RunAsync();
        Assert.False(result.IsCancelled);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, failure));
        Assert.Equal(operation is "activate" or "capture" ? 0 : 1, fixture.Platform.Diagnostics.Count);
    }

    /// <summary>诊断写入失败仍停止，并记录诊断异常而不重新输入。</summary>
    [Fact]
    public async Task DiagnosticFailureCannotResumeAutomation()
    {
        var fixture = new Fixture(Unknown());
        fixture.Platform.DiagnosticFailure = true;
        var result = await fixture.RunAsync();
        Assert.Contains("等待", result.Reason);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is not null);
    }

    /// <summary>默认空模型字段可直接显示，非取消结束使用默认空日志实现。</summary>
    [Fact]
    public async Task DefaultModelsAndOptionalProgressAndLoggerAreUsable()
    {
        Assert.Equal("", new FreePackProgress().Stage);
        Assert.Equal(0, new FreePackProgress().ScannedPacks);
        Assert.Equal(0, new FreePackProgress().OpenedPacks);
        Assert.Equal("", new FreePackRunResult().Reason);
        Assert.False(new FreePackRunResult().IsCancelled);
        var fixture = new Fixture(Detail("a"), Detail("a"));
        var result = await new FreePackAutomationService(fixture.Recognizer, fixture.Platform).RunAsync();
        Assert.Equal(1, result.ScannedPacks);
    }

    /// <summary>冻结的截图时间仍受到累计延迟预算约束，测试无需真实等待一分钟。</summary>
    [Fact]
    public async Task FrozenCaptureClockStillHasABoundedDelayBudget()
    {
        var fixture = new Fixture(Unknown());
        fixture.Platform.FreezeClock = true;
        var result = await fixture.RunAsync();
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.CaptureCount, 240, 242);
    }

    /// <summary>识别处理耗费的单调实际时间计入期限，即使截图时间不变也不得输入。</summary>
    [Fact]
    public async Task MonotonicProcessingTimeCountsTowardTheWaitLimit()
    {
        var fixture = new Fixture(Detail("a", true));
        var clock = new ManualTimeProvider();
        fixture.Platform.ChangeFrame = (_, frame) => { clock.Timestamp = TimeSpan.FromSeconds(61).Ticks; return frame; };
        var result = await new FreePackAutomationService(fixture.Recognizer, fixture.Platform, fixture.Logger, clock).RunAsync();
        Assert.Contains("60", result.Reason);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>调用方同步上下文仅负责进度转发，实际截图与识别在后台执行。</summary>
    [Fact]
    public async Task CaptureDoesNotRunOnTheCallingSynchronizationContext()
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        var original = SynchronizationContext.Current;
        var caller = new SynchronizationContext();
        Task<FreePackRunResult> run;
        try { SynchronizationContext.SetSynchronizationContext(caller); run = fixture.RunAsync(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await run;
        Assert.DoesNotContain(fixture.Platform.CaptureContexts, context => ReferenceEquals(context, caller));
    }

    /// <summary>任一必需按钮或卡图身份缺失时停止，不猜测点击坐标。</summary>
    [Theory]
    [InlineData("identity")]
    [InlineData("entry")]
    [InlineData("next")]
    [InlineData("purchase")]
    [InlineData("return-next")]
    public async Task MissingRequiredIdentityOrTargetStopsWithoutGuessing(string missing)
    {
        var detail = Detail("a", missing != "next");
        var sequence = missing switch
        {
            "identity" => new[] { detail with { Fingerprint = " " } },
            "entry" => new[] { detail with { PrimaryTarget = null } },
            "next" => new[] { detail with { NextTarget = null } },
            "purchase" => new[] { detail, Dialog() with { PrimaryTarget = null } },
            _ => new[] { detail, Dialog(), Results(), detail with { NextTarget = null } }
        };
        var fixture = new Fixture(sequence);
        var result = await fixture.RunAsync();
        Assert.NotEmpty(result.Reason);
        Assert.Single(fixture.Platform.Diagnostics);
        Assert.Equal(missing switch { "purchase" => 1, "return-next" => 3, _ => 0 }, fixture.Platform.Clicks.Count);
    }

    /// <summary>购买授权点击后已消耗，后续新免费确认框不允许再次购买。</summary>
    [Fact]
    public async Task ConsumedPurchasePermissionCannotBeUsedAgain()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Dialog() with { PrimaryTarget = new PixelPoint(51, 60) });
        var result = await fixture.RunAsync();
        Assert.Equal(3, fixture.Platform.Clicks.Count);
        Assert.Contains("阶段", result.Reason);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>同一动画按钮小幅定位抖动不代表页面变化，只发送一次跳过动作。</summary>
    [Theory]
    [InlineData(81, 80)]
    [InlineData(80, 81)]
    public async Task SmallOpeningTargetJitterDoesNotRepeatTheSkip(int x, int y)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Opening(true) with { PrimaryTarget = new PixelPoint(x, y) },
            Results(), Detail("a", true), Detail("b"), Detail("a", true));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.Opening);
    }

    /// <summary>同一开包页面中明显不同位置的按钮允许分开操作，目标暂时消失不会重发旧动作。</summary>
    [Theory]
    [InlineData(90, 80)]
    [InlineData(80, 90)]
    [InlineData(80, 80)]
    public async Task OpeningOnlyRepeatsForClearlyDifferentTargetPositions(int x, int y)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Opening(false),
            Opening(true) with { PrimaryTarget = new PixelPoint(x, y) }, Results(), Detail("a", true), Detail("b"), Detail("a", true));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(x == 80 && y == 80 ? 1 : 2, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Opening));
    }

    /// <summary>请求下一包后同一卡图的按钮消失或移动仍只等待，不重发上一包输入。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePackWaitsForIdentityChangeAfterNextButtonChanges(bool disappeared)
    {
        var changed = Detail("a") with { NextTarget = disappeared ? null : new PixelPoint(91, 50) };
        var fixture = new Fixture(Detail("a"), changed, Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
    }

    /// <summary>确认过结果后的按钮定位变化不会重复确认，只等待返回原卡包详情。</summary>
    [Fact]
    public async Task ChangedResultButtonStillWaitsForOriginalDetailsWithoutRepeatingConfirmation()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Results() with { PrimaryTarget = new PixelPoint(51, 80) },
            Detail("a", true), Detail("b"), Detail("a", true));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.Results);
    }

    /// <summary>成功、预取消和激活部分失败均结束平台会话。</summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("activate-failure")]
    public async Task EveryAcceptedRunEndsItsPlatformSession(string ending)
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        using var cancellation = new CancellationTokenSource();
        if (ending == "cancel") cancellation.Cancel();
        if (ending == "activate-failure") fixture.Platform.Failure = ("activate", new InvalidOperationException("激活部分失败"));
        await fixture.Service.RunAsync(null, cancellation.Token);
        Assert.Equal(1, fixture.Platform.EndCalls);
    }

    /// <summary>清理失败保留正常结果并释放单运行门禁，下一轮仍可执行。</summary>
    [Fact]
    public async Task CleanupFailureKeepsTheOriginalResultAndReleasesTheGate()
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.EndFailure = true;
        var result = await fixture.RunAsync();
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains(fixture.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("清理"));
        fixture.Platform.Restart();
        var next = await fixture.RunAsync();
        Assert.Contains("一轮", next.Reason);
        Assert.Equal(2, fixture.Platform.EndCalls);
    }

    /// <summary>注入时钟失败仍结束会话并释放门禁，修复时钟后可再次开始。</summary>
    [Fact]
    public async Task AClockFailureCannotLeaveTheRunGateLocked()
    {
        var fixture = new Fixture(Detail("a"), Detail("b"), Detail("a"));
        var clock = new ManualTimeProvider { ThrowNext = true };
        var service = new FreePackAutomationService(fixture.Recognizer, fixture.Platform, fixture.Logger, clock);
        var failed = await service.RunAsync();
        Assert.Contains("时钟", failed.Reason);
        Assert.Equal(1, fixture.Platform.EndCalls);
        var completed = await service.RunAsync();
        Assert.Contains("一轮", completed.Reason);
        Assert.Equal(2, fixture.Platform.EndCalls);
    }

    /// <summary>返回确认按钮输入失败不计入已开包数量。</summary>
    [Fact]
    public async Task FailedResultConfirmationDoesNotIncrementOpenedCount()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.FailClickAt = 3;
        var result = await fixture.RunAsync();
        Assert.Equal(0, result.OpenedPacks);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
    }

    /// <summary>超过四位哈希差异是新卡包，非法十六位标识继续按原文匹配。</summary>
    [Theory]
    [InlineData("0000000000000000", "000000000000001f")]
    [InlineData("gggggggggggggggg", "0000000000000000")]
    [InlineData("0000000000000000", "gggggggggggggggg")]
    [InlineData("0000000000000000", "short")]
    public async Task DistinctHashesAndNonHexFingerprintsDoNotCollapseDifferentPacks(string first, string second)
    {
        var fixture = new Fixture(Detail(first), Detail(second), Detail(first));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
    }

    /// <summary>构造已识别详情，付费按钮仍存在但仅免费入口允许购买。</summary>
    private static PackObservation Detail(string fingerprint, bool free = false) => new(PackScreen.PackDetails, new PixelPoint(50, 50), new PixelPoint(90, 50), free, fingerprint, .98);
    /// <summary>构造已验证免费文字和购买按钮的确认框。</summary>
    private static PackObservation Dialog() => new(PackScreen.FreePurchaseDialog, new PixelPoint(50, 60), null, true, "", .99);
    /// <summary>构造动画等待或具有跳过目标的开包画面。</summary>
    private static PackObservation Opening(bool target) => new(PackScreen.Opening, target ? new PixelPoint(80, 80) : null, null, false, "", .99);
    /// <summary>构造可以确认返回的卡包结果画面。</summary>
    private static PackObservation Results() => new(PackScreen.Results, new PixelPoint(50, 80), null, false, "", .99);
    /// <summary>构造保守未知画面。</summary>
    private static PackObservation Unknown() => new(PackScreen.Unknown, null, null, false, "", 0);

    /// <summary>组装不会访问任何真实进程、文件或注册表的状态机边界。</summary>
    private sealed class Fixture
    {
        /// <summary>可控制时间和截图序列的虚构游戏边界。</summary>
        public FakePlatform Platform { get; }
        /// <summary>按帧索引返回录制观察的识别边界。</summary>
        public FakeRecognizer Recognizer { get; }
        /// <summary>同步收集的进度快照。</summary>
        public RecordingProgress Progress { get; } = new();
        /// <summary>记录实际等级和异常的日志边界。</summary>
        public RecordingLogger Logger { get; } = new();
        /// <summary>正在测试的真实状态机。</summary>
        public FreePackAutomationService Service { get; }
        /// <summary>每个观察重复两帧，以模拟真实稳定截图。</summary>
        public Fixture(params PackObservation[] sequence)
        {
            Platform = new FakePlatform(sequence.SelectMany(item => new[] { item, item }).ToArray());
            Recognizer = new FakeRecognizer(Platform);
            Service = new FreePackAutomationService(Recognizer, Platform, Logger);
        }
        /// <summary>通过记录器运行一次隔离状态机。</summary>
        public Task<FreePackRunResult> RunAsync() => Service.RunAsync(Progress);
    }

    /// <summary>用帧内四字节索引驱动观察序列，所有点击只保存在内存。</summary>
    private sealed class FakePlatform : IGameAutomationPlatform
    {
        /// <summary>虚构捕获的起始时间，仅用于有限等待测试。</summary>
        private DateTimeOffset clock = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        /// <summary>按捕获顺序返回的观察序列。</summary>
        public PackObservation[] Observations { get; set; }
        /// <summary>序列结束后可提供不同的等待画面。</summary>
        public Func<int, PackObservation>? Tail { get; set; }
        /// <summary>可模拟句柄或几何变化的帧转换。</summary>
        public Func<int, GameFrame, GameFrame>? ChangeFrame { get; set; }
        /// <summary>允许模拟系统边界失败的操作和异常。</summary>
        public (string Operation, Exception Exception)? Failure { get; set; }
        /// <summary>捕获指定帧数后模拟 F8 停止。</summary>
        public int StopAfterCaptures { get; set; } = int.MaxValue;
        /// <summary>模拟等待期间的令牌取消。</summary>
        public bool CancelDelay { get; set; }
        /// <summary>可暂停首轮等待以验证服务并发门禁。</summary>
        public Func<Task>? DelayHook { get; set; }
        /// <summary>冻结截图时间，单独验证累计延迟期限。</summary>
        public bool FreezeClock { get; set; }
        /// <summary>指定第几次点击抛异常，测试失败结果不被计数。</summary>
        public int FailClickAt { get; set; } = int.MaxValue;
        /// <summary>模拟结束输入会话的清理失败。</summary>
        public bool EndFailure { get; set; }
        /// <summary>已执行的会话结束次数。</summary>
        public int EndCalls { get; private set; }
        /// <summary>模拟诊断文件保存异常。</summary>
        public bool DiagnosticFailure { get; set; }
        /// <summary>已执行激活次数。</summary>
        public int Activations { get; private set; }
        /// <summary>已执行捕获次数。</summary>
        public int CaptureCount { get; private set; }
        /// <summary>只存在内存中的页面与点击坐标。</summary>
        public List<(PackScreen Screen, PixelPoint Point)> Clicks { get; } = [];
        /// <summary>只存在内存中的诊断截图和原因。</summary>
        public List<(GameFrame Frame, string Reason)> Diagnostics { get; } = [];
        /// <summary>每次捕获时的同步上下文，用于确认截图在后台运行。</summary>
        public List<SynchronizationContext?> CaptureContexts { get; } = [];
        /// <summary>模拟 F8 停止键状态。</summary>
        public bool IsStopRequested => CaptureCount >= StopAfterCaptures;
        /// <summary>保存虚构观察序列。</summary>
        public FakePlatform(PackObservation[] observations) => Observations = observations;
        /// <summary>记录激活而不寻找或操作任何窗口。</summary>
        public void ActivateGame() { Fail("activate"); Activations++; }
        /// <summary>创建包含识别索引的虚构帧。</summary>
        public GameFrame Capture()
        {
            Fail("capture");
            CaptureContexts.Add(SynchronizationContext.Current);
            var frame = new GameFrame(17, 100, 100, 10, 10, BitConverter.GetBytes(CaptureCount), clock);
            CaptureCount++;
            return ChangeFrame?.Invoke(CaptureCount, frame) ?? frame;
        }
        /// <summary>从帧索引读取录制观察，不解析任何真实像素。</summary>
        public PackObservation Observation(GameFrame frame)
        {
            var index = BitConverter.ToInt32(frame.Pixels);
            return index < Observations.Length ? Observations[index] : Tail?.Invoke(index) ?? Observations[^1];
        }
        /// <summary>记录点击意图，不调用任何输入 API。</summary>
        public void Click(GameFrame frame, PixelPoint point)
        {
            Fail("click");
            if (Clicks.Count + 1 == FailClickAt) throw new InvalidOperationException("测试确认结果输入失败");
            Clicks.Add((Observation(frame).Screen, point));
        }
        /// <summary>推进虚构时间而不让测试真实等待一分钟。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Fail("delay");
            if (CancelDelay) throw new OperationCanceledException();
            cancellationToken.ThrowIfCancellationRequested();
            if (!FreezeClock) clock += TimeSpan.FromSeconds(1);
            return DelayHook?.Invoke() ?? Task.CompletedTask;
        }
        /// <summary>记录诊断请求或模拟写入失败，不写入用户目录。</summary>
        public void SaveDiagnostic(GameFrame frame, string reason)
        {
            if (DiagnosticFailure) throw new IOException("测试诊断不可写");
            Diagnostics.Add((frame, reason));
        }
        /// <summary>仅在指定边界操作抛出测试异常。</summary>
        private void Fail(string operation) { if (Failure is { } failure && failure.Operation == operation) throw failure.Exception; }
        /// <summary>重置录制序列，仅用于验证服务门禁释放后的第二次运行。</summary>
        public void Restart() => CaptureCount = 0;
        /// <summary>记录结束会话，即使异常也不触碰真实热键。</summary>
        public void EndAutomation()
        {
            EndCalls++;
            if (EndFailure) throw new IOException("测试会话清理失败");
        }
    }

    /// <summary>提供无真实等待的单调处理时间。</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>测试控制的单调时钟刻度。</summary>
        public long Timestamp { get; set; }
        /// <summary>下一次读取时模拟时钟边界异常。</summary>
        public bool ThrowNext { get; set; }
        /// <summary>每秒对应时间刻度，便于表达处理延迟。</summary>
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <summary>返回当前测试时间刻度。</summary>
        public override long GetTimestamp()
        {
            if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("测试时钟暂不可用"); }
            return Timestamp;
        }
    }

    /// <summary>将隔离帧索引转换为录制观察的识别器。</summary>
    private sealed class FakeRecognizer(FakePlatform platform) : IPackRecognizer
    {
        /// <summary>可模拟识别过程中的异常。</summary>
        public Exception? Failure { get; set; }
        /// <summary>返回序列观察或抛出配置的测试异常。</summary>
        public PackObservation Recognize(GameFrame frame) => Failure is null ? platform.Observation(frame) : throw Failure;
    }

    /// <summary>同步保存状态机进度，避免测试依赖线程调度。</summary>
    private sealed class RecordingProgress : IProgress<FreePackProgress>
    {
        /// <summary>按报告顺序保存的阶段与计数。</summary>
        public List<FreePackProgress> Values { get; } = [];
        /// <summary>记录一个完整进度快照。</summary>
        public void Report(FreePackProgress value) => Values.Add(value);
    }

    /// <summary>验证状态机日志不需要真实持久化依赖。</summary>
    private sealed class RecordingLogger : ILogger<FreePackAutomationService>
    {
        /// <summary>各日志等级、格式化正文和实际异常。</summary>
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        /// <summary>测试不创建日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>记录所有日志等级。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存格式化正文和原异常。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
