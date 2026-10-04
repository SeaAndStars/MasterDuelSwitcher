using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>以录制观察序列和虚构窗口输入验证免费开包，不启动或操作真实游戏。</summary>
public sealed class FreePackAutomationTests
{
    /// <summary>逐次关联输入发起、完成和后续观察，验证诊断不会改变原免费流程。</summary>
    [Fact]
    public async Task ActionDiagnosticsCorrelateSuccessfulInputsAndFollowingFrames()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Results(), Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        var requested = fixture.Logger.Records.Where(record => Template(record).StartsWith("FreePackActionRequested", StringComparison.Ordinal)).ToArray();
        var completed = fixture.Logger.Records.Where(record => Template(record).StartsWith("FreePackActionCompleted", StringComparison.Ordinal)).ToArray();
        Assert.Equal(fixture.Platform.Clicks.Count, requested.Length);
        Assert.Equal(requested.Length, completed.Length);
        string runId = Assert.IsType<string>(requested[0]["RunId"]);
        Assert.Equal(32, runId.Length);
        for (int index = 0; index < requested.Length; index++)
        {
            Assert.Equal(runId, requested[index]["RunId"]);
            Assert.Equal((long)index + 1, requested[index]["ActionSequence"]);
            Assert.Equal(requested[index]["ActionSequence"], completed[index]["ActionSequence"]);
            Assert.True(Assert.IsType<double>(completed[index]["InputMilliseconds"]) >= 0);
            Assert.Equal(fixture.Platform.Clicks[index].Screen, requested[index]["Screen"]);
        }
        Assert.Null(requested[0]["SincePreviousCompletionMilliseconds"]);
        Assert.True(Assert.IsType<double>(requested[1]["SincePreviousCompletionMilliseconds"]) >= 0);
        Assert.Contains(fixture.Logger.Records, record => Template(record).StartsWith("FreePackObservation", StringComparison.Ordinal)
            && Equals(record["Phase"], "AwaitReturn") && Equals(record["Screen"], PackScreen.PackDetails)
            && Assert.IsType<long>(record["LastSuccessfulActionSequence"]) > 0);
        Assert.Contains(fixture.Logger.Records, record => Template(record).StartsWith("FreePackStabilityResult", StringComparison.Ordinal) && Equals(record["Stable"], true));
    }

    /// <summary>输入边界抛出异常时保留原结束结果，并明确区分发起与成功完成。</summary>
    [Fact]
    public async Task ActionDiagnosticsDoNotMarkFailedInputAsCompleted()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true), Results());
        fixture.Platform.FailClickAt = 3;
        var result = await fixture.RunAsync();
        Assert.Contains("输入失败", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Equal(3, fixture.Logger.Records.Count(record => Template(record).StartsWith("FreePackActionRequested", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.Logger.Records.Count(record => Template(record).StartsWith("FreePackActionCompleted", StringComparison.Ordinal)));
        var failed = Assert.Single(fixture.Logger.Records, record => Template(record).StartsWith("FreePackActionFailed", StringComparison.Ordinal));
        Assert.Equal(3L, failed["ActionSequence"]);
        Assert.Equal("Opening", failed["Phase"]);
        Assert.Equal(0, result.OpenedPacks);
    }

    /// <summary>结果页持续不变时日志解释退避和三十次上限，且诊断不会增加点击。</summary>
    [Fact]
    public async Task ResultRetryDiagnosticsExplainBackoffAndAttemptLimit()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Contains("60 秒", result.Reason);
        Assert.Equal(30, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results));
        var gates = fixture.Logger.Records.Where(record => Template(record).StartsWith("FreePackResultRetryBlocked", StringComparison.Ordinal)).ToArray();
        Assert.Contains(gates, record => Equals(record["Reason"], "Backoff") && Equals(record["RequiredIntervalMilliseconds"], 200.0));
        Assert.Contains(gates, record => Equals(record["Reason"], "AttemptLimit") && Equals(record["Attempts"], 30));
        Assert.Equal(30, fixture.Logger.Records.Count(record => Template(record).StartsWith("FreePackResultConfirmationRequested", StringComparison.Ordinal)));
        Assert.Equal(0, result.OpenedPacks);
    }

    /// <summary>从结构化记录读取事件模板，避免测试依赖中文数字格式。</summary>
    private static string Template(IReadOnlyDictionary<string, object?> record) => Assert.IsType<string>(record["{OriginalFormat}"]);

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

    /// <summary>免费入口成功后原包详情持续多帧且导航目标抖动时等待弹窗，只购买一次并正常完成计数。</summary>
    [Fact]
    public async Task SamePackDetailsAfterFreeEntryWaitsForDialogAndCompletesOnce()
    {
        var detail = TitledDetail("猛火魔兽", "a", true);
        var transition = detail with { NextTarget = new PixelPoint(91, 50) };
        var fixture = new Fixture(detail, transition, transition, transition, Dialog(), Opening(true), Results(),
            detail with { FreeOffer = false }, TitledDetail("其他卡包", "b"), detail);
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.False(result.IsCancelled);
        Assert.Equal(new[] { PackScreen.PackDetails, PackScreen.FreePurchaseDialog, PackScreen.Opening,
            PackScreen.Results, PackScreen.PackDetails, PackScreen.PackDetails }, fixture.Platform.Clicks.Select(click => click.Screen));
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Point == transition.NextTarget);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.Results);
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>免费入口后原包详情持续未弹出确认框时遵守六十秒期限，不重复入口或提前计数。</summary>
    [Fact]
    public async Task SamePackDetailsAfterFreeEntryRetainsSixtySecondDeadline()
    {
        var detail = TitledDetail("猛火魔兽", "a", true);
        var fixture = new Fixture(detail, detail with { NextTarget = new PixelPoint(91, 50) });
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Contains("60", result.Reason);
        Assert.False(result.IsCancelled);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 60.2);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>免费入口的同包详情过渡后出现付费购买框时停止，诊断确实停在付费框且不购买。</summary>
    [Fact]
    public async Task PaidDialogAfterFreeEntryTransitionStopsBeforePurchase()
    {
        var detail = TitledDetail("猛火魔兽", "a", true);
        var paid = new PackObservation(PackScreen.UnverifiedPurchaseDialog, new PixelPoint(50, 60), null, false, "", .99);
        var transition = detail with { NextTarget = new PixelPoint(91, 50) };
        var fixture = new Fixture(detail, transition, transition, paid);
        var result = await fixture.RunAsync();
        Assert.Contains("停止", result.Reason);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        var diagnostic = Assert.Single(fixture.Platform.Diagnostics);
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, fixture.Recognizer.Recognize(diagnostic.Frame).Screen);
    }

    /// <summary>免费入口的原包过渡后标题变为其他卡包时停止，同哈希也不允许继续等待或购买。</summary>
    [Fact]
    public async Task DifferentPackAfterFreeEntryTransitionStopsBeforePurchase()
    {
        var detail = TitledDetail("猛火魔兽", "a", true);
        var changed = detail with { PackTitle = "其他卡包", NextTarget = new PixelPoint(91, 50) };
        var transition = detail with { NextTarget = new PixelPoint(91, 50) };
        var fixture = new Fixture(detail, transition, transition, changed, Dialog(), Results());
        var result = await fixture.RunAsync();
        Assert.Contains("停止", result.Reason);
        Assert.Equal(1, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        var diagnostic = Assert.Single(fixture.Platform.Diagnostics);
        Assert.Equal(changed.PackTitle, fixture.Recognizer.Recognize(diagnostic.Frame).PackTitle);
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

    /// <summary>轮次身份精确比较图像哈希，少于四位差异也不得误认成首包。</summary>
    [Fact]
    public async Task SmallHashChangesStillCompleteTheSameCarouselCycle()
    {
        var fixture = new Fixture(Detail("0000000000000000"), Detail("ffffffffffffffff"), Detail("000000000000000f"), Detail("0000000000000000"));
        var result = await fixture.RunAsync();
        Assert.Equal(3, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(3, fixture.Platform.Clicks.Count);
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
        Assert.Equal(0, result.OpenedPacks);
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
        Assert.InRange(fixture.Platform.CaptureCount, 750, 752);
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

    /// <summary>结果目标变化后须重新双帧确认，合法重试后仍只在原包详情计数一次。</summary>
    [Fact]
    public async Task ChangedResultButtonStillWaitsForOriginalDetailsWithoutRepeatingConfirmation()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Results() with { PrimaryTarget = new PixelPoint(51, 80) },
            Results() with { PrimaryTarget = new PixelPoint(51, 80) },
            Detail("a", true), Detail("b"), Detail("a", true));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results));
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

    /// <summary>二十个真实标题即使旧哈希全部碰撞，也扫描到首标题再次出现才完成。</summary>
    [Fact]
    public async Task MoreThanElevenDistinctTitlesWithCollidingHashesCompleteOnlyAtFirstTitle()
    {
        var packs = Enumerable.Range(0, 20).Select(index => TitledDetail("卡包" + index, "0000000000000000")).ToArray();
        var fixture = new Fixture(packs.Concat([packs[0]]).ToArray());
        var result = await fixture.RunAsync();
        Assert.Equal(20, result.ScannedPacks);
        Assert.Equal(20, fixture.Platform.Clicks.Count);
        Assert.Contains("一轮", result.Reason);
        Assert.Contains(fixture.Logger.Entries, item => item.Message.Contains("首次") && item.Message.Contains("卡包0"));
        Assert.Contains(fixture.Logger.Entries, item => item.Message.Contains("当前") && item.Message.Contains("卡包19"));
        Assert.Contains(fixture.Logger.Entries, item => item.Message.Contains("重复") && item.Message.Contains("卡包0"));
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>非首历史标题再次出现视为异常导航，保留已扫描数量而不宣称完成一轮。</summary>
    [Fact]
    public async Task RepeatedNonFirstTitleStopsWithDiagnosticInsteadOfClaimingCompletion()
    {
        var fixture = new Fixture(TitledDetail("首包", "a"), TitledDetail("次包", "b"), TitledDetail("三包", "c"), TitledDetail("次包", "d"));
        var result = await fixture.RunAsync();
        Assert.Equal(3, result.ScannedPacks);
        Assert.DoesNotContain("完成", result.Reason);
        Assert.Contains("非首", result.Reason);
        Assert.Equal(3, fixture.Platform.Clicks.Count);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>标题已有OCR证据时，第二帧缺失或不同标题不得以相似旧哈希维持免费授权。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("其他包")]
    public async Task TitleStabilityCannotFallBackToAnOldHash(string changedTitle)
    {
        var first = TitledDetail("首包", "0000000000000000", true);
        var fixture = new Fixture(first);
        fixture.Platform.Observations = [first, first with { PackTitle = changedTitle }];
        fixture.Platform.StopAfterCaptures = 3;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>真实标题可以独立识别卡包，原图哈希为空仍可精确闭合轮次。</summary>
    [Fact]
    public async Task TitlesCanIdentifyAFullCycleWithoutImageHashes()
    {
        var fixture = new Fixture(TitledDetail("首包", ""), TitledDetail("次包", ""), TitledDetail("首包", ""));
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
    }

    /// <summary>恢复覆盖入口前、免费确认、购买后动画、结果返回与下一包阶段，已成功购买不重发。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task TemporaryCaptureFailurePreservesEveryPhaseAndSuccessfulPurchase(int capture)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.TemporaryCaptureAt.Add(capture);
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
        Assert.Equal(1, fixture.Platform.RecoverCalls);
        Assert.True(fixture.Platform.RecoverElapsed.Single() >= TimeSpan.FromSeconds(3));
        Assert.Contains(fixture.Progress.Values, progress => progress.Stage.Contains("恢复"));
    }

    /// <summary>临时输入异常发生在免费入口提交前时，不提前记已访问，恢复后继续原包扫描。</summary>
    [Fact]
    public async Task TemporaryEntryClickFailureDoesNotCommitAnIncompleteVisitedPack()
    {
        var fixture = new Fixture(Detail("a", true), Detail("a", true), Dialog(), Results(), Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.TemporaryClickAt = 1;
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
    }

    /// <summary>免费确认输入在系统核验前失焦，恢复后双帧验证当前免费框并仅成功购买一次。</summary>
    [Fact]
    public async Task TemporaryPurchaseClickFailureRevalidatesBeforeItsOnlySuccessfulPurchase()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Dialog(), Results(), Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.TemporaryClickAt = 2;
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
        Assert.Equal(1, fixture.Platform.RecoverCalls);
    }

    /// <summary>双帧中途失焦使旧候选失效，恢复后至少重新捕获两帧才发送任何点击。</summary>
    [Fact]
    public async Task RecoveryDiscardsTheOldCandidateAndRequiresTwoFreshFrames()
    {
        var fixture = new Fixture(Detail("a", true), Detail("a", true));
        fixture.Platform.TemporaryCaptureAt.Add(2);
        fixture.Platform.StopAfterCaptures = 4;
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Single(fixture.Platform.Clicks);
        Assert.Equal(2, fixture.Platform.ClickFrameIndices.Single());
    }

    /// <summary>恢复等待期间F8或令牌取消直接结束，三秒暂停内无恢复与输入。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryPauseImmediatelyRespondsToStopOrCancellation(bool f8)
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.TemporaryCaptureAt.Add(1);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterDelays = f8 ? 1 : int.MaxValue;
        fixture.Platform.CancelDelay = !f8;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.Equal(0, fixture.Platform.RecoverCalls);
    }

    /// <summary>原窗口持续恢复失败到六十秒即停止，不延长恢复期限或发送后台点击。</summary>
    [Fact]
    public async Task RecoveryHasOneSixtySecondDeadline()
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.TemporaryCaptureAt.Add(1);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.RecoverSucceeds = false;
        var result = await fixture.RunAsync();
        Assert.Contains("恢复", result.Reason);
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 60.2);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.True(fixture.Platform.RecoverCalls > 1);
    }

    /// <summary>恢复识别到永久PID或窗口几何错误时直接停止，已有购买授权与点击均不重放。</summary>
    [Fact]
    public async Task PermanentIdentityFailureDuringRecoveryStopsWithoutReplay()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.TemporaryCaptureAt.Add(5);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.RecoverFailure = new InvalidOperationException("原PID已变化");
        var result = await fixture.RunAsync();
        Assert.Contains("PID", result.Reason);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Equal(1, fixture.Platform.RecoverCalls);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>较长恢复停顿从当前阶段等待期限扣除，成功恢复后的原页面仍有剩余观察时间。</summary>
    [Fact]
    public async Task RecoveryTimeDoesNotImmediatelyExpireThePreservedPhase()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Results(), Detail("a"), Detail("b"), Detail("a"));
        var clock = new ManualTimeProvider();
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.TemporaryCaptureAt.Add(6);
        fixture.Platform.RecoverFailuresRemaining = 700;
        fixture.Platform.DelayObserver = delay => clock.Timestamp += delay.Ticks;
        fixture.Platform.ChangeFrame = (count, frame) => { if (count == 5) clock.Timestamp += TimeSpan.FromSeconds(10).Ticks; return frame; };
        var result = await new FreePackAutomationService(fixture.Recognizer, fixture.Platform, fixture.Logger, clock).RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.True(fixture.Platform.Elapsed >= TimeSpan.FromSeconds(59));
    }

    /// <summary>结果页连续确认按指数退避进行，每次均重新验证双帧，返回原包只计一次。</summary>
    [Fact]
    public async Task ResultConfirmationRetriesExponentiallyAndCountsOnlyAfterReturning()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.Tail = _ => fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results) >= 7
            ? Detail(fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.PackDetails) == 1 ? "a" : "b") : Results();
        fixture.Platform.StopAfterCaptures = 600;
        var result = await fixture.RunAsync();
        var resultTimes = fixture.Platform.ClickTimes.Where((_, index) => fixture.Platform.Clicks[index].Screen == PackScreen.Results).ToArray();
        Assert.Equal(7, resultTimes.Length);
        var minimums = new[] { .2, .4, .6, .6, .6, .6 };
        for (var index = 0; index < minimums.Length; index++)
            Assert.InRange((resultTimes[index + 1] - resultTimes[index]).TotalSeconds, minimums[index], minimums[index] + .16);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
    }

    /// <summary>一直停留结果页最多确认三十次，重试不延长六十秒总期限且不提前累计开包。</summary>
    [Fact]
    public async Task ResultRetriesAreBoundedAndNeverResetTheTotalDeadline()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(30, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results));
        Assert.Equal(0, result.OpenedPacks);
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 61);
    }

    /// <summary>结果按钮第十一次才生效时继续逐次验证，返回原包只累计一次开包。</summary>
    [Fact]
    public async Task ResultConfirmationCanReturnAfterMoreThanTenValidatedAttempts()
    {
        const int confirmationsRequired = 11;
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.Tail = _ => fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results) < confirmationsRequired
            ? Results() : Detail(fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.PackDetails) == 2 ? "b" : "a");
        var result = await fixture.RunAsync();
        Assert.Equal(confirmationsRequired, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.Results));
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.False(result.IsCancelled);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
        var frames = fixture.Platform.ClickFrameIndices.Where((_, index) => fixture.Platform.Clicks[index].Screen == PackScreen.Results).ToArray();
        for (int index = 1; index < frames.Length; index++)
            Assert.True(frames[index] >= frames[index - 1] + 2);
    }

    /// <summary>不稳定结果按钮导致本次重试放弃，不复用前次已验证坐标。</summary>
    [Fact]
    public async Task ResultRetryNeedsANewStableTarget()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.Tail = index => Results() with { PrimaryTarget = new PixelPoint(index % 2 == 0 ? 50 : 51, 80) };
        fixture.Platform.StopAfterCaptures = 30;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.Results);
        Assert.Equal(0, result.OpenedPacks);
    }

    /// <summary>已双帧识别Skip并购买后，按钮隐藏的Opening与Unknown允许节流点击旧Skip目标。</summary>
    [Theory]
    [InlineData(PackScreen.Opening)]
    [InlineData(PackScreen.Unknown)]
    public async Task AValidatedSkipCanBeClickedWhileHiddenAfterPurchase(PackScreen hidden)
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.Tail = _ => fixture.Platform.Clicks.Count >= 6 ? Results()
            : hidden == PackScreen.Unknown ? Unknown() : Opening(false);
        fixture.Platform.StopAfterCaptures = 100;
        var result = await fixture.RunAsync();
        var skips = fixture.Platform.Clicks.Where(click => click.Point == new PixelPoint(80, 80)).ToArray();
        Assert.True(skips.Length >= 4);
        Assert.Contains(fixture.Platform.Clicks, click => click.Screen == hidden && click.Point == new PixelPoint(80, 80));
        Assert.Equal(0, result.OpenedPacks);
        Assert.True(result.IsCancelled);
    }

    /// <summary>无免费购买、无已验证Skip坐标或进入详情/结果页时均不发送旧Skip点击。</summary>
    [Theory]
    [InlineData("unbought")]
    [InlineData("no-skip")]
    [InlineData("details")]
    [InlineData("results")]
    public async Task HiddenSkipPermissionIsLimitedToThePurchasedOpening(string scene)
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var sequence = scene switch
        {
            "unbought" => new[] { Unknown() with { AnimationSkipTarget = new PixelPoint(80, 80) } },
            "no-skip" => new[] { Detail("a", true), Dialog(), Opening(false), Unknown() },
            "details" => new[] { Detail("a", true), Dialog(), skip, Detail("a") },
            _ => new[] { Detail("a", true), Dialog(), skip, Results() }
        };
        var fixture = new Fixture(sequence);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterCaptures = 30;
        await fixture.RunAsync();
        Assert.Equal(scene is "details" or "results" ? 1 : 0,
            fixture.Platform.Clicks.Count(click => click.Point == new PixelPoint(80, 80)));
    }

    /// <summary>隐形Skip仍复验客户区几何，变化后结束并禁止沿用旧坐标。</summary>
    [Fact]
    public async Task HiddenSkipRefusesAnAlteredWindowGeometry()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) }, Unknown());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.ChangeFrame = (count, frame) => count >= 7 ? frame with { ScreenX = 11 } : frame;
        var result = await fixture.RunAsync();
        Assert.Contains("窗口", result.Reason);
        Assert.Single(fixture.Platform.Clicks, click => click.Point == new PixelPoint(80, 80));
    }

    /// <summary>隐形Skip输入有最小节流及上限，不重置整体等待预算，也响应紧急停止。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenSkipRepeatsRemainThrottledAndBounded(bool stop)
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) }, Unknown());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterCaptures = stop ? 50 : int.MaxValue;
        var result = await fixture.RunAsync();
        Assert.Equal(stop, result.IsCancelled);
        if (!stop) Assert.Contains("60", result.Reason);
        var times = fixture.Platform.ClickTimes.Where((_, index) => fixture.Platform.Clicks[index].Point == new PixelPoint(80, 80)).ToArray();
        Assert.True(times.Length > 2);
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(160)));
        Assert.True(times.Length < 400);
    }

    /// <summary>已点击Open仍暂时显示原按钮时，使用本包已验证Skip目标而不持续等待原动作锁。</summary>
    [Fact]
    public async Task VerifiedSkipIsUsedWhileThePreviouslyClickedOpenRemainsVisible()
    {
        var opening = Opening(true) with { PrimaryTarget = new PixelPoint(50, 90), AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), opening, opening, opening, Results(), Results(), Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Point == new PixelPoint(50, 90));
        Assert.Contains(fixture.Platform.Clicks, click => click.Point == new PixelPoint(80, 80));
    }

    /// <summary>隐藏动画曾复用Skip点击后，重新出现且抖动的Primary不会解引用旧观察的空目标。</summary>
    [Fact]
    public async Task ReappearingOpeningTargetAfterHiddenSkipKeepsTheRunAlive()
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var reappearing = skip with { PrimaryTarget = new PixelPoint(81, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip, Opening(false), Opening(false), reappearing, Results(), Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Contains(fixture.Platform.Clicks, click => click.Screen == PackScreen.Opening && click.Point == new PixelPoint(80, 80));
    }

    /// <summary>同包详情已核验并计数后下一包输入暂时失焦，恢复原详情仅重试导航且不重复计数。</summary>
    [Fact]
    public async Task TemporaryNextClickAfterReturnedDetailsDoesNotCountThePackTwice()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Detail("a"), Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.TemporaryClickAt = 4;
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, fixture.Platform.RecoverCalls);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
    }

    /// <summary>恢复调用自身耗时达到截止线时，即使最终声称前台成功也在六十秒边界停止。</summary>
    [Fact]
    public async Task SuccessfulRecoveryCannotOverrunItsOwnMonotonicDeadline()
    {
        var fixture = new Fixture(Detail("a", true));
        var clock = new ManualTimeProvider();
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.TemporaryCaptureAt.Add(1);
        fixture.Platform.DelayObserver = delay => clock.Timestamp += delay.Ticks;
        fixture.Platform.RecoverObserver = () => clock.Timestamp += TimeSpan.FromSeconds(61).Ticks;
        var result = await new FreePackAutomationService(fixture.Recognizer, fixture.Platform, fixture.Logger, clock).RunAsync();
        Assert.Contains("恢复", result.Reason);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>请求下一包后短暂出现不同卡包的单帧不提交新阶段，原包稳定重现仍等待实际导航。</summary>
    [Fact]
    public async Task ATransientNextPackFrameCannotTurnTheOriginalDetailsIntoACompletedCycle()
    {
        var first = TitledDetail("首包", "a");
        var second = TitledDetail("次包", "b");
        var jitteredFirst = first with { NextTarget = new PixelPoint(91, 50) };
        var fixture = new Fixture(first);
        fixture.Platform.Observations = [first, first, second, first, jitteredFirst, jitteredFirst, second, second, first, first];
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.Contains("一轮", result.Reason);
    }

    /// <summary>每次激活声称成功但截图仍连续失焦时，共享同一恢复期限并在六十秒停止。</summary>
    [Fact]
    public async Task RepeatedSuccessfulActivationWithoutFreshFramesCannotRestartTheRecoveryDeadline()
    {
        var fixture = new Fixture(Detail("a", true));
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.AlwaysTemporaryCaptureFailure = true;
        fixture.Platform.StopAfterDelays = 1000;
        var result = await fixture.RunAsync();
        Assert.False(result.IsCancelled);
        Assert.Contains("恢复", result.Reason);
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 60.2);
        Assert.Empty(fixture.Platform.Clicks);
        Assert.True(fixture.Platform.RecoverCalls > 1);
    }

    /// <summary>原窗口新双帧已恢复后再次失焦开启独立恢复预算，不被上一次五十九秒耗时挤占。</summary>
    [Fact]
    public async Task ARecoveredPairAllowsANewPhaseItsOwnRecoveryDeadline()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results(), Detail("a"), Detail("b"), Detail("a"));
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.RecoverFailuresRemaining = 700;
        fixture.Platform.TemporaryCaptureAt.UnionWith([1, 3]);
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.True(fixture.Platform.Elapsed >= TimeSpan.FromSeconds(62));
    }

    /// <summary>第一个真实OCR卡包日志已包含首包自身标题，不依赖后续出现第二包才补全。</summary>
    [Fact]
    public async Task TheFirstInspectedPackLogsItsOwnFirstTitleImmediately()
    {
        var fixture = new Fixture(TitledDetail("首包", "a"));
        fixture.Platform.StopAfterCaptures = 4;
        await fixture.RunAsync();
        var firstIdentityLog = fixture.Logger.Entries.First(entry => entry.Message.Contains("卡包当前标题"));
        Assert.Contains("首次标题 首包", firstIdentityLog.Message);
    }

    /// <summary>可见Skip目标双字段同时轻微抖动仍属于Skip重试，不把旧缓存坐标误当新Open而续期。</summary>
    [Fact]
    public async Task VisibleSkipJitterCannotResetTheOpeningDeadline()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) });
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterDelays = 1000;
        fixture.Platform.Tail = index =>
        {
            var point = new PixelPoint(index / 4 % 2 == 0 ? 80 : 81, 80);
            return Opening(true) with { PrimaryTarget = point, AnimationSkipTarget = point };
        };
        var result = await fixture.RunAsync();
        Assert.False(result.IsCancelled);
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 61);
        Assert.Equal(0, result.OpenedPacks);
        Assert.True(fixture.Platform.Clicks.Count < 400);
    }

    /// <summary>购买确认或返回详情的第二帧目标变动时不提交原动作，已确认结果也不提前计数。</summary>
    [Theory]
    [InlineData("purchase")]
    [InlineData("return")]
    public async Task UnstablePurchaseAndReturnedDetailsRetainTheirPendingPhase(string phase)
    {
        var detail = Detail("a", true);
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = phase == "purchase"
            ? [detail, detail, Dialog(), Dialog() with { PrimaryTarget = new PixelPoint(51, 60) }]
            : [detail, detail, Dialog(), Dialog(), Results(), Results(), Detail("a"), Detail("a") with { NextTarget = new PixelPoint(91, 50) }];
        fixture.Platform.StopAfterCaptures = phase == "purchase" ? 5 : 9;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Equal(phase == "purchase" ? 1 : 3, fixture.Platform.Clicks.Count);
    }

    /// <summary>结果确认目标暂时缺失只等待，目标重新稳定后才确认并核验原包返回。</summary>
    [Fact]
    public async Task AResultWithNoConfirmationTargetWaitsForAValidatedButton()
    {
        var fixture = new Fixture(Detail("a", true), Dialog(), Results() with { PrimaryTarget = null }, Results(), Detail("a"), Detail("b"), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.Results);
    }

    /// <summary>免费入口后在购买确认前出现变化的原包详情不会被当作已购买的过渡页继续处理。</summary>
    [Fact]
    public async Task ChangedSamePackDetailsBeforePurchaseCannotPretendToBeThePurchasedTransition()
    {
        var fixture = new Fixture(Detail("a", true), Detail("a"));
        var result = await fixture.RunAsync();
        Assert.Contains("阶段", result.Reason);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Single(fixture.Platform.Clicks);
        Assert.Single(fixture.Platform.Diagnostics);
    }

    /// <summary>只有已验证动画Skip字段而没有Primary的开包页仍须双帧确认，之后复用原Skip并响应停止。</summary>
    [Fact]
    public async Task AVerifiedAnimationSkipTargetDoesNotRequireAPrimaryTarget()
    {
        var skip = Opening(false) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip, skip);
        fixture.Platform.StopAfterCaptures = 7;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Single(fixture.Platform.Clicks, click => click.Point == new PixelPoint(80, 80));
    }

    /// <summary>第二帧Skip专用目标移动、消失或首次出现时不使用第一帧动作坐标。</summary>
    [Theory]
    [InlineData("different")]
    [InlineData("missing")]
    [InlineData("appeared")]
    public async Task AnimationSkipTargetMustBeStableAcrossBothFrames(string change)
    {
        var detail = Detail("a", true);
        var first = Opening(true) with { AnimationSkipTarget = change == "appeared" ? null : new PixelPoint(80, 80) };
        var second = Opening(true) with { AnimationSkipTarget = change == "missing" ? null : new PixelPoint(change == "different" ? 81 : 80, 80) };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, detail, Dialog(), Dialog(), first, second];
        fixture.Platform.StopAfterCaptures = 7;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Screen == PackScreen.Opening);
    }

    /// <summary>隐藏Skip时Open与推断Skip同步抖动仍按Skip节流，推断短时缺失只复用已有缓存且不续期。</summary>
    [Fact]
    public async Task HiddenOpenAndInferredSkipJitterCannotResetTheOpeningDeadline()
    {
        var opening = Opening(true) with { PrimaryTarget = new PixelPoint(50, 90), AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), opening);
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterDelays = 1000;
        fixture.Platform.Tail = index =>
        {
            var shift = index / 4 % 2;
            return Opening(true) with
            {
                PrimaryTarget = new PixelPoint(50 + shift, 90),
                AnimationSkipTarget = index / 8 % 2 == 0 ? new PixelPoint(80 + shift, 80) : null
            };
        };
        var result = await fixture.RunAsync();
        Assert.False(result.IsCancelled);
        Assert.Contains("60", result.Reason);
        Assert.InRange(fixture.Platform.Elapsed.TotalSeconds, 60, 61);
        Assert.Equal(0, result.OpenedPacks);
        Assert.True(fixture.Platform.Clicks.Count < 400);
    }

    /// <summary>上一包已验证的Skip在下一次免费购买后立即复用，按钮尚未出现也持续独立点击。</summary>
    [Fact]
    public async Task LastVerifiedSkipIsReusedDuringTheNextPurchasedAnimation()
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip, Results(), Detail("a"),
            Detail("b", true), Dialog(), Unknown(), Unknown(), Results(), Detail("b"), Detail("a"));
        fixture.Platform.UseRequestedDelay = true;
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Contains(fixture.Platform.Clicks, click => click.Screen == PackScreen.Unknown && click.Point == new PixelPoint(80, 80));
        Assert.Equal(2, fixture.Platform.Clicks.Count(click => click.Screen == PackScreen.FreePurchaseDialog));
    }

    /// <summary>隐藏Skip连续点击保持固定最小间隔，不因多次无响应退避到八百毫秒。</summary>
    [Fact]
    public async Task HiddenSkipKeepsAConstantIntervalUntilTheResultsAppear()
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip, Unknown());
        fixture.Platform.UseRequestedDelay = true;
        fixture.Platform.StopAfterCaptures = 80;
        await fixture.RunAsync();
        var times = fixture.Platform.ClickTimes.Where((_, index) => fixture.Platform.Clicks[index].Point == new PixelPoint(80, 80)).ToArray();
        Assert.True(times.Length >= 12);
        Assert.All(times.Zip(times.Skip(1)).Skip(2), pair => Assert.InRange((pair.Second - pair.First).TotalMilliseconds, 160, 240));
    }

    /// <summary>动画已验证Skip后打开文字闪隐不延迟跳过，双帧结果转换仍禁止沿用动画坐标。</summary>
    [Theory]
    [InlineData(PackScreen.Unknown)]
    [InlineData(PackScreen.Results)]
    public async Task CachedSkipAcceptsAnimationFlickerButStopsOnResults(PackScreen next)
    {
        var detail = Detail("a", true);
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, detail, Dialog(), Dialog(), skip, skip, skip,
            next == PackScreen.Unknown ? Unknown() : Results()];
        fixture.Platform.ChangeFrame = (count, frame) => frame with { CapturedAtUtc = frame.CapturedAtUtc.AddMilliseconds(count * 160) };
        fixture.Platform.StopAfterCaptures = 9;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Equal(next == PackScreen.Unknown ? 2 : 1,
            fixture.Platform.Clicks.Count(click => click.Point == new PixelPoint(80, 80)));
    }

    /// <summary>完整字形签名一致时，真实星生/星尘OCR采样差异仍属于原包，继续下一包并完成轮次。</summary>
    [Fact]
    public async Task ExactVisualTitleSurvivesTheObservedStardustOcrAlias()
    {
        var initial = TitledDetail("编织羁绊的星生", "1aa8999c00000000", true) with { TitleVisualSignature = "stardust" };
        var returned = initial with { PackTitle = "编织羁绊的星尘", FreeOffer = false };
        var fixture = new Fixture(initial, Dialog(), Results(), returned,
            TitledDetail("另一卡包", "1aa8999c00000000") with { TitleVisualSignature = "other" }, returned);
        var result = await fixture.RunAsync();
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Empty(fixture.Platform.Diagnostics);
    }

    /// <summary>两帧OCR文字不同而完整字形相同时允许稳定核验，单侧签名缺失或字形不同则等待。</summary>
    [Theory]
    [InlineData("stardust", "stardust", true)]
    [InlineData("stardust", "other", false)]
    [InlineData("stardust", "", false)]
    [InlineData("", "stardust", false)]
    public async Task VisualEvidenceControlsTheStablePair(string firstSignature, string secondSignature, bool accepted)
    {
        var first = TitledDetail("星生", "same", true) with { TitleVisualSignature = firstSignature };
        var second = first with { PackTitle = "星尘", TitleVisualSignature = secondSignature };
        var fixture = new Fixture(first);
        fixture.Platform.Observations = [first, second];
        fixture.Platform.StopAfterCaptures = 3;
        await fixture.RunAsync();
        Assert.Equal(accepted ? 1 : 0, fixture.Platform.Clicks.Count);
    }

    /// <summary>完整标题与字形不同、旧卡图哈希碰撞时仍逐包扫描，精确文字身份不受旧哈希干扰。</summary>
    [Fact]
    public async Task DifferentExactTitlesCannotMergeWhenOldHashesCollide()
    {
        var packs = Enumerable.Range(0, 20).Select(index => TitledDetail("不同卡包" + index, "same")
            with { TitleVisualSignature = "actual-glyph-" + index }).ToArray();
        var fixture = new Fixture(packs.Concat([packs[0]]).ToArray());
        var result = await fixture.RunAsync();
        Assert.Equal(20, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.Equal(20, fixture.Platform.Clicks.Count);
    }

    /// <summary>跨包隐藏Skip例外仅复用旧坐标，新Open或新Skip只出现一帧时禁止点击和学习。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APreviouslyCachedSkipNeverAuthorizesASingleFrameNewTarget(bool open)
    {
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(Detail("a", true), Dialog(), skip, Results(), Detail("a"), Detail("b", true), Dialog());
        var changed = skip with { PrimaryTarget = open ? new PixelPoint(50, 90) : new PixelPoint(81, 80), AnimationSkipTarget = new PixelPoint(81, 80) };
        fixture.Platform.Observations = fixture.Platform.Observations.Concat([changed, Unknown()]).ToArray();
        fixture.Platform.StopAfterCaptures = fixture.Platform.Observations.Length + 1;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Point == changed.PrimaryTarget || click.Point == changed.AnimationSkipTarget);
    }

    /// <summary>真实美丽的漆黑蔷薇标题保持精确一致时，背景引起的SHA变动不影响双帧或原包返回。</summary>
    [Fact]
    public async Task ExactOcrTitleRemainsStableWhenTheWhiteMaskChanges()
    {
        var detail = TitledDetail("美丽的漆黑蔷薇", "33a9998c00000000", true) with { TitleVisualSignature = "9BB6A1" };
        var changed = detail with { TitleVisualSignature = "10CDEA" };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, changed, Dialog(), Dialog(), Results(), Results(),
            detail with { FreeOffer = false }, changed with { FreeOffer = false },
            TitledDetail("其他卡包", "same"), TitledDetail("其他卡包", "same"), detail, changed];
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
    }

    /// <summary>结果只出现一帧即被快速点击确认，返回原包双帧后仍计数并切换下一包。</summary>
    [Fact]
    public async Task ABriefObservedResultCanReturnWithoutASeparateConfirmationClick()
    {
        var detail = TitledDetail("美丽的漆黑蔷薇", "same", true) with { TitleVisualSignature = "9BB6A1" };
        var returned = detail with { FreeOffer = false, TitleVisualSignature = "10CDEA" };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, detail, Dialog(), Dialog(), Results(), Unknown(), returned, returned,
            TitledDetail("其他卡包", "other"), TitledDetail("其他卡包", "other"), returned, returned];
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Contains("一轮", result.Reason);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Screen == PackScreen.Results);
        Assert.Equal(2, fixture.Platform.Clicks.Count(click => click.Point == detail.NextTarget));
    }

    /// <summary>结果可在主观察或Skip复核帧闪现，之后任何未知或动画过渡都停止重放旧Skip。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnObservedResultFreezesCachedAnimationClicks(bool resultDuringSkipValidation, bool openingTransition)
    {
        var detail = TitledDetail("原卡包", "same", true);
        var returned = detail with { FreeOffer = false };
        var opening = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var transition = openingTransition ? opening : Unknown();
        var frames = new List<PackObservation> { detail, detail, Dialog(), Dialog(), opening, opening };
        if (resultDuringSkipValidation) frames.Add(Unknown());
        frames.AddRange([Results(), Unknown(), transition, transition, returned, returned,
            TitledDetail("下一卡包", "other"), TitledDetail("下一卡包", "other"), returned, returned]);
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = frames.ToArray();
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Single(fixture.Platform.Clicks, click => click.Point == new PixelPoint(80, 80));
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Screen == PackScreen.Unknown);
    }

    /// <summary>购买后费用读不到而没有任何结果证据时只等待，不把未知或仍免费的详情计为完成。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AReturnNeedsAnObservedResultAndAConsumedFreeOffer(bool stillFree, bool seenResult)
    {
        var detail = TitledDetail("原卡包", "same", true);
        var fixture = new Fixture(detail);
        var frames = new List<PackObservation> { detail, detail, Dialog(), Dialog() };
        if (seenResult) frames.AddRange([Results(), Unknown()]);
        frames.AddRange([detail with { FreeOffer = stillFree }, detail with { FreeOffer = stillFree }]);
        fixture.Platform.Observations = frames.ToArray();
        fixture.Platform.StopAfterCaptures = 20;
        var result = await fixture.RunAsync();
        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.OpenedPacks);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Point == detail.NextTarget);
    }

    /// <summary>快速确认后导航暂失焦，已返回阶段保持完成计数，恢复期间不向未知详情发送旧Skip。</summary>
    [Fact]
    public async Task RecoveredNavigationAfterAnImplicitResultNeverReplaysTheOldSkip()
    {
        var detail = TitledDetail("原卡包", "same", true);
        var skip = Opening(true) with { AnimationSkipTarget = new PixelPoint(80, 80) };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, detail, Dialog(), Dialog(), skip, skip, Results(), Unknown(),
            detail with { FreeOffer = false }, detail with { FreeOffer = false }, Unknown(), Unknown(),
            detail with { FreeOffer = false }, detail with { FreeOffer = false },
            TitledDetail("下一卡包", "other"), TitledDetail("下一卡包", "other"), detail, detail];
        fixture.Platform.TemporaryClickAt = 4;
        var result = await fixture.RunAsync();
        Assert.Equal(1, result.OpenedPacks);
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, fixture.Platform.RecoverCalls);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Screen == PackScreen.Unknown);
        Assert.Single(fixture.Platform.Clicks, click => click.Point == new PixelPoint(80, 80));
    }

    /// <summary>单侧OCR标题缺失时，相同完整字形与旧哈希均不得替代缺失的免费入口身份。</summary>
    /// <param name="missingFirst">首帧缺少标题；否则第二帧缺少标题。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASingleMissingOcrTitleCannotUseAnIdenticalVisualSignature(bool missingFirst)
    {
        var detail = TitledDetail("原卡包", "same", true) with { TitleVisualSignature = new string('A', 64) };
        var first = detail with { PackTitle = missingFirst ? "" : detail.PackTitle };
        var second = detail with { PackTitle = missingFirst ? detail.PackTitle : "" };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [first, second];
        fixture.Platform.StopAfterCaptures = 3;

        var result = await fixture.RunAsync();

        Assert.True(result.IsCancelled);
        Assert.Equal(0, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Empty(fixture.Platform.Clicks);
    }

    /// <summary>快速结果后的原包收费详情只有一帧时，免费重现、未知或其他卡包均不得完成返回或导航。</summary>
    /// <param name="secondFrame">第二帧变为仍免费、未知或不同卡包详情。</param>
    [Theory]
    [InlineData("free")]
    [InlineData("unknown")]
    [InlineData("other")]
    public async Task AnImplicitReturnRequiresTwoConsistentOriginalPaidDetails(string secondFrame)
    {
        var detail = TitledDetail("原卡包", "same", true) with { TitleVisualSignature = new string('A', 64) };
        var returned = detail with { FreeOffer = false, TitleVisualSignature = new string('B', 64) };
        var second = secondFrame switch
        {
            "free" => detail,
            "unknown" => Unknown(),
            _ => returned with { PackTitle = "其他卡包", TitleVisualSignature = new string('C', 64) }
        };
        var fixture = new Fixture(detail);
        fixture.Platform.Observations = [detail, detail, Dialog(), Dialog(), Results(), Unknown(), returned, second];
        fixture.Platform.StopAfterCaptures = 9;

        var result = await fixture.RunAsync();

        Assert.True(result.IsCancelled);
        Assert.Equal(1, result.ScannedPacks);
        Assert.Equal(0, result.OpenedPacks);
        Assert.Equal(2, fixture.Platform.Clicks.Count);
        Assert.DoesNotContain(fixture.Platform.Clicks, click => click.Point == detail.NextTarget);
        Assert.Single(fixture.Platform.Clicks, click => click.Screen == PackScreen.FreePurchaseDialog);
        Assert.All(fixture.Progress.Values, progress => Assert.Equal(0, progress.OpenedPacks));
    }

    /// <summary>构造具有真实OCR标题的已识别卡包详情。</summary>
    private static PackObservation TitledDetail(string title, string fingerprint, bool free = false)
        => Detail(fingerprint, free) with { PackTitle = title };

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
        /// <summary>每次请求延迟的累计时长。</summary>
        public TimeSpan Elapsed { get; private set; }
        /// <summary>使用请求间隔推进截图时钟，便于检查退避与恢复期限。</summary>
        public bool UseRequestedDelay { get; set; }
        /// <summary>进入指定捕获序号前抛出一次临时失焦。</summary>
        public HashSet<int> TemporaryCaptureAt { get; } = [];
        /// <summary>连续制造捕获临时失焦，验证激活假阳性不得续期恢复预算。</summary>
        public bool AlwaysTemporaryCaptureFailure { get; set; }
        /// <summary>在指定点击成功序号前抛出一次临时失焦。</summary>
        public int TemporaryClickAt { get; set; } = int.MaxValue;
        /// <summary>指定恢复是否取得原窗口前台。</summary>
        public bool RecoverSucceeds { get; set; } = true;
        /// <summary>恢复成功前的暂时失败次数。</summary>
        public int RecoverFailuresRemaining { get; set; }
        /// <summary>恢复发生永久窗口错误时抛出的异常。</summary>
        public Exception? RecoverFailure { get; set; }
        /// <summary>模拟系统恢复调用自身耗费的处理时间。</summary>
        public Action? RecoverObserver { get; set; }
        /// <summary>实际尝试恢复次数。</summary>
        public int RecoverCalls { get; private set; }
        /// <summary>恢复尝试时的累计请求延迟。</summary>
        public List<TimeSpan> RecoverElapsed { get; } = [];
        /// <summary>按延迟次数触发F8，验证三秒暂停可以立即取消。</summary>
        public int StopAfterDelays { get; set; } = int.MaxValue;
        /// <summary>按顺序记录每一次等待请求。</summary>
        public List<TimeSpan> Delays { get; } = [];
        /// <summary>推进注入的单调时钟的测试边界。</summary>
        public Action<TimeSpan>? DelayObserver { get; set; }
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
        /// <summary>点击成功时的累计请求延迟。</summary>
        public List<TimeSpan> ClickTimes { get; } = [];
        /// <summary>点击所依据的最新捕获帧序号。</summary>
        public List<int> ClickFrameIndices { get; } = [];
        /// <summary>只存在内存中的诊断截图和原因。</summary>
        public List<(GameFrame Frame, string Reason)> Diagnostics { get; } = [];
        /// <summary>每次捕获时的同步上下文，用于确认截图在后台运行。</summary>
        public List<SynchronizationContext?> CaptureContexts { get; } = [];
        /// <summary>模拟 F8 停止键状态。</summary>
        public bool IsStopRequested => CaptureCount >= StopAfterCaptures || Delays.Count >= StopAfterDelays;
        /// <summary>保存虚构观察序列。</summary>
        public FakePlatform(PackObservation[] observations) => Observations = observations;
        /// <summary>记录激活而不寻找或操作任何窗口。</summary>
        public void ActivateGame() { Fail("activate"); Activations++; }
        /// <summary>模拟恢复原窗口，记录恢复时机而不操作任何系统窗口。</summary>
        public bool TryRecoverGame()
        {
            RecoverCalls++;
            RecoverElapsed.Add(Elapsed);
            RecoverObserver?.Invoke();
            if (RecoverFailure is { } failure) throw failure;
            if (RecoverFailuresRemaining > 0) { RecoverFailuresRemaining--; return false; }
            return RecoverSucceeds;
        }
        /// <summary>创建包含识别索引的虚构帧。</summary>
        public GameFrame Capture()
        {
            Fail("capture");
            if (AlwaysTemporaryCaptureFailure || TemporaryCaptureAt.Remove(CaptureCount + 1)) throw new GameWindowTemporarilyUnavailableException("测试游戏暂时失焦");
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
            if (Clicks.Count + 1 == TemporaryClickAt)
            {
                TemporaryClickAt = int.MaxValue;
                throw new GameWindowTemporarilyUnavailableException("点击前测试游戏暂时失焦");
            }
            if (Clicks.Count + 1 == FailClickAt) throw new InvalidOperationException("测试确认结果输入失败");
            Clicks.Add((Observation(frame).Screen, point));
            ClickTimes.Add(Elapsed);
            ClickFrameIndices.Add(BitConverter.ToInt32(frame.Pixels));
        }
        /// <summary>推进虚构时间而不让测试真实等待一分钟。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Fail("delay");
            if (CancelDelay) throw new OperationCanceledException();
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Elapsed += delay;
            DelayObserver?.Invoke(delay);
            if (!FreezeClock) clock += UseRequestedDelay ? delay : TimeSpan.FromSeconds(1);
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
        /// <summary>保存诊断关联标识和原始计数，验证事件配对与失败清理。</summary>
        public List<IReadOnlyDictionary<string, object?>> Records { get; } = [];
        /// <summary>测试不创建日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>记录所有日志等级。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存格式化正文和原异常。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
            Records.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(item => item.Key, item => item.Value));
        }
    }
}
