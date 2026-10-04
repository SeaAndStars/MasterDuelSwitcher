using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用真实游戏截图和真实 OpenCV 图像处理验证免费卡包识别及动作边界。</summary>
public sealed class FreePackVisionTests : IDisposable
{
    /// <summary>将费用及标题文字边界隔离后的真实 OpenCV 截图识别器。</summary>
    private readonly OpenCvPackRecognizer recognizer = new(feeVerifier: new RecordingFeeVerifier(true),
        titleReader: new RecordingTitleReader("独立卡包标题"));

    /// <summary>向测试结果记录四种真实界面的逐帧识别耗时。</summary>
    private readonly ITestOutputHelper output;

    /// <summary>测试帧使用的固定捕获时间，避免断言依赖系统时钟。</summary>
    private static readonly DateTimeOffset capturedAtUtc = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>接收测试输出通道，性能记录仅写入测试结果。</summary>
    /// <param name="output">当前测试的输出记录器。</param>
    public FreePackVisionTests(ITestOutputHelper output) => this.output = output;

    /// <summary>独立免费按钮片段缺少详情状态锚点，各缩放下均不得授权任何点击。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void FreeButtonFragmentDoesNotAuthorizeDetailsOrClicks(double scale)
    {
        using var original = LoadFixture("free-button.png");
        using var image = Resize(original, scale);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>真实免费确认弹窗须同时识别免费文字和购买按钮，并将目标定位在右侧购买按钮内。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void FreeConfirmationHasVerifiedPurchaseTarget(double scale)
    {
        using var original = LoadFixture("free-confirm.png");
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.FreePurchaseDialog, observation.Screen);
        Assert.True(observation.FreeOffer);
        AssertTargetInside(observation.PrimaryTarget, new Rect(565, 469, 351, 60), scale, image);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>真实长卡包名导致免费文字横向移动时，整窗和客户区各缩放仍应验证费用行及购买按钮。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void LongTitleFreeConfirmationHasVerifiedPurchaseTarget(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("free-confirm-long-title.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var purchaseRegion = includeTitlebar ? new Rect(1041, 683, 355, 65) : new Rect(1040, 652, 355, 65);
        Assert.Equal(PackScreen.FreePurchaseDialog, observation.Screen);
        Assert.True(observation.FreeOffer);
        AssertTargetInside(observation.PrimaryTarget, purchaseRegion, scale, image);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>真实宝石收费弹窗在整窗及客户区四种缩放下均须保持未验证状态，不得生成购买动作。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void PaidConfirmationDoesNotAuthorizePurchase(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("paid-confirm.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>仅遮掉原图的免费文字时仍应识别购买弹窗，但不得返回购买目标或免费标志。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ConfirmationWithoutFreeTextIsUnverified(double scale)
    {
        using var original = LoadFixture("free-confirm.png");
        PaintFromNearbyPixel(original, new Rect(590, 370, 150, 60), 751, 399);
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>免费文字仍在而购买按钮已被遮掉时，不得将文字单独作为购买授权。</summary>
    [Fact]
    public void ConfirmationWithoutPurchaseButtonHasNoPurchaseTarget()
    {
        using var image = LoadFixture("free-confirm.png");
        PaintFromNearbyPixel(image, new Rect(560, 460, 360, 74), 952, 493);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.NotEqual(PackScreen.FreePurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
    }

    /// <summary>打开和跳过同时存在时，动画状态应优先选择右下角跳过按钮。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void OpeningPrefersSkipOverOpen(double scale)
    {
        using var original = LoadFixture("opening.png");
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, new Rect(1109, 941, 243, 58), scale, image);
        Assert.Equal(observation.PrimaryTarget, observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>跳过按钮缺失但打开按钮仍在时，应将动画动作定位在真实打开按钮内。</summary>
    [Fact]
    public void OpeningWithoutSkipUsesOpenButton()
    {
        using var image = LoadFixture("opening.png");
        PaintFromNearbyPixel(image, new Rect(1100, 930, 265, 82), 1060, 976);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, new Rect(205, 846, 300, 77), 1, image);
        AssertTargetInside(observation.AnimationSkipTarget, new Rect(1109, 941, 243, 58), 1, image);
        Assert.NotEqual(observation.PrimaryTarget, observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
    }

    /// <summary>含原始标题栏的结果全图应识别为结果页，确认坐标仍相对整张测试帧。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ResultsHasConfirmationTargetInFrameCoordinates(double scale)
    {
        using var original = LoadFixture("results.png");
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.Results, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, new Rect(1519, 1063, 402, 65), scale, image);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>真实完整开包画面在整窗和客户区各缩放下，应优先点击右下角跳过按钮。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void FullWindowOpeningPrefersActualSkipButton(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("opening-full-window.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var skipRegion = includeTitlebar ? new Rect(1776, 1062, 248, 65) : new Rect(1775, 1031, 248, 65);
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, skipRegion, scale, image);
        Assert.Equal(observation.PrimaryTarget, observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>完整开包图仅移除跳过按钮时，应回退到仍然可见的真实打开文字。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void FullWindowOpeningWithoutSkipUsesActualOpenLabel(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("opening-full-window.png");
        PaintFromNearbyPixel(original, new Rect(1770, 1054, 264, 80), 1700, 1100);
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var openRegion = includeTitlebar ? new Rect(962, 984, 119, 48) : new Rect(961, 953, 119, 48);
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, openRegion, scale, image);
        var skipRegion = includeTitlebar ? new Rect(1776, 1062, 248, 65) : new Rect(1775, 1031, 248, 65);
        AssertTargetInside(observation.AnimationSkipTarget, skipRegion, scale, image);
        Assert.NotEqual(observation.PrimaryTarget, observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>真实跳过按钮移到左上方且原位置清空时，应忽略错误位置的控件并回退到打开文字。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void FullWindowOpeningIgnoresSkipMovedOutsideActionRegion(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("opening-full-window.png");
        using var skip = Crop(original, new Rect(1776, 1062, 248, 65));
        PaintFromNearbyPixel(original, new Rect(1770, 1054, 264, 80), 1700, 1100);
        using (var destination = new Mat(original, new Rect(180, 220, skip.Width, skip.Height)))
            skip.CopyTo(destination);
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var openRegion = includeTitlebar ? new Rect(962, 984, 119, 48) : new Rect(961, 953, 119, 48);
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, openRegion, scale, image);
        var skipRegion = includeTitlebar ? new Rect(1776, 1062, 248, 65) : new Rect(1775, 1031, 248, 65);
        AssertTargetInside(observation.AnimationSkipTarget, skipRegion, scale, image);
        Assert.NotEqual(observation.PrimaryTarget, observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>打开状态文字缺失而跳过按钮仍在时，应保留未知状态并撤回全部动作。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void FullWindowOpeningWithoutOpenAnchorDoesNotAuthorizeSkip(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("opening-full-window.png");
        PaintFromNearbyPixel(original, new Rect(944, 966, 160, 88), 1310, 1008);
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>真实结果图右侧含秘密卡包面板时，确认动作仍应落在该面板下方的真实确认按钮内。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void SecretSidebarResultsHasActualConfirmationTarget(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("results-secret-sidebar.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var confirmationRegion = includeTitlebar ? new Rect(1519, 1063, 402, 65) : new Rect(1518, 1032, 402, 65);
        Assert.Equal(PackScreen.Results, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, confirmationRegion, scale, image);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>结果标题和秘密卡包面板仍在而确认按钮缺失时，整窗及客户区均不得生成动作。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void SecretSidebarResultsWithoutConfirmationDoNotAuthorizeClicks(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("results-secret-sidebar.png");
        PaintFromNearbyPixel(original, new Rect(1505, 1053, 432, 85), 1570, 1000);
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>收费详情只提供最右侧双箭头导航，不得授权购买或误选详情行上的单箭头。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void PaidDetailsHasDoubleArrowNavigationWithoutPurchase(double scale)
    {
        using var original = LoadFixture("paid-details.png");
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        AssertTargetInside(observation.NextTarget, new Rect(1931, 541, 75, 129), scale, image);
        AssertFingerprint(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>开包后实际收费详情截图在整窗或客户区的四种缩放下均仅提供双箭头导航。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void PaidDetailsAfterOpeningHasNavigationWithoutFreeOffer(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("paid-details-after-opening.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var navigationRegion = includeTitlebar ? new Rect(1931, 541, 75, 129) : new Rect(1930, 510, 75, 129);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        AssertTargetInside(observation.NextTarget, navigationRegion, scale, image);
        AssertFingerprint(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>真实单行免费详情在整窗和客户区的四种缩放下，购买点须位于该行黄色免费按钮内。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void SingleRowFreeDetailsHasVerifiedPurchaseTarget(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("free-details-single-row.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var purchaseRegion = includeTitlebar ? new Rect(1311, 897, 493, 81) : new Rect(1310, 866, 493, 81);
        var navigationRegion = includeTitlebar ? new Rect(1931, 541, 75, 129) : new Rect(1930, 510, 75, 129);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.True(observation.FreeOffer);
        AssertTargetInside(observation.PrimaryTarget, purchaseRegion, scale, image);
        AssertTargetInside(observation.NextTarget, navigationRegion, scale, image);
        AssertFingerprint(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>另一真实卡包的单行免费详情在整窗及客户区各缩放下，须验证黄色购买按钮和下一包导航。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void SecondTitleSingleRowFreeDetailsHasVerifiedPurchaseTarget(double scale, bool includeTitlebar)
    {
        using var original = LoadFixture("free-details-second-title.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        var purchaseRegion = includeTitlebar ? new Rect(1311, 897, 493, 81) : new Rect(1310, 866, 493, 81);
        var navigationRegion = includeTitlebar ? new Rect(1931, 541, 75, 129) : new Rect(1930, 510, 75, 129);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.True(observation.FreeOffer);
        AssertTargetInside(observation.PrimaryTarget, purchaseRegion, scale, image);
        AssertTargetInside(observation.NextTarget, navigationRegion, scale, image);
        AssertFingerprint(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>第三个真实卡包标题与两个已有卡包标题，在客户区四种缩放下均须保持身份区分。</summary>
    [Theory]
    [InlineData("paid-details.png", 0.65)]
    [InlineData("paid-details.png", 0.85)]
    [InlineData("paid-details.png", 1.0)]
    [InlineData("paid-details.png", 1.25)]
    [InlineData("paid-details-after-opening.png", 0.65)]
    [InlineData("paid-details-after-opening.png", 0.85)]
    [InlineData("paid-details-after-opening.png", 1.0)]
    [InlineData("paid-details-after-opening.png", 1.25)]
    public void ThirdRealPackTitleIsDistinctFromBothEarlierTitles(string earlierFixture, double scale)
    {
        using var thirdOriginal = LoadFixture("free-details-second-title.png");
        using var earlierOriginal = LoadFixture(earlierFixture);
        using var thirdClient = Crop(thirdOriginal, new Rect(1, 31, 2048, 1152));
        using var earlierClient = Crop(earlierOriginal, new Rect(1, 31, 2048, 1152));
        using var thirdImage = Resize(thirdClient, scale);
        using var earlierImage = Resize(earlierClient, scale);
        var third = recognizer.Recognize(ToFrame(thirdImage));
        var earlier = recognizer.Recognize(ToFrame(earlierImage));
        Assert.Equal(PackScreen.PackDetails, third.Screen);
        Assert.Equal(PackScreen.PackDetails, earlier.Screen);
        AssertFingerprint(third.Fingerprint);
        AssertFingerprint(earlier.Fingerprint);
        Assert.True(HammingDistance(third.Fingerprint, earlier.Fingerprint) > 4);
    }

    /// <summary>真实单行入口仅保留免费文字而单包文字缺失时，不得授权购买且标题身份应保持。</summary>
    [Fact]
    public void SingleRowFreeWordWithoutOnePackDoesNotAuthorizePurchase()
    {
        using var image = LoadFixture("free-details-single-row.png");
        var before = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        AssertFingerprint(before.Fingerprint);
        PaintFromNearbyPixel(image, new Rect(1395, 910, 85, 52), 1550, 964);
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        Assert.NotNull(after.NextTarget);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>将原始单包文字移到免费入口上方其他行后，两行文字不得被组合为同一次免费购买。</summary>
    [Fact]
    public void OnePackOnDifferentPurchaseRowDoesNotAuthorizeFreeOffer()
    {
        using var image = LoadFixture("free-details-single-row.png");
        using var onePack = Crop(image, new Rect(1404, 915, 62, 42));
        var before = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        AssertFingerprint(before.Fingerprint);
        PaintFromNearbyPixel(image, new Rect(1395, 910, 85, 52), 1550, 964);
        using (var destination = new Mat(image, new Rect(1404, 845, onePack.Width, onePack.Height)))
            onePack.CopyTo(destination);
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        Assert.NotNull(after.NextTarget);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>原已验证入口的两段文字仍同一行，但中间黄色区域完全断开时不得作为同一个购买按钮。</summary>
    [Fact]
    public void SeparatedYellowButtonsDoNotAuthorizeFreeOffer()
    {
        using var image = CreateFreeDetails();
        var before = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        Assert.True(before.FreeOffer);
        Assert.NotNull(before.PrimaryTarget);
        AssertFingerprint(before.Fingerprint);
        using (var gap = new Mat(image, new Rect(1480, 825, 127, 78)))
            gap.SetTo(new Scalar(0, 0, 0, 255));
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        Assert.NotNull(after.NextTarget);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>原已验证黄色按钮被贯穿整高的八像素黑缝隔开时，两段文字不得继续共同授权购买。</summary>
    [Fact]
    public void NarrowFullHeightGapDoesNotAuthorizeFreeOffer()
    {
        using var image = CreateFreeDetails();
        var before = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        Assert.True(before.FreeOffer);
        Assert.NotNull(before.PrimaryTarget);
        AssertFingerprint(before.Fingerprint);
        using (var gap = new Mat(image, new Rect(1510, 825, 8, 78)))
            gap.SetTo(new Scalar(0, 0, 0, 255));
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        Assert.NotNull(after.NextTarget);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>真实免费入口两段文字仍完整但按钮验证色带已处于裁剪帧外时，应保留导航及身份并撤回购买授权。</summary>
    [Fact]
    public void FreeWordsWithoutInFrameYellowBandDoNotAuthorizePurchase()
    {
        using var original = LoadFixture("free-details-single-row.png");
        var before = recognizer.Recognize(ToFrame(original));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        Assert.True(before.FreeOffer);
        AssertFingerprint(before.Fingerprint);
        using var image = Crop(original, new Rect(0, 0, 2050, 953));
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        AssertTargetInside(after.NextTarget, new Rect(1931, 541, 75, 129), 1, image);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        AssertConfidence(after);
    }

    /// <summary>菜单和双箭头保留而真实标题被背景像素完全遮掉时，缺失身份的详情不得产生任何点击。</summary>
    [Fact]
    public void BlankPackTitleHeaderDoesNotAuthorizeDetails()
    {
        using var image = LoadFixture("paid-details.png");
        PaintFromNearbyPixel(image, new Rect(284, 81, 1000, 45), 1210, 93);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>同名卡包由真实免费画面变成收费画面并切换动画插画时，同一缩放和裁剪下身份须一致。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void SamePackTitleKeepsIdentityAcrossAnimationAndPurchaseState(double scale, bool includeTitlebar)
    {
        using var paidOriginal = LoadFixture("paid-details-after-opening.png");
        using var freeOriginal = LoadFixture("free-details-single-row.png");
        using var paidInput = includeTitlebar ? paidOriginal.Clone() : Crop(paidOriginal, new Rect(1, 31, 2048, 1152));
        using var freeInput = includeTitlebar ? freeOriginal.Clone() : Crop(freeOriginal, new Rect(1, 31, 2048, 1152));
        using var paidImage = Resize(paidInput, scale);
        using var freeImage = Resize(freeInput, scale);
        var paid = recognizer.Recognize(ToFrame(paidImage));
        var free = recognizer.Recognize(ToFrame(freeImage));
        Assert.Equal(PackScreen.PackDetails, paid.Screen);
        Assert.Equal(PackScreen.PackDetails, free.Screen);
        AssertFingerprint(paid.Fingerprint);
        AssertFingerprint(free.Fingerprint);
        Assert.Equal(paid.Fingerprint, free.Fingerprint);
    }

    /// <summary>两张真实详情的卡包标题不同时，整窗与客户区各缩放下的身份距离须超过同包容差。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void DifferentRealPackTitlesHaveDistinctIdentity(double scale, bool includeTitlebar)
    {
        using var firstOriginal = LoadFixture("paid-details.png");
        using var secondOriginal = LoadFixture("paid-details-after-opening.png");
        using var firstInput = includeTitlebar ? firstOriginal.Clone() : Crop(firstOriginal, new Rect(1, 31, 2048, 1152));
        using var secondInput = includeTitlebar ? secondOriginal.Clone() : Crop(secondOriginal, new Rect(1, 31, 2048, 1152));
        using var firstImage = Resize(firstInput, scale);
        using var secondImage = Resize(secondInput, scale);
        var first = recognizer.Recognize(ToFrame(firstImage));
        var second = recognizer.Recognize(ToFrame(secondImage));
        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(PackScreen.PackDetails, second.Screen);
        AssertFingerprint(first.Fingerprint);
        AssertFingerprint(second.Fingerprint);
        Assert.True(HammingDistance(first.Fingerprint, second.Fingerprint) > 4);
    }

    /// <summary>保持详情锚点并贴入原图免费入口后，各缩放下均应授权该入口且保留下一包导航。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void DetailsWithRealFreeButtonHasVerifiedFreeEntry(double scale)
    {
        using var original = CreateFreeDetails();
        using var image = Resize(original, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.True(observation.FreeOffer);
        AssertTargetInside(observation.PrimaryTarget, new Rect(1312, 825, 491, 78), scale, image);
        AssertTargetInside(observation.NextTarget, new Rect(1931, 541, 75, 129), scale, image);
        AssertFingerprint(observation.Fingerprint);
        AssertConfidence(observation);
    }

    /// <summary>收费页双箭头被遮掉时，详情行和货币栏单箭头不得被返回为下一包目标。</summary>
    [Fact]
    public void SingleArrowsDoNotReplaceMissingDoubleArrow()
    {
        using var image = LoadFixture("paid-details.png");
        PaintFromNearbyPixel(image, new Rect(1917, 527, 113, 154), 1905, 705);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Null(observation.NextTarget);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
    }

    /// <summary>灰色空白和黑屏都不含状态锚点，识别器不得从统一色块生成点击目标。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public void UniformFramesAreUnknown(int shade)
    {
        using var image = new Mat(1184, 2050, MatType.CV_8UC4, new Scalar(shade, shade, shade, 255));
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>收费及完整免费详情整体被深色遮罩压暗时，即使文字轮廓相似也不得返回任何操作目标。</summary>
    [Theory]
    [InlineData("paid-details.png")]
    [InlineData("free-details-single-row.png")]
    public void DarkenedDetailsAreUnknown(string fixture)
    {
        using var original = LoadFixture(fixture);
        using var image = DarkenRgb(original, .35);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>合法但小于模板的图像应返回未知状态，不得使模板匹配抛出 OpenCV 尺寸错误。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(32, 20)]
    [InlineData(128, 20)]
    public void TinyValidFramesAreUnknown(int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC4, new Scalar(127, 127, 127, 255));
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>帧为空时应在访问任何图像或模板前报告空参数。</summary>
    [Fact]
    public void NullFrameIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => recognizer.Recognize(null!));
    }

    /// <summary>像素缓冲为空引用时应报告参数错误，而非进入原生图像读取。</summary>
    [Fact]
    public void NullPixelsAreRejected()
    {
        var frame = new GameFrame(1, 16, 8, 0, 0, null!, capturedAtUtc);
        Assert.ThrowsAny<ArgumentException>(() => recognizer.Recognize(frame));
    }

    /// <summary>非正尺寸及像素总量超过数组上限的尺寸必须在原生分配前拒绝。</summary>
    [Theory]
    [InlineData(0, 8)]
    [InlineData(16, 0)]
    [InlineData(-1, 8)]
    [InlineData(16, -1)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidDimensionsAreRejected(int width, int height)
    {
        var frame = new GameFrame(1, width, height, 0, 0, [], capturedAtUtc);
        Assert.ThrowsAny<ArgumentException>(() => recognizer.Recognize(frame));
    }

    /// <summary>紧密 BGRA 必须恰好拥有每像素四字节，截断、附加字节或三通道长度均须拒绝。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(513)]
    [InlineData(384)]
    public void MalformedBgraLengthIsRejected(int length)
    {
        var frame = new GameFrame(1, 16, 8, 0, 0, new byte[length], capturedAtUtc);
        Assert.ThrowsAny<ArgumentException>(() => recognizer.Recognize(frame));
    }

    /// <summary>屏幕原点与窗口句柄变化不得被加到返回的客户区内目标坐标上。</summary>
    [Fact]
    public void ScreenOriginDoesNotOffsetClientTargets()
    {
        using var image = LoadFixture("paid-details.png");
        var frame = ToFrame(image);
        var first = recognizer.Recognize(frame);
        var moved = recognizer.Recognize(frame with { WindowHandle = 17, ScreenX = -2560, ScreenY = -120 });
        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(first.Screen, moved.Screen);
        Assert.NotNull(first.NextTarget);
        Assert.Equal(first.NextTarget, moved.NextTarget);
        Assert.Equal(first.Fingerprint, moved.Fingerprint);
    }

    /// <summary>识别器只读取调用方像素；处理后同一缓冲应保持逐字节一致。</summary>
    [Fact]
    public void RecognitionPreservesCallerPixels()
    {
        using var image = LoadFixture("paid-details.png");
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        Assert.Equal(PackScreen.PackDetails, recognizer.Recognize(frame).Screen);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>同一卡包标题在四种缩放下的身份距离不超过四位；免费入口叠加不得改变身份。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void StableTitleFingerprintAcrossScaleAndFreeOffer(double scale)
    {
        using var original = LoadFixture("paid-details.png");
        using var freeDetails = CreateFreeDetails();
        using var resized = Resize(original, scale);
        using var resizedFree = Resize(freeDetails, scale);
        var baseline = recognizer.Recognize(ToFrame(original)).Fingerprint;
        var scaled = recognizer.Recognize(ToFrame(resized)).Fingerprint;
        var free = recognizer.Recognize(ToFrame(resizedFree)).Fingerprint;
        AssertFingerprint(baseline);
        AssertFingerprint(scaled);
        AssertFingerprint(free);
        Assert.InRange(HammingDistance(baseline, scaled), 0, 4);
        Assert.InRange(HammingDistance(baseline, free), 0, 4);
    }

    /// <summary>同一卡包标题下将真实插画替换为动画中的另一图像时，详情状态、导航和身份均须保持。</summary>
    [Fact]
    public void ArtworkAnimationDoesNotChangePackIdentity()
    {
        using var original = LoadFixture("paid-details.png");
        using var changed = original.Clone();
        using var opening = LoadFixture("opening.png");
        using var otherArtwork = new Mat(opening, new Rect(228, 185, 253, 344));
        using var replacement = new Mat();
        var artworkRegion = new Rect(255, 274, 995, 380);
        Cv2.Resize(otherArtwork, replacement, artworkRegion.Size, 0, 0, InterpolationFlags.Linear);
        using (var destination = new Mat(changed, artworkRegion)) replacement.CopyTo(destination);
        var first = recognizer.Recognize(ToFrame(original));
        var second = recognizer.Recognize(ToFrame(changed));
        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(PackScreen.PackDetails, second.Screen);
        AssertFingerprint(first.Fingerprint);
        AssertFingerprint(second.Fingerprint);
        Assert.False(first.FreeOffer);
        Assert.False(second.FreeOffer);
        Assert.Null(first.PrimaryTarget);
        Assert.Null(second.PrimaryTarget);
        Assert.NotNull(first.NextTarget);
        Assert.Equal(first.NextTarget, second.NextTarget);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    /// <summary>原始窗口边框和标题栏裁掉后，详情及结果目标应落在真实客户区坐标中。</summary>
    [Theory]
    [InlineData("paid-details.png", PackScreen.PackDetails, 0.65)]
    [InlineData("paid-details.png", PackScreen.PackDetails, 0.85)]
    [InlineData("paid-details.png", PackScreen.PackDetails, 1.0)]
    [InlineData("paid-details.png", PackScreen.PackDetails, 1.25)]
    [InlineData("results.png", PackScreen.Results, 0.65)]
    [InlineData("results.png", PackScreen.Results, 0.85)]
    [InlineData("results.png", PackScreen.Results, 1.0)]
    [InlineData("results.png", PackScreen.Results, 1.25)]
    public void ActualClientCropUsesClientCoordinates(string fixture, PackScreen expected, double scale)
    {
        using var original = LoadFixture(fixture);
        using var client = Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(client, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(expected, observation.Screen);
        Assert.False(observation.FreeOffer);
        if (expected == PackScreen.PackDetails)
        {
            Assert.Null(observation.PrimaryTarget);
            AssertTargetInside(observation.NextTarget, new Rect(1930, 510, 75, 129), scale, image);
            AssertFingerprint(observation.Fingerprint);
        }
        else
        {
            AssertTargetInside(observation.PrimaryTarget, new Rect(1518, 1032, 402, 65), scale, image);
            Assert.Null(observation.NextTarget);
            Assert.Empty(observation.Fingerprint);
        }
        AssertConfidence(observation);
    }

    /// <summary>弹窗取消按钮缺失时，即使免费文字及购买按钮都在也不得授权购买。</summary>
    [Fact]
    public void ConfirmationWithoutCancelButtonIsUnverified()
    {
        using var image = LoadFixture("free-confirm.png");
        PaintFromNearbyPixel(image, new Rect(170, 460, 365, 74), 142, 493);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
    }

    /// <summary>结果标题存在但确认按钮已被遮掉时，结果页不得产生确认点击。</summary>
    [Fact]
    public void ResultsWithoutConfirmationButtonAreUnknown()
    {
        using var image = LoadFixture("results.png");
        PaintFromNearbyPixel(image, new Rect(1515, 1055, 420, 82), 1478, 1079);
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>免费入口缺少单包文字时保留详情与标题身份，但撤回免费购买授权。</summary>
    [Fact]
    public void FreeDetailsWithoutOnePackTextKeepFingerprintWithoutFreeOffer()
    {
        using var image = CreateFreeDetails();
        var before = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, before.Screen);
        Assert.True(before.FreeOffer);
        AssertFingerprint(before.Fingerprint);
        PaintFromNearbyPixel(image, new Rect(1395, 835, 75, 55), 1550, 864);
        var after = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, after.Screen);
        Assert.False(after.FreeOffer);
        Assert.Null(after.PrimaryTarget);
        Assert.NotNull(after.NextTarget);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    /// <summary>原免费文字被移到费用行上方或弹窗外背景时，费用行组合应验证失败。</summary>
    [Theory]
    [InlineData(830, 330)]
    [InlineData(855, 92)]
    public void FreeTextOutsideFeeRegionDoesNotAuthorizePurchase(int destinationX, int destinationY)
    {
        using var image = LoadFixture("free-confirm.png");
        using var freeText = Crop(image, new Rect(608, 382, 114, 31));
        PaintFromNearbyPixel(image, new Rect(590, 370, 150, 60), 751, 399);
        using (var destination = new Mat(image, new Rect(destinationX, destinationY, freeText.Width, freeText.Height)))
            freeText.CopyTo(destination);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
    }

    /// <summary>仅保留结果左上部或上半部时，缺失帧外确认按钮不得生成任何动作点。</summary>
    [Theory]
    [InlineData(512, 600)]
    [InlineData(2050, 600)]
    [InlineData(2050, 1058)]
    [InlineData(2050, 1059)]
    [InlineData(2050, 1060)]
    public void TruncatedResultsWithoutConfirmationRegionAreUnknown(int width, int height)
    {
        using var original = LoadFixture("results.png");
        using var image = Crop(original, new Rect(0, 0, width, height));
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>菜单及双箭头仍在而左侧标题被截断时，不得为缺少完整卡包标题的详情返回导航。</summary>
    [Fact]
    public void CutoffPackTitleWithMenuAndNextArrowDoesNotAuthorizeDetails()
    {
        using var original = LoadFixture("paid-details.png");
        using var image = Crop(original, new Rect(500, 0, 1550, 1184));
        AssertUnknown(recognizer.Recognize(ToFrame(image)));
    }

    /// <summary>显式日志记录实际状态、置信度及标题取样原文；重复释放后再次识别须在原生访问前拒绝。</summary>
    [Fact]
    public void ExplicitDebugLoggerRecordsObservationAndDisposalIsIdempotent()
    {
        using var provider = new MemoryProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        using var loggedRecognizer = new OpenCvPackRecognizer(factory.CreateLogger<OpenCvPackRecognizer>(),
            new RecordingFeeVerifier(true), new RecordingTitleReader("【独立\t卡包 标题】"));
        using var image = LoadFixture("paid-details.png");
        var frame = ToFrame(image);
        var observation = loggedRecognizer.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("独立卡包标题", observation.PackTitle);
        Assert.Equal(3, provider.Events.Count);
        var headerLog = Assert.Single(provider.Events, entry => entry.Message.StartsWith("FreePackHeaderMatched", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, headerLog.Level);
        Assert.Equal("header-secret", Assert.IsType<string>(headerLog.Properties["Category"]));
        Assert.InRange(Assert.IsType<double>(headerLog.Properties["Confidence"]), .92, 1);
        Assert.InRange(Assert.IsType<double>(headerLog.Properties["SeparatorConfidence"]), .84, 1);
        var titleLog = Assert.Single(provider.Events, entry => entry.Message.StartsWith("FreePackTitleRead", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, titleLog.Level);
        Assert.Equal("【独立\t卡包 标题】", Assert.IsType<string>(titleLog.Properties["RawText"]));
        Assert.Equal(observation.PackTitle, Assert.IsType<string>(titleLog.Properties["PackTitle"]));
        Assert.Equal(Assert.IsType<int>(headerLog.Properties["HeaderX"]) + Assert.IsType<int>(headerLog.Properties["HeaderWidth"]),
            Assert.IsType<int>(titleLog.Properties["RegionX"]));
        Assert.Equal(55, Assert.IsType<int>(titleLog.Properties["RegionY"]));
        Assert.Equal(1000, Assert.IsType<int>(titleLog.Properties["RegionWidth"]));
        Assert.Equal(90, Assert.IsType<int>(titleLog.Properties["RegionHeight"]));
        Assert.Equal(1d, Assert.IsType<double>(titleLog.Properties["AnchorScale"]));
        var log = Assert.Single(provider.Events, entry => entry.Properties.ContainsKey("Screen"));
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal(observation.Screen, Assert.IsType<PackScreen>(log.Properties["Screen"]));
        Assert.Equal(observation.Confidence, Assert.IsType<double>(log.Properties["Confidence"]));
        Assert.Equal(observation.PackTitle, Assert.IsType<string>(log.Properties["PackTitle"]));
        Assert.Contains(observation.Screen.ToString(), log.Message);
        Assert.Contains(observation.PackTitle, log.Message);
        loggedRecognizer.Dispose();
        loggedRecognizer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loggedRecognizer.Recognize(frame));
        Assert.Equal(3, provider.Events.Count);
    }

    /// <summary>模板已经确认免费入口或购买弹窗时，费用文字拒绝仍必须撤回购买坐标。</summary>
    [Theory]
    [InlineData("free-confirm.png", PackScreen.UnverifiedPurchaseDialog)]
    [InlineData("free-confirm-long-title.png", PackScreen.UnverifiedPurchaseDialog)]
    [InlineData("free-details-single-row.png", PackScreen.PackDetails)]
    [InlineData("free-details-second-title.png", PackScreen.PackDetails)]
    public void RejectedFeeEvidenceOverridesPositiveFreeTemplates(string fixture, PackScreen expectedScreen)
    {
        var verifier = new RecordingFeeVerifier(false);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var image = LoadFixture(fixture);
        var observation = subject.Recognize(ToFrame(image));
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Equal(expectedScreen, observation.Screen);
        Assert.False(Assert.Single(verifier.Calls).GemDetected);
        if (expectedScreen == PackScreen.PackDetails) Assert.NotNull(observation.NextTarget);
        else Assert.Null(observation.NextTarget);
    }

    /// <summary>费用文字只接收长标题弹窗的独立费用行 BGRA 像素，不包含右上钱包或购买按钮。</summary>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void ModalFeeVerifierReceivesOnlyTightlyPackedFeeRow(double scale, bool includeTitlebar)
    {
        var verifier = new RecordingFeeVerifier(true);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var original = LoadFixture("free-confirm-long-title.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var observation = subject.Recognize(frame);
        Assert.Equal(PackScreen.FreePurchaseDialog, observation.Screen);
        Assert.True(observation.FreeOffer);
        var fee = Assert.Single(verifier.Calls);
        Assert.False(fee.GemDetected);
        var expected = includeTitlebar ? new Rect(595, 589, 880, 59) : new Rect(594, 558, 880, 59);
        AssertCopiedFeeRegion(image, fee, expected, scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>详情费用识别仅接收同一个黄色单包按钮内的数量和免费文字，排除卡图与钱包。</summary>
    [Theory]
    [InlineData("free-details-single-row.png", 0.65)]
    [InlineData("free-details-single-row.png", 1.0)]
    [InlineData("free-details-second-title.png", 0.65)]
    [InlineData("free-details-second-title.png", 1.0)]
    public void DetailsFeeVerifierReceivesOnlyTheVerifiedSingleButton(string fixture, double scale)
    {
        var verifier = new RecordingFeeVerifier(true);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var original = LoadFixture(fixture);
        using var image = Resize(original, scale);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.True(observation.FreeOffer);
        var fee = Assert.Single(verifier.Calls);
        Assert.False(fee.GemDetected);
        AssertCopiedFeeRegion(image, fee, new Rect(1391, 902, 335, 65), scale);
    }

    /// <summary>费用区域中出现真实宝石图标时，即使文字边界错误地批准免费，也必须保留未验证状态。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void GemInsideModalBlocksPurchaseDespiteApprovedText(double scale)
    {
        var verifier = new RecordingFeeVerifier(true);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var original = LoadFixture("free-confirm-long-title.png");
        using var paid = LoadFixture("paid-confirm.png");
        using var gem = new Mat(paid, new Rect(938, 571, 80, 85));
        using var destination = new Mat(original, new Rect(965, 574, 80, 85));
        gem.CopyTo(destination);
        using var image = Resize(original, scale);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.True(Assert.Single(verifier.Calls).GemDetected);
    }

    /// <summary>游戏右上钱包即使出现与费用区完全相同的宝石图标，费用区校验也应保持明确免费。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void WalletGemOutsideModalDoesNotBlockVerifiedFreeFee(double scale)
    {
        var verifier = new RecordingFeeVerifier(true);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var original = LoadFixture("free-confirm-long-title.png");
        using var paid = LoadFixture("paid-confirm.png");
        using var gem = new Mat(paid, new Rect(938, 571, 80, 85));
        using var destination = new Mat(original, new Rect(1750, 64, 80, 85));
        gem.CopyTo(destination);
        using var image = Resize(original, scale);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.FreePurchaseDialog, observation.Screen);
        Assert.True(observation.FreeOffer);
        Assert.NotNull(observation.PrimaryTarget);
        Assert.False(Assert.Single(verifier.Calls).GemDetected);
    }

    /// <summary>缺少正向免费模板的收费详情、收费弹窗和独立按钮片段不应发起费用授权请求。</summary>
    [Theory]
    [InlineData("paid-details.png")]
    [InlineData("paid-confirm.png")]
    [InlineData("free-button.png")]
    public void ScreensWithoutFreeTemplateNeverRequestFeeAuthorization(string fixture)
    {
        var verifier = new RecordingFeeVerifier(true);
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier,
            titleReader: new RecordingTitleReader("独立卡包标题"));
        using var image = LoadFixture(fixture);
        var observation = subject.Recognize(ToFrame(image));
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Empty(verifier.Calls);
    }

    /// <summary>默认构造必须以真实 Windows 中文 OCR 验证真实免费截图，并在真实宝石弹窗撤回购买。</summary>
    [Theory]
    [InlineData("free-details-single-row.png", PackScreen.PackDetails, true, 0.65)]
    [InlineData("free-details-single-row.png", PackScreen.PackDetails, true, 0.85)]
    [InlineData("free-details-single-row.png", PackScreen.PackDetails, true, 1.0)]
    [InlineData("free-details-single-row.png", PackScreen.PackDetails, true, 1.25)]
    [InlineData("free-details-second-title.png", PackScreen.PackDetails, true, 0.65)]
    [InlineData("free-details-second-title.png", PackScreen.PackDetails, true, 0.85)]
    [InlineData("free-details-second-title.png", PackScreen.PackDetails, true, 1.0)]
    [InlineData("free-details-second-title.png", PackScreen.PackDetails, true, 1.25)]
    [InlineData("free-confirm.png", PackScreen.FreePurchaseDialog, true, 0.65)]
    [InlineData("free-confirm.png", PackScreen.FreePurchaseDialog, true, 0.85)]
    [InlineData("free-confirm.png", PackScreen.FreePurchaseDialog, true, 1.0)]
    [InlineData("free-confirm.png", PackScreen.FreePurchaseDialog, true, 1.25)]
    [InlineData("free-confirm-long-title.png", PackScreen.FreePurchaseDialog, true, 0.65)]
    [InlineData("free-confirm-long-title.png", PackScreen.FreePurchaseDialog, true, 0.85)]
    [InlineData("free-confirm-long-title.png", PackScreen.FreePurchaseDialog, true, 1.0)]
    [InlineData("free-confirm-long-title.png", PackScreen.FreePurchaseDialog, true, 1.25)]
    [InlineData("paid-confirm.png", PackScreen.UnverifiedPurchaseDialog, false, 0.65)]
    [InlineData("paid-confirm.png", PackScreen.UnverifiedPurchaseDialog, false, 0.85)]
    [InlineData("paid-confirm.png", PackScreen.UnverifiedPurchaseDialog, false, 1.0)]
    [InlineData("paid-confirm.png", PackScreen.UnverifiedPurchaseDialog, false, 1.25)]
    public void DefaultWindowsOcrVerifiesRealFreeFeeAndRejectsPaidModal(string fixture, PackScreen expected,
        bool freeOffer, double scale)
    {
        using var subject = new OpenCvPackRecognizer(titleReader: new RecordingTitleReader("独立卡包标题"));
        using var original = LoadFixture(fixture);
        using var image = Resize(original, scale);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(expected, observation.Screen);
        Assert.Equal(freeOffer, observation.FreeOffer);
        if (freeOffer) Assert.NotNull(observation.PrimaryTarget);
        else Assert.Null(observation.PrimaryTarget);
        AssertConfidence(observation);
    }

    /// <summary>详情文字没有有效标题时，即使详情菜单和导航仍在，也不得建立卡包身份或动作。</summary>
    /// <param name="rawTitle">标题 OCR 返回的空白或标点文字。</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n　")]
    [InlineData("【】?!・。，—")]
    [InlineData("💎🙂＋＝")]
    public void DetailsWithoutLetterOrDigitTitleDoNotAuthorizeActions(string rawTitle)
    {
        var reader = new RecordingTitleReader(rawTitle);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var image = LoadFixture("paid-details-after-opening.png");
        AssertUnknown(subject.Recognize(ToFrame(image)));
        Assert.Single(reader.Calls);
    }

    /// <summary>标题须按兼容规范化保留全部 Unicode 字母及数字，移除空白标点并保留稀有中文代理对。</summary>
    /// <param name="rawTitle">标题 OCR 返回的原始文字。</param>
    /// <param name="expectedTitle">独立指定的精确规范化标题。</param>
    [Theory]
    [InlineData("  颠 覆\t世 界\r\n恶 魔 之 力。", "颠覆世界恶魔之力")]
    [InlineData("ＡＢＣ１２３-卡包 #Ⅳ", "ABC123卡包IV")]
    [InlineData("【𠮷 𠀀】魔・神＋２０２６", "𠮷𠀀魔神2026")]
    [InlineData("Pack Ａ-2（免费?）", "PackA2免费")]
    [InlineData("Ｃafé・卡包１２", "Café卡包12")]
    [InlineData("丨魔神", "丨魔神")]
    public void DetailsNormalizeCompleteUnicodeTitleExactly(string rawTitle, string expectedTitle)
    {
        var reader = new RecordingTitleReader(rawTitle);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var image = LoadFixture("paid-details-after-opening.png");
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(expectedTitle, observation.PackTitle);
        Assert.Single(reader.Calls);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
        AssertFingerprint(observation.Fingerprint);
    }

    /// <summary>相同真实像素产生相同图像指纹时，不同的近似文字标题仍须提供不同的精确卡包身份。</summary>
    /// <param name="firstTitle">第一个标题 OCR 结果。</param>
    /// <param name="secondTitle">具有相似字形但身份不同的第二个标题。</param>
    [Theory]
    [InlineData("颠覆世界恶魔之力", "颠覆世界恶魔之刃")]
    [InlineData("免费魔神", "免费魔王")]
    [InlineData("PackA2", "PackA3")]
    [InlineData("PackA2", "Packa2")]
    [InlineData("𠮷魔神", "𠀀魔神")]
    [InlineData("丨魔神", "魔神")]
    public void SimilarTitlesRemainDistinctDespiteIdenticalRealImageHash(string firstTitle, string secondTitle)
    {
        using var firstSubject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true),
            titleReader: new RecordingTitleReader(firstTitle));
        using var secondSubject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true),
            titleReader: new RecordingTitleReader(secondTitle));
        using var image = LoadFixture("paid-details-after-opening.png");
        var frame = ToFrame(image);
        var first = firstSubject.Recognize(frame);
        var second = secondSubject.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(PackScreen.PackDetails, second.Screen);
        AssertFingerprint(first.Fingerprint);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(firstTitle, first.PackTitle);
        Assert.Equal(secondTitle, second.PackTitle);
        Assert.NotEqual(first.PackTitle, second.PackTitle);
        Assert.NotNull(first.NextTarget);
        Assert.NotNull(second.NextTarget);
        Assert.Null(first.AnimationSkipTarget);
        Assert.Null(second.AnimationSkipTarget);
    }

    /// <summary>标题 OCR 只接收包含完整真实标题字形的紧密 BGRA 区域，排除左侧类别文字和右侧钱包。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void DetailsTitleReaderReceivesCompleteIndependentTitlePixels(double scale, bool includeTitlebar)
    {
        var reader = new RecordingTitleReader("颠覆世界恶魔之力");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var original = LoadFixture("paid-details-after-opening.png");
        using var input = includeTitlebar ? original.Clone() : Crop(original, new Rect(1, 31, 2048, 1152));
        using var image = Resize(input, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var observation = subject.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("颠覆世界恶魔之力", observation.PackTitle);
        AssertCopiedTitleRegion(image, Assert.Single(reader.Calls), includeTitlebar, scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>现场开包返回帧在各缩放下须精确读取猛火魔兽，左侧界面分隔线不成为标题文字。</summary>
    /// <param name="scale">客户区现场截图的缩放比例。</param>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void DefaultTitleOcrExcludesSeparatorFromRealFireBeastDetails(double scale)
    {
        using var subject = new OpenCvPackRecognizer();
        using var original = LoadFixture("paid-details-fire-beast-after-opening.png");
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var observation = subject.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("猛火魔兽", observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
        AssertFingerprint(observation.Fingerprint);
        Assert.InRange(observation.Confidence, .84, 1);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>现场标题传给OCR的真实像素排除左分隔线，完整包含首末字且不改动输入帧。</summary>
    /// <param name="scale">客户区现场截图的缩放比例。</param>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void FireBeastTitlePixelsExcludeSeparatorAndKeepCompleteGlyphs(double scale)
    {
        var reader = new RecordingTitleReader("猛火魔兽");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var original = LoadFixture("paid-details-fire-beast-after-opening.png");
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var observation = subject.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("猛火魔兽", observation.PackTitle);
        AssertCopiedFireBeastTitleRegion(frame, Assert.Single(reader.Calls), scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>现场顶部留白不足的免费详情在各缩放下仍须读取完整真实标题并提供免费入口及下一包。</summary>
    /// <param name="scale">客户区现场截图的缩放比例。</param>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void TopEdgeFreeDetailsUseRealOcrAndAuthorizeCompleteTitle(double scale)
    {
        using var subject = new OpenCvPackRecognizer();
        using var original = LoadFixture("free-details-top-edge.png");
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var observation = subject.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("黑之魔导师", observation.PackTitle);
        Assert.True(observation.FreeOffer);
        Assert.NotNull(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        AssertFingerprint(observation.Fingerprint);
        Assert.InRange(observation.Confidence, .84, 1);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>裁去可选顶部留白而完整保留标题时，系统OCR仍须精确读取收费卡包身份且不授权购买。</summary>
    [Fact]
    public void TopEdgeDetailsWithoutOptionalPaddingKeepCompletePaidTitle()
    {
        using var subject = new OpenCvPackRecognizer();
        using var original = LoadFixture("paid-details-after-opening.png");
        using var image = Crop(original, new Rect(0, 66, 2050, 1118));
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("颠覆世界恶魔之力", observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
    }

    /// <summary>实际裁掉标题字形和必需核心时，应等待完整画面且不向OCR提供残缺标题。</summary>
    [Fact]
    public void DetailsWithCroppedRequiredTitleDoNotReadOrAuthorizePartialTitle()
    {
        var reader = new RecordingTitleReader("颠覆世界恶魔之力");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var original = LoadFixture("paid-details-after-opening.png");
        using var image = Crop(original, new Rect(0, 100, 2050, 1084));
        AssertUnknown(subject.Recognize(ToFrame(image)));
        Assert.Empty(reader.Calls);
    }

    /// <summary>标题读取故障应传播同一异常并停止识别，调用方像素保持原样。</summary>
    [Fact]
    public void TitleReaderFailurePropagatesWithoutProducingAnObservation()
    {
        var failure = new InvalidOperationException("测试标题 OCR 故障");
        var reader = new RecordingTitleReader("故障没有文字结果", failure);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var image = LoadFixture("paid-details-after-opening.png");
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var thrown = Assert.Throws<InvalidOperationException>(() => subject.Recognize(frame));
        Assert.Same(failure, thrown);
        Assert.Single(reader.Calls);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>真实系统标题 OCR 在整窗和客户区各缩放下，应为同一包的收费及免费详情返回相同完整标题。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void DefaultTitleOcrKeepsSamePackAcrossFreeOfferAndAnimation(double scale, bool includeTitlebar)
    {
        using var subject = new OpenCvPackRecognizer();
        using var paidOriginal = LoadFixture("paid-details-after-opening.png");
        using var freeOriginal = LoadFixture("free-details-single-row.png");
        using var paidInput = includeTitlebar ? paidOriginal.Clone() : Crop(paidOriginal, new Rect(1, 31, 2048, 1152));
        using var freeInput = includeTitlebar ? freeOriginal.Clone() : Crop(freeOriginal, new Rect(1, 31, 2048, 1152));
        using var paidImage = Resize(paidInput, scale);
        using var freeImage = Resize(freeInput, scale);
        var paid = subject.Recognize(ToFrame(paidImage));
        var free = subject.Recognize(ToFrame(freeImage));
        Assert.Equal(PackScreen.PackDetails, paid.Screen);
        Assert.Equal(PackScreen.PackDetails, free.Screen);
        Assert.Equal("颠覆世界恶魔之力", paid.PackTitle);
        Assert.Equal(paid.PackTitle, free.PackTitle);
        Assert.False(paid.FreeOffer);
        Assert.True(free.FreeOffer);
        Assert.Null(paid.PrimaryTarget);
        Assert.NotNull(free.PrimaryTarget);
        Assert.NotNull(paid.NextTarget);
        Assert.NotNull(free.NextTarget);
        Assert.Null(paid.AnimationSkipTarget);
        Assert.Null(free.AnimationSkipTarget);
    }

    /// <summary>真实系统标题 OCR 在不同卡包之间必须建立精确区分，第三标题不能与原收费或开包后标题混同。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="includeTitlebar">是否保留截图的窗口标题栏。</param>
    [Theory]
    [InlineData(0.65, true)]
    [InlineData(0.85, true)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(0.65, false)]
    [InlineData(0.85, false)]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    public void DefaultTitleOcrDistinguishesAllThreeRealPacks(double scale, bool includeTitlebar)
    {
        using var subject = new OpenCvPackRecognizer();
        using var firstOriginal = LoadFixture("paid-details.png");
        using var secondOriginal = LoadFixture("paid-details-after-opening.png");
        using var thirdOriginal = LoadFixture("free-details-second-title.png");
        using var firstInput = includeTitlebar ? firstOriginal.Clone() : Crop(firstOriginal, new Rect(1, 31, 2048, 1152));
        using var secondInput = includeTitlebar ? secondOriginal.Clone() : Crop(secondOriginal, new Rect(1, 31, 2048, 1152));
        using var thirdInput = includeTitlebar ? thirdOriginal.Clone() : Crop(thirdOriginal, new Rect(1, 31, 2048, 1152));
        using var firstImage = Resize(firstInput, scale);
        using var secondImage = Resize(secondInput, scale);
        using var thirdImage = Resize(thirdInput, scale);
        var first = subject.Recognize(ToFrame(firstImage));
        var second = subject.Recognize(ToFrame(secondImage));
        var third = subject.Recognize(ToFrame(thirdImage));
        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(PackScreen.PackDetails, second.Screen);
        Assert.Equal(PackScreen.PackDetails, third.Screen);
        Assert.False(string.IsNullOrEmpty(first.PackTitle));
        Assert.Equal("颠覆世界恶魔之力", second.PackTitle);
        Assert.Equal("于毁灭中觉醒", third.PackTitle);
        Assert.NotEqual(first.PackTitle, second.PackTitle);
        Assert.NotEqual(first.PackTitle, third.PackTitle);
        Assert.NotEqual(second.PackTitle, third.PackTitle);
        Assert.NotNull(first.NextTarget);
        Assert.NotNull(second.NextTarget);
        Assert.NotNull(third.NextTarget);
        Assert.Null(first.AnimationSkipTarget);
        Assert.Null(second.AnimationSkipTarget);
        Assert.Null(third.AnimationSkipTarget);
    }

    /// <summary>未知、结果及购买弹窗均不应读取卡包标题或提供动画跳过动作；开包只允许提供已验证动画动作。</summary>
    /// <param name="fixture">独立真实截图名称。</param>
    /// <param name="expected">截图应识别的界面状态。</param>
    [Theory]
    [InlineData("free-button.png", PackScreen.Unknown)]
    [InlineData("free-confirm.png", PackScreen.FreePurchaseDialog)]
    [InlineData("free-confirm-long-title.png", PackScreen.FreePurchaseDialog)]
    [InlineData("paid-confirm.png", PackScreen.UnverifiedPurchaseDialog)]
    [InlineData("opening.png", PackScreen.Opening)]
    [InlineData("opening-full-window.png", PackScreen.Opening)]
    [InlineData("results.png", PackScreen.Results)]
    [InlineData("results-secret-sidebar.png", PackScreen.Results)]
    public void NonDetailsScreensNeverReadOrPublishPackTitle(string fixture, PackScreen expected)
    {
        var reader = new RecordingTitleReader("错误区域不能成为标题");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(true), titleReader: reader);
        using var image = LoadFixture(fixture);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(expected, observation.Screen);
        Assert.Empty(reader.Calls);
        Assert.Empty(observation.PackTitle);
        if (expected == PackScreen.Opening)
        {
            Assert.NotNull(observation.AnimationSkipTarget);
            Assert.Equal(observation.PrimaryTarget, observation.AnimationSkipTarget);
        }
        else Assert.Null(observation.AnimationSkipTarget);
    }

    /// <summary>打开文字仍完整但跳过按钮位于裁切帧右侧或下方时，主要动作仍为打开且动画坐标必须为空。</summary>
    /// <param name="scale">真实图像的缩放比例。</param>
    /// <param name="width">保留的原图宽度。</param>
    /// <param name="height">保留的原图高度。</param>
    [Theory]
    [InlineData(0.65, 1100, 1184)]
    [InlineData(0.85, 1100, 1184)]
    [InlineData(1.0, 1100, 1184)]
    [InlineData(1.25, 1100, 1184)]
    [InlineData(0.65, 2050, 1060)]
    [InlineData(0.85, 2050, 1060)]
    [InlineData(1.0, 2050, 1060)]
    [InlineData(1.25, 2050, 1060)]
    public void OpeningWithSkipOutsideCroppedFrameHasNoAnimationTarget(double scale, int width, int height)
    {
        using var original = LoadFixture("opening-full-window.png");
        using var input = Crop(original, new Rect(0, 0, width, height));
        using var image = Resize(input, scale);
        var observation = recognizer.Recognize(ToFrame(image));
        Assert.Equal(PackScreen.Opening, observation.Screen);
        AssertTargetInside(observation.PrimaryTarget, new Rect(962, 984, 119, 48), scale, image);
        Assert.Null(observation.AnimationSkipTarget);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.PackTitle);
    }

    /// <summary>预热后记录四种真实界面各十帧的识别耗时，并核验调用方像素不变，不设置机器相关时间门槛。</summary>
    [Fact]
    public void FourScreenProfileRecordsTenFramesWithoutTimingThreshold()
    {
        string[] fixtures = ["paid-details.png", "free-confirm-long-title.png", "opening-full-window.png", "results-secret-sidebar.png"];
        foreach (var fixture in fixtures)
        {
            using var image = LoadFixture(fixture);
            var frame = ToFrame(image);
            var before = frame.Pixels.ToArray();
            for (var warmup = 0; warmup < 2; warmup++) recognizer.Recognize(frame);
            var watch = Stopwatch.StartNew();
            PackScreen lastScreen = PackScreen.Unknown;
            for (var index = 0; index < 10; index++) lastScreen = recognizer.Recognize(frame).Screen;
            watch.Stop();
            Assert.Equal(before, frame.Pixels);
            output.WriteLine("{0}: warmup=2 frames=10 totalMs={1:F2} meanMs={2:F2} lastScreen={3}", fixture,
                watch.Elapsed.TotalMilliseconds, watch.Elapsed.TotalMilliseconds / 10, lastScreen);
        }
    }

    /// <summary>独立定位一千像素宽的真实标题上下文矩形并逐行比较 BGRA，完整包含字形且排除类别与钱包。</summary>
    /// <param name="image">真实缩放后的源截图。</param>
    /// <param name="title">被测识别器实际交给文字边界的区域。</param>
    /// <param name="includeTitlebar">源截图是否包含标题栏。</param>
    /// <param name="scale">源截图的缩放比例。</param>
    private static void AssertCopiedTitleRegion(Mat image, TitleCall title, bool includeTitlebar, double scale)
    {
        Assert.Equal(title.Width * title.Height * 4, title.Pixels.Length);
        var tolerance = (int)Math.Ceiling(12 * scale);
        var width1000 = (int)Math.Round(1000 * scale);
        // 类别锚点允许窗口参考尺度的±4%候选；宽度随实际联合锚点变化，完整字形仍逐像素核验。
        var widthTolerance = (int)Math.Ceiling(42 * scale);
        Assert.InRange(title.Width, width1000 - widthTolerance, width1000 + widthTolerance);
        Assert.InRange(title.Height, (int)Math.Round(90 * scale) - tolerance, (int)Math.Round(90 * scale) + tolerance);
        var expectedX = (int)Math.Round((includeTitlebar ? 284 : 283) * scale);
        var expectedY = (int)Math.Round((includeTitlebar ? 55 : 24) * scale);
        var source = ToFrame(image).Pixels;
        var imageWidth = image.Width;
        var imageHeight = image.Height;
        Rect? actualRegion = null;
        for (var y = Math.Max(0, expectedY - tolerance); y <= Math.Min(imageHeight - title.Height, expectedY + tolerance) && actualRegion is null; y++)
        {
            for (var x = Math.Max(0, expectedX - tolerance); x <= Math.Min(imageWidth - title.Width, expectedX + tolerance); x++)
            {
                var allRowsMatch = true;
                for (var row = 0; row < title.Height && allRowsMatch; row++)
                    allRowsMatch = source.AsSpan(((y + row) * imageWidth + x) * 4, title.Width * 4)
                        .SequenceEqual(title.Pixels.AsSpan(row * title.Width * 4, title.Width * 4));
                if (!allRowsMatch) continue;
                actualRegion = new Rect(x, y, title.Width, title.Height);
                break;
            }
        }
        Assert.True(actualRegion.HasValue, "标题像素必须逐行来自真实截图的标题专属区域。");
        var region = actualRegion.GetValueOrDefault();
        var glyphs = includeTitlebar ? new Rect(290, 85, 254, 30) : new Rect(289, 54, 254, 30);
        Assert.True(region.Contains(new Point((int)Math.Floor(glyphs.Left * scale), (int)Math.Floor(glyphs.Top * scale))));
        Assert.True(region.Contains(new Point((int)Math.Ceiling(glyphs.Right * scale) - 1, (int)Math.Ceiling(glyphs.Bottom * scale) - 1)));
    }

    /// <summary>逐行定位现场标题的真实复制区域，核验分隔线排除与所有标题字形的完整边界。</summary>
    /// <param name="frame">持有现场缩放图像完整BGRA像素的输入帧。</param>
    /// <param name="title">标题识别器实际收到的独立像素区域。</param>
    /// <param name="scale">现场图像的缩放比例。</param>
    private static void AssertCopiedFireBeastTitleRegion(GameFrame frame, TitleCall title, double scale)
    {
        Assert.Equal(title.Width * title.Height * 4, title.Pixels.Length);
        Rect? actualRegion = null;
        for (int y = 0; y <= Math.Min(frame.Height - title.Height, (int)Math.Ceiling(30 * scale)) && actualRegion is null; y++)
        {
            for (int x = (int)Math.Floor(200 * scale); x <= Math.Min(frame.Width - title.Width, (int)Math.Ceiling(300 * scale)); x++)
            {
                var allRowsMatch = true;
                for (int row = 0; row < title.Height && allRowsMatch; row++)
                    allRowsMatch = frame.Pixels.AsSpan(((y + row) * frame.Width + x) * 4, title.Width * 4)
                        .SequenceEqual(title.Pixels.AsSpan(row * title.Width * 4, title.Width * 4));
                if (!allRowsMatch) continue;
                actualRegion = new Rect(x, y, title.Width, title.Height);
                break;
            }
        }
        Assert.True(actualRegion.HasValue, "标题像素必须逐行来自现场截图的真实独立区域。");
        var region = actualRegion.GetValueOrDefault();
        Assert.True(region.Left >= (int)Math.Ceiling(248 * scale), "OCR区域必须排除现场左侧界面分隔线。");
        Assert.True(region.Contains(new Point((int)Math.Floor(267 * scale), (int)Math.Floor(48 * scale))),
            "标题区域必须完整保留首字及标题顶部。");
        Assert.True(region.Contains(new Point((int)Math.Ceiling(391 * scale) - 1, (int)Math.Ceiling(78 * scale) - 1)),
            "标题区域必须完整保留末字及标题底部。");
    }

    /// <summary>核验费用像素是指定原图局部逐行复制，尺寸和坐标容差只允许模板缩放插值的像素取整。</summary>
    private static void AssertCopiedFeeRegion(Mat original, FeeCall fee, Rect expected, double scale)
    {
        Assert.Equal(fee.Width * fee.Height * 4, fee.Pixels.Length);
        var tolerance = (int)Math.Ceiling(12 * scale);
        Assert.InRange(fee.Width, (int)Math.Round(expected.Width * scale) - tolerance,
            (int)Math.Round(expected.Width * scale) + tolerance);
        Assert.InRange(fee.Height, (int)Math.Round(expected.Height * scale) - tolerance,
            (int)Math.Round(expected.Height * scale) + tolerance);
        var source = ToFrame(original).Pixels;
        var imageWidth = original.Width;
        var imageHeight = original.Height;
        var expectedX = (int)Math.Round(expected.X * scale);
        var expectedY = (int)Math.Round(expected.Y * scale);
        var matched = false;
        for (var y = Math.Max(0, expectedY - tolerance); y <= Math.Min(imageHeight - fee.Height, expectedY + tolerance); y++)
        {
            for (var x = Math.Max(0, expectedX - tolerance); x <= Math.Min(imageWidth - fee.Width, expectedX + tolerance); x++)
            {
                var allRowsMatch = true;
                for (var row = 0; row < fee.Height && allRowsMatch; row++)
                    allRowsMatch = source.AsSpan(((y + row) * imageWidth + x) * 4, fee.Width * 4)
                        .SequenceEqual(fee.Pixels.AsSpan(row * fee.Width * 4, fee.Width * 4));
                matched |= allRowsMatch;
            }
        }
        Assert.True(matched, "费用像素必须逐行来自真实截图的限定费用区域。");
    }

    /// <summary>一次费用校验接收到的独立紧密 BGRA 区域和费用宝石标志。</summary>
    /// <param name="Pixels">被测识别器复制的区域像素。</param>
    /// <param name="Width">局部区域宽度。</param>
    /// <param name="Height">局部区域高度。</param>
    /// <param name="GemDetected">限定费用区是否识别宝石图标。</param>
    private sealed record FeeCall(byte[] Pixels, int Width, int Height, bool GemDetected);

    /// <summary>隔离系统 OCR，并记录费用区域和宝石门控的调用证据。</summary>
    /// <param name="approved">模拟费用文字边界是否确认免费。</param>
    private sealed class RecordingFeeVerifier(bool approved) : IPackFeeVerifier
    {
        /// <summary>每一次局部费用授权请求，保留独立像素以检查原图边界。</summary>
        public List<FeeCall> Calls { get; } = [];

        /// <summary>记录实际费用区像素及宝石标志，并返回固定的费用证据结果。</summary>
        public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected)
        {
            Calls.Add(new(bgraPixels.ToArray(), width, height, gemDetected));
            return approved;
        }
    }

    /// <summary>一次标题识别收到的紧密 BGRA 字节和实际区域尺寸。</summary>
    /// <param name="Pixels">标题区域的独立像素副本。</param>
    /// <param name="Width">区域的物理像素宽度。</param>
    /// <param name="Height">区域的物理像素高度。</param>
    private sealed record TitleCall(byte[] Pixels, int Width, int Height);

    /// <summary>隔离系统标题 OCR，并保留真实标题区域供独立字节验证。</summary>
    /// <param name="text">标题识别返回的原始文字。</param>
    /// <param name="failure">识别边界应传播的可控故障，省略时返回标题文字。</param>
    private sealed class RecordingTitleReader(string text, Exception? failure = null) : IPackTextReader
    {
        /// <summary>实际收到的标题区域，按识别调用顺序保留。</summary>
        public List<TitleCall> Calls { get; } = [];

        /// <summary>记录标题区域的紧密 BGRA 副本，并返回指定的原始文字。</summary>
        /// <param name="bgraPixels">被测识别器传入的标题区域字节。</param>
        /// <param name="width">实际区域宽度。</param>
        /// <param name="height">实际区域高度。</param>
        /// <returns>当前测试指定的原始标题文字。</returns>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            Calls.Add(new(bgraPixels.ToArray(), width, height));
            if (failure is not null) throw failure;
            return text;
        }
    }

    /// <summary>复制真实截图的指定矩形，确保所得测试帧拥有独立且连续的 BGRA 内存。</summary>
    private static Mat Crop(Mat original, Rect region)
    {
        using var cropped = new Mat(original, region);
        return cropped.Clone();
    }

    /// <summary>从测试程序集的嵌入资源读取原始 PNG，并用真实 OpenCV 解码为 BGRA。</summary>
    private static Mat LoadFixture(string name)
    {
        using var stream = typeof(FreePackVisionTests).Assembly.GetManifestResourceStream(
            $"MasterDuelSwitcher.Tests.Assets.PackFrames.{name}");
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var bgr = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
        Assert.False(bgr.Empty());
        var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    /// <summary>通过真实图像缩放改变输入分辨率，缩小时使用面积插值、放大时使用线性插值。</summary>
    private static Mat Resize(Mat original, double scale)
    {
        var resized = new Mat();
        var size = new Size((int)Math.Round(original.Width * scale), (int)Math.Round(original.Height * scale));
        Cv2.Resize(original, resized, size, 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return resized;
    }

    /// <summary>用真实 OpenCV 将 RGB 亮度乘以指定比例，并保持测试帧的透明通道完全不透明。</summary>
    private static Mat DarkenRgb(Mat original, double factor)
    {
        using var bgr = new Mat();
        Cv2.CvtColor(original, bgr, ColorConversionCodes.BGRA2BGR);
        using var darkenedBgr = new Mat();
        bgr.ConvertTo(darkenedBgr, MatType.CV_8UC3, factor);
        var bgra = new Mat();
        Cv2.CvtColor(darkenedBgr, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    /// <summary>将原截图中的免费按钮贴到收费详情的单包入口，不生成任何文字或状态锚点。</summary>
    private static Mat CreateFreeDetails()
    {
        var details = LoadFixture("paid-details.png");
        using var originalButton = LoadFixture("free-button.png");
        using var button = new Mat(originalButton, new Rect(15, 29, 491, 78));
        using var destination = new Mat(details, new Rect(1312, 825, 491, 78));
        button.CopyTo(destination);
        return details;
    }

    /// <summary>使用图中附近的真实背景像素遮掉指定内容，保留区域之外全部原始像素。</summary>
    private static void PaintFromNearbyPixel(Mat image, Rect region, int sampleX, int sampleY)
    {
        var pixel = image.At<Vec4b>(sampleY, sampleX);
        using var destination = new Mat(image, region);
        destination.SetTo(new Scalar(pixel.Item0, pixel.Item1, pixel.Item2, pixel.Item3));
    }

    /// <summary>复制连续 BGRA 像素构造独立测试帧，使被测识别器不会持有临时 Mat 内存。</summary>
    private static GameFrame ToFrame(Mat image)
    {
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.True(image.IsContinuous());
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(1, image.Width, image.Height, 0, 0, pixels, capturedAtUtc);
    }

    /// <summary>验证未知画面没有任何可点击目标、免费标志或详情指纹。</summary>
    private static void AssertUnknown(PackObservation observation)
    {
        Assert.Equal(PackScreen.Unknown, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Empty(observation.Fingerprint);
        Assert.Empty(observation.PackTitle);
        Assert.Null(observation.AnimationSkipTarget);
        Assert.InRange(observation.Confidence, 0, 1);
    }

    /// <summary>验证动作点落在独立指定的原图按钮区域内，并满足缩放后帧的排他边界。</summary>
    private static void AssertTargetInside(PixelPoint? target, Rect originalRegion, double scale, Mat image)
    {
        Assert.True(target.HasValue);
        var point = Assert.IsType<PixelPoint>(target);
        Assert.InRange(point.X, 0, image.Width - 1);
        Assert.InRange(point.Y, 0, image.Height - 1);
        Assert.InRange(point.X, (int)Math.Floor(originalRegion.Left * scale),
            (int)Math.Ceiling(originalRegion.Right * scale) - 1);
        Assert.InRange(point.Y, (int)Math.Floor(originalRegion.Top * scale),
            (int)Math.Ceiling(originalRegion.Bottom * scale) - 1);
    }

    /// <summary>验证已识别状态的归一化置信度为有限正数，且不超过完整匹配分数。</summary>
    private static void AssertConfidence(PackObservation observation)
    {
        Assert.True(double.IsFinite(observation.Confidence));
        Assert.InRange(observation.Confidence, double.Epsilon, 1);
    }

    /// <summary>验证详情指纹是可计算位距离的六十四位十六进制字符串。</summary>
    private static void AssertFingerprint(string fingerprint)
    {
        Assert.Matches("\\A[0-9a-fA-F]{16}\\z", fingerprint);
    }

    /// <summary>计算两个六十四位卡包标题指纹的真实位汉明距离，允许缩放插值产生少量位差。</summary>
    private static int HammingDistance(string first, string second) =>
        BitOperations.PopCount(Convert.ToUInt64(first, 16) ^ Convert.ToUInt64(second, 16));

    /// <summary>每个测试完成后释放默认识别器持有的原生模板图像。</summary>
    public void Dispose() => recognizer.Dispose();

    /// <summary>保存真实日志工厂输出的级别、格式化内容和结构化字段。</summary>
    /// <param name="Level">实际输出的日志级别。</param>
    /// <param name="Message">真实格式化器产生的消息。</param>
    /// <param name="Properties">状态模板中保留的原始字段值。</param>
    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);

    /// <summary>接入真实日志工厂并在本测试内存中保留诊断事件的提供程序。</summary>
    private sealed class MemoryProvider : ILoggerProvider, ILogger
    {
        /// <summary>本测试实际收到的日志事件，按输出顺序保存。</summary>
        private readonly List<LogEntry> events = [];

        /// <summary>供断言读取的实际日志事件集合。</summary>
        internal IReadOnlyList<LogEntry> Events => events;

        /// <summary>返回由真实工厂过滤和调用的内存记录器。</summary>
        public ILogger CreateLogger(string categoryName) => this;

        /// <summary>接收调试及以上诊断级别，配合工厂的真实级别过滤。</summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        /// <summary>识别诊断没有范围状态，该记录器无需分配范围对象。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>调用真实格式化器并保存结构化状态字段，避免断言绑定整段消息文案。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            events.Add(new(logLevel, formatter(state, exception), properties));
        }

        /// <summary>提供程序仅持有托管事件，工厂或测试重复释放均不删除诊断证据。</summary>
        public void Dispose() { }
    }
}
