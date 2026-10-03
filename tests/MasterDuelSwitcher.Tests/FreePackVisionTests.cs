using System.Numerics;
using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用真实游戏截图和真实 OpenCV 图像处理验证免费卡包识别及动作边界。</summary>
public sealed class FreePackVisionTests : IDisposable
{
    /// <summary>将费用文字边界隔离后的真实 OpenCV 截图识别器。</summary>
    private readonly OpenCvPackRecognizer recognizer = new(feeVerifier: new RecordingFeeVerifier(true));

    /// <summary>测试帧使用的固定捕获时间，避免断言依赖系统时钟。</summary>
    private static readonly DateTimeOffset capturedAtUtc = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>显式日志应记录实际状态及置信度；重复释放后再次识别须在原生访问前拒绝。</summary>
    [Fact]
    public void ExplicitDebugLoggerRecordsObservationAndDisposalIsIdempotent()
    {
        using var provider = new MemoryProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        using var loggedRecognizer = new OpenCvPackRecognizer(factory.CreateLogger<OpenCvPackRecognizer>(), new RecordingFeeVerifier(true));
        using var image = LoadFixture("paid-details.png");
        var frame = ToFrame(image);
        var observation = loggedRecognizer.Recognize(frame);
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        var log = Assert.Single(provider.Events);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.Equal(observation.Screen, Assert.IsType<PackScreen>(log.Properties["Screen"]));
        Assert.Equal(observation.Confidence, Assert.IsType<double>(log.Properties["Confidence"]));
        Assert.Contains(observation.Screen.ToString(), log.Message);
        loggedRecognizer.Dispose();
        loggedRecognizer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loggedRecognizer.Recognize(frame));
        Assert.Single(provider.Events);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer(feeVerifier: verifier);
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
        using var subject = new OpenCvPackRecognizer();
        using var original = LoadFixture(fixture);
        using var image = Resize(original, scale);
        var observation = subject.Recognize(ToFrame(image));
        Assert.Equal(expected, observation.Screen);
        Assert.Equal(freeOffer, observation.FreeOffer);
        if (freeOffer) Assert.NotNull(observation.PrimaryTarget);
        else Assert.Null(observation.PrimaryTarget);
        AssertConfidence(observation);
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
