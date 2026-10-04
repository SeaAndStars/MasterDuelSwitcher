using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>用真实截图验证模板定位缓存保持动作证据、费用复核和几何失效行为。</summary>
public sealed class PackTemplateLocalizationCacheTests
{
    /// <summary>所有离线帧采用的固定捕获时间。</summary>
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 4, 14, 0, 0, TimeSpan.Zero);

    /// <summary>枚举四类真实动作界面在四档缩放下的冷暖定位一致性。</summary>
    /// <returns>真实截图资源、预期界面、免费标志及物理缩放比例。</returns>
    public static IEnumerable<object[]> WarmScreenCases()
    {
        (string File, PackScreen Screen, bool Free)[] fixtures =
        [
            ("free-details-single-row.png", PackScreen.PackDetails, true),
            ("free-confirm-long-title.png", PackScreen.FreePurchaseDialog, true),
            ("opening-full-window.png", PackScreen.Opening, false),
            ("results-secret-sidebar.png", PackScreen.Results, false)
        ];
        foreach (var fixture in fixtures)
            foreach (var scale in new[] { .65, .85, 1, 1.25 })
                yield return [fixture.File, fixture.Screen, fixture.Free, scale];
    }

    /// <summary>真实详情、购买、开包和结果在暖缓存中仍返回相同动作，并报告实际局部单尺度命中。</summary>
    /// <param name="fixture">真实截图资源名。</param>
    /// <param name="screen">原图已经建立的实际界面状态。</param>
    /// <param name="free">原图是否具有经独立费用复核确认的免费动作。</param>
    /// <param name="scale">真实截图的物理缩放比例。</param>
    [Theory]
    [MemberData(nameof(WarmScreenCases))]
    public void WarmLocalizationKeepsTheColdScreenAndCoordinates(string fixture, PackScreen screen, bool free, double scale)
    {
        using var original = LoadFrame(fixture);
        using var image = Resize(original, scale);
        var logger = new CacheLogger();
        var feeReader = new CountingTextReader("1次免费");
        using var subject = new OpenCvPackRecognizer(logger, new OcrFreePackCostVerifier(feeReader),
            new CountingTextReader("颠覆世界恶魔之力"));
        var frame = ToFrame(image);

        var cold = subject.Recognize(frame);
        logger.Entries.Clear();
        var warm = subject.Recognize(frame);

        Assert.Equal(screen, cold.Screen);
        Assert.Equal(free, cold.FreeOffer);
        Assert.NotNull(cold.PrimaryTarget);
        AssertSameActions(cold, warm);
        Assert.NotEmpty(logger.Events("FreePackTemplateCacheHit"));
        Assert.Single(logger.Events("FreePackRecognitionTiming"));
        foreach (var hit in logger.Events("FreePackTemplateCacheHit"))
        {
            Assert.Equal(1, Convert.ToInt32(hit.Properties["RegionScaleCount"]));
            Assert.True(Convert.ToDouble(hit.Properties["Scale"]) > 0);
        }
        Assert.Equal(free ? 2 : 0, feeReader.Calls);
    }

    /// <summary>购买弹窗命中定位缓存后仍独立调用费用OCR，后续费用拒绝应立即撤回购买动作。</summary>
    /// <param name="fixture">真实短标题或长标题免费购买弹窗。</param>
    [Theory]
    [InlineData("free-confirm.png")]
    [InlineData("free-confirm-long-title.png")]
    public void WarmPurchaseDialogRechecksFeeOcrBeforeEveryApproval(string fixture)
    {
        using var image = LoadFrame(fixture);
        var logger = new CacheLogger();
        var feeReader = new CountingTextReader("1次免费", "消费100宝石");
        using var subject = new OpenCvPackRecognizer(logger, new OcrFreePackCostVerifier(feeReader),
            new CountingTextReader("完整卡包标题"));
        var frame = ToFrame(image);

        var cold = subject.Recognize(frame);
        logger.Entries.Clear();
        var warm = subject.Recognize(frame);

        Assert.Equal(PackScreen.FreePurchaseDialog, cold.Screen);
        Assert.True(cold.FreeOffer);
        Assert.NotNull(cold.PrimaryTarget);
        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, warm.Screen);
        Assert.False(warm.FreeOffer);
        Assert.Null(warm.PrimaryTarget);
        Assert.Equal(2, feeReader.Calls);
        Assert.NotEmpty(logger.Events("FreePackTemplateCacheHit"));
    }

    /// <summary>枚举真实收费详情和收费购买弹窗在四档缩放下的拒绝行为。</summary>
    /// <returns>真实截图资源、预期界面及当前物理缩放比例。</returns>
    public static IEnumerable<object[]> PaidScreenCases()
    {
        foreach (var fixture in new[] { "paid-details.png", "paid-confirm.png" })
            foreach (var scale in new[] { .65, .85, 1, 1.25 })
                yield return [fixture, fixture == "paid-details.png" ? PackScreen.PackDetails
                    : PackScreen.UnverifiedPurchaseDialog, scale];
    }

    /// <summary>局部缓存仅复用定位，真实收费界面在受控免费OCR文字下仍不得产生免费动作。</summary>
    /// <param name="fixture">真实收费详情或购买弹窗。</param>
    /// <param name="screen">按真实组合锚点建立的原始界面状态。</param>
    /// <param name="scale">当前真实截图的物理缩放比例。</param>
    [Theory]
    [MemberData(nameof(PaidScreenCases))]
    public void WarmPaidScreensNeverReuseAFreeApproval(string fixture, PackScreen screen, double scale)
    {
        using var original = LoadFrame(fixture);
        using var image = Resize(original, scale);
        var logger = new CacheLogger();
        using var subject = new OpenCvPackRecognizer(logger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("完整卡包标题"));
        var frame = ToFrame(image);

        var cold = subject.Recognize(frame);
        logger.Entries.Clear();
        var warm = subject.Recognize(frame);

        Assert.Equal(screen, cold.Screen);
        Assert.False(cold.FreeOffer);
        Assert.Null(cold.PrimaryTarget);
        AssertSameActions(cold, warm);
        Assert.NotEmpty(logger.Events("FreePackTemplateCacheHit"));
    }

    /// <summary>真实免费文字被遮挡后，已有定位必须回退复验并撤回旧免费状态和点击目标。</summary>
    /// <param name="scale">当前真实截图的物理缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void OccludingTheCachedFreeTextWithdrawsTheOldAction(double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var changed = original.Clone();
        var yellow = original.At<Vec4b>(934, 1550);
        using (var text = new Mat(changed, new Rect(1605, 912, 112, 45)))
            text.SetTo(new Scalar(yellow.Item0, yellow.Item1, yellow.Item2, yellow.Item3));
        using var coldImage = Resize(original, scale);
        using var changedImage = Resize(changed, scale);
        var logger = new CacheLogger();
        var feeReader = new CountingTextReader("1次免费");
        using var subject = new OpenCvPackRecognizer(logger, new OcrFreePackCostVerifier(feeReader),
            new CountingTextReader("颠覆世界恶魔之力"));

        var cold = subject.Recognize(ToFrame(coldImage));
        logger.Entries.Clear();
        var changedObservation = subject.Recognize(ToFrame(changedImage));

        Assert.True(cold.FreeOffer);
        Assert.NotNull(cold.PrimaryTarget);
        Assert.Equal(PackScreen.PackDetails, changedObservation.Screen);
        Assert.False(changedObservation.FreeOffer);
        Assert.Null(changedObservation.PrimaryTarget);
        Assert.Equal(cold.NextTarget, changedObservation.NextTarget);
        AssertCacheEvent(logger, "FreePackTemplateCacheFallback", "free-entry-text");
        Assert.Equal(1, feeReader.Calls);
    }

    /// <summary>真实免费文字首帧被遮挡后恢复时，应重新完整定位，不以阴性匹配缓存阻止实际免费入口。</summary>
    /// <param name="scale">当前真实截图的物理缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void RestoringRealFreeTextAfterANegativeMatchUsesANewFullLocalization(double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var obscured = original.Clone();
        var yellow = original.At<Vec4b>(934, 1550);
        using (var text = new Mat(obscured, new Rect(1605, 912, 112, 45)))
            text.SetTo(new Scalar(yellow.Item0, yellow.Item1, yellow.Item2, yellow.Item3));
        using var originalImage = Resize(original, scale);
        using var obscuredImage = Resize(obscured, scale);
        var logger = new CacheLogger();
        var feeReader = new CountingTextReader("1次免费");
        using var subject = new OpenCvPackRecognizer(logger, new OcrFreePackCostVerifier(feeReader),
            new CountingTextReader("完整卡包标题"));
        using var fresh = new OpenCvPackRecognizer(
            feeVerifier: new OcrFreePackCostVerifier(new CountingTextReader("1次免费")),
            titleReader: new CountingTextReader("完整卡包标题"));
        var expectedCold = fresh.Recognize(ToFrame(originalImage));

        var negative = subject.Recognize(ToFrame(obscuredImage));
        Assert.Equal(PackScreen.PackDetails, negative.Screen);
        Assert.False(negative.FreeOffer);
        Assert.Null(negative.PrimaryTarget);
        Assert.Equal(0, feeReader.Calls);
        logger.Entries.Clear();
        var restored = subject.Recognize(ToFrame(originalImage));

        Assert.True(restored.FreeOffer);
        AssertSameActions(expectedCold, restored);
        Assert.Equal(1, feeReader.Calls);
        Assert.Contains(logger.Events("FreePackTemplateCacheFallback"), entry =>
            Equals(entry.Properties.GetValueOrDefault("Template"), "free-entry-text")
            && Equals(entry.Properties.GetValueOrDefault("Reason"), "NoCachedMatch"));
    }

    /// <summary>真实免费按钮明确移动十二物理像素时，旧局部位置失效并由完整扫描返回新中心。</summary>
    /// <param name="scale">当前真实截图的物理缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void MovingTheFreeButtonBeyondTheLocalMarginReturnsItsNewCenter(double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var image = Resize(original, scale);
        using var shifted = ShiftRealFreeButton(image, scale, 12);
        var logger = new CacheLogger();
        using var subject = new OpenCvPackRecognizer(logger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("完整卡包标题"));

        var cold = subject.Recognize(ToFrame(image));
        logger.Entries.Clear();
        var moved = subject.Recognize(ToFrame(shifted));

        Assert.True(cold.FreeOffer);
        Assert.True(moved.FreeOffer);
        Assert.Equal(PackScreen.PackDetails, moved.Screen);
        var firstPoint = Assert.IsType<PixelPoint>(cold.PrimaryTarget);
        var movedPoint = Assert.IsType<PixelPoint>(moved.PrimaryTarget);
        Assert.InRange(movedPoint.X - firstPoint.X, 10, 14);
        Assert.InRange(Math.Abs(movedPoint.Y - firstPoint.Y), 0, 2);
        AssertCacheEvent(logger, "FreePackTemplateCacheFallback", "free-entry-text");
    }

    /// <summary>枚举真实免费按钮在四档缩放下向左或向右轻移两个物理像素。</summary>
    /// <returns>物理缩放比例及当前横向位移。</returns>
    public static IEnumerable<object[]> SmallMovementCases()
    {
        foreach (var scale in new[] { .65, .85, 1, 1.25 })
            foreach (var offset in new[] { -2, 2 }) yield return [scale, offset];
    }

    /// <summary>实际文字模板仍覆盖首帧点击中心时，两像素轻移沿用该中心并重新核验免费费用。</summary>
    /// <param name="scale">当前真实截图的物理缩放比例。</param>
    /// <param name="offset">真实按钮的物理横向位移。</param>
    [Theory]
    [MemberData(nameof(SmallMovementCases))]
    public void SmallMovementInsideTheRealTemplateKeepsTheFirstCenter(double scale, int offset)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var image = Resize(original, scale);
        using var shifted = ShiftRealFreeButton(image, scale, offset);
        var logger = new CacheLogger();
        var feeReader = new CountingTextReader("1次免费");
        using var subject = new OpenCvPackRecognizer(logger, new OcrFreePackCostVerifier(feeReader),
            new CountingTextReader("完整卡包标题"));

        var cold = subject.Recognize(ToFrame(image));
        logger.Entries.Clear();
        var moved = subject.Recognize(ToFrame(shifted));

        Assert.True(cold.FreeOffer);
        Assert.True(moved.FreeOffer);
        AssertSameActions(cold, moved);
        AssertCacheEvent(logger, "FreePackTemplateCacheHit", "free-entry-text");
        Assert.Equal(2, feeReader.Calls);
    }

    /// <summary>枚举窗口句柄、宽高和屏幕原点五个几何字段在四档真实缩放下的独立变化。</summary>
    /// <returns>单个变更字段及当前真实截图缩放比例。</returns>
    public static IEnumerable<object[]> GeometryCases()
    {
        foreach (var field in new[] { "window", "width", "height", "screen-x", "screen-y" })
            foreach (var scale in new[] { .65, .85, 1, 1.25 }) yield return [field, scale];
    }

    /// <summary>任一帧几何字段发生变化都应清除全部旧定位，再以真实截图建立新的完整匹配。</summary>
    /// <param name="field">本次独立变化的窗口几何字段。</param>
    /// <param name="scale">当前真实截图缩放比例。</param>
    [Theory]
    [MemberData(nameof(GeometryCases))]
    public void AnyGeometryKeyChangeInvalidatesAllOldLocalizations(string field, double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var image = Resize(original, scale);
        var logger = new CacheLogger();
        using var subject = new OpenCvPackRecognizer(logger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("完整卡包标题"));
        var frame = ToFrame(image);
        subject.Recognize(frame);
        subject.Recognize(frame);
        Assert.NotEmpty(logger.Events("FreePackTemplateCacheHit"));
        logger.Entries.Clear();

        var changedFrame = ChangeGeometry(image, frame, field);
        using var fresh = new OpenCvPackRecognizer(
            feeVerifier: new OcrFreePackCostVerifier(new CountingTextReader("1次免费")),
            titleReader: new CountingTextReader("完整卡包标题"));
        var expectedCold = fresh.Recognize(changedFrame);
        var changed = subject.Recognize(changedFrame);

        AssertSameActions(expectedCold, changed);
        Assert.Equal(expectedCold.PackTitle, changed.PackTitle);
        Assert.Equal(expectedCold.TitleVisualSignature, changed.TitleVisualSignature);
        Assert.Single(logger.Events("FreePackTemplateCacheGeometryInvalidated"));
        Assert.Empty(logger.Events("FreePackTemplateCacheHit"));
    }

    /// <summary>四像素宽的真实分隔符轻移两像素后不再覆盖旧中心，必须回退完整定位。</summary>
    [Fact]
    public void TinySeparatorMovementOutsideTheOldCenterFallsBackToFullScan()
    {
        using var original = LoadFrame("paid-details-fire-beast-after-opening.png");
        using var image = Resize(original, .65);
        using var changed = image.Clone();
        using var strokeView = new Mat(image, new Rect(159, 32, 4, 22));
        using var stroke = strokeView.Clone();
        var background = image.At<Vec4b>(32, 156);
        using (var erased = new Mat(changed, new Rect(157, 32, 6, 22)))
            erased.SetTo(new Scalar(background.Item0, background.Item1, background.Item2, background.Item3));
        using (var destination = new Mat(changed, new Rect(157, 32, 4, 22))) stroke.CopyTo(destination);
        // 冷定位中心为(161,43)，新分隔符右界161是排他边界，旧中心已经位于新模板之外。
        var logger = new CacheLogger();
        using var subject = new OpenCvPackRecognizer(logger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("猛火魔兽"));

        var cold = subject.Recognize(ToFrame(image));
        logger.Entries.Clear();
        var moved = subject.Recognize(ToFrame(changed));

        Assert.Equal(PackScreen.PackDetails, cold.Screen);
        AssertSameActions(cold, moved);
        AssertCacheEvent(logger, "FreePackTemplateCacheFallback", "header-separator");
        Assert.DoesNotContain(logger.Events("FreePackTemplateCacheHit"), entry =>
            Equals(entry.Properties.GetValueOrDefault("Template"), "header-separator"));
    }

    /// <summary>真实类别轻微变大后，旧尺度仅达到通用阈值时必须回退，保留另一完整尺度已经通过的详情身份。</summary>
    /// <param name="category">实际秘密类别或普通类别联合模板名称。</param>
    [Theory]
    [InlineData("header-secret")]
    [InlineData("header-normal")]
    public void WarmHeaderBelowTheStrictThresholdFallsBackToTheValidFullScale(string category)
    {
        using var original = LoadFrame("paid-details.png");
        using var template = LoadHeaderTemplate(category);
        if (category == "header-normal")
        {
            using var destination = new Mat(original, new Rect(120, 80, 158, 43));
            template.CopyTo(destination);
        }
        using var changedImage = ResizeRealJointHeader(original);
        var warmLogger = new CacheLogger();
        using var warmSubject = new OpenCvPackRecognizer(warmLogger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("完整卡包标题"));
        var originalObservation = warmSubject.Recognize(ToFrame(original));
        Assert.Equal(PackScreen.PackDetails, originalObservation.Screen);
        var originalHeader = Assert.Single(warmLogger.Events("FreePackHeaderMatched"));
        Assert.Equal(category, originalHeader.Properties["Category"]);
        var oldScale = Convert.ToDouble(originalHeader.Properties["AnchorScale"]);
        var originalBounds = new Rect(Convert.ToInt32(originalHeader.Properties["HeaderX"]),
            Convert.ToInt32(originalHeader.Properties["HeaderY"]),
            Convert.ToInt32(originalHeader.Properties["HeaderWidth"]),
            Convert.ToInt32(originalHeader.Properties["HeaderHeight"]));
        // 独立真实CCoeff测量不依赖修复后的缓存命中事件，固定变体仍须明确落在两个阈值之间。
        var oldScaleScore = MeasureRealHeaderAtOneScale(changedImage, template, oldScale, originalBounds);
        Assert.InRange(oldScaleScore, .84, 1);
        Assert.True(oldScaleScore < .92, $"旧尺度分数应低于完整类别门槛，实际{oldScaleScore:F9}。");
        warmLogger.Entries.Clear();
        var warm = warmSubject.Recognize(ToFrame(changedImage));

        var coldLogger = new CacheLogger();
        using var coldSubject = new OpenCvPackRecognizer(coldLogger,
            new OcrFreePackCostVerifier(new CountingTextReader("1次免费")), new CountingTextReader("完整卡包标题"));
        var expectedCold = coldSubject.Recognize(ToFrame(changedImage));
        Assert.Equal(PackScreen.PackDetails, expectedCold.Screen);
        var coldHeader = Assert.Single(coldLogger.Events("FreePackHeaderMatched"));
        Assert.Equal(category, coldHeader.Properties["Category"]);
        Assert.InRange(Convert.ToDouble(coldHeader.Properties["Confidence"]), .92, 1);

        AssertSameActions(expectedCold, warm);
        Assert.Equal(expectedCold.PackTitle, warm.PackTitle);
        Assert.Equal(expectedCold.TitleVisualSignature, warm.TitleVisualSignature);
        Assert.Equal(expectedCold.Fingerprint, warm.Fingerprint);
        AssertCacheEvent(warmLogger, "FreePackTemplateCacheFallback", category);
    }

    /// <summary>带红色提示图标的真实自然选择详情须由默认Windows OCR识别，并保持冷暖完整身份和免费授权。</summary>
    /// <param name="scale">真实原图或客户区的物理缩放比例。</param>
    /// <param name="cropWindow">是否先从原始窗口截图保留实际客户区。</param>
    [Theory]
    [InlineData(.65, false)]
    [InlineData(.65, true)]
    [InlineData(.85, false)]
    [InlineData(.85, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(1.25, false)]
    [InlineData(1.25, true)]
    public void NativeAlertShiftedCategoryKeepsTheRealFreePackIdentity(double scale, bool cropWindow)
    {
        using var original = LoadFrame("free-details-nature-selection-alert-full-window.png");
        using var clientView = new Mat(original, cropWindow ? new Rect(1, 31, 2048, 1152)
            : new Rect(0, 0, original.Width, original.Height));
        using var client = clientView.Clone();
        using var image = Resize(client, scale);
        var logger = new CacheLogger();
        using var subject = new OpenCvPackRecognizer(logger);
        var frame = ToFrame(image);

        var cold = subject.Recognize(frame);
        var warm = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, cold.Screen);
        Assert.Equal("自然选择", cold.PackTitle);
        Assert.True(cold.FreeOffer);
        AssertNativeAlertTargetInside(cold.PrimaryTarget, new Rect(1311, 897, 493, 81), cropWindow, scale, image);
        AssertNativeAlertTargetInside(cold.NextTarget, new Rect(1931, 541, 75, 129), cropWindow, scale, image);
        Assert.Matches("\\A[0-9A-F]{64}\\z", cold.TitleVisualSignature);
        AssertSameActions(cold, warm);
        Assert.Equal(cold.PackTitle, warm.PackTitle);
        Assert.Equal(cold.TitleVisualSignature, warm.TitleVisualSignature);
        var headers = logger.Events("FreePackHeaderMatched").ToArray();
        Assert.Equal(2, headers.Length);
        foreach (var header in headers)
        {
            Assert.InRange(Convert.ToDouble(header.Properties["Confidence"]), .92, 1);
            Assert.InRange(Convert.ToDouble(header.Properties["SeparatorConfidence"]), .84, 1);
        }
    }

    /// <summary>将独立核验的原始黄色按钮或下一包框映射到真实客户区和当前缩放，核验实际Native动作点。</summary>
    /// <param name="target">默认实际识别器返回的动作坐标。</param>
    /// <param name="originalRegion">原始PNG中人工及像素核验的实际动作区域。</param>
    /// <param name="cropWindow">是否移除一像素边框及三十一像素标题栏。</param>
    /// <param name="scale">当前输入的物理缩放比例。</param>
    /// <param name="image">当前真实输入图像。</param>
    private static void AssertNativeAlertTargetInside(PixelPoint? target, Rect originalRegion,
        bool cropWindow, double scale, Mat image)
    {
        var point = Assert.IsType<PixelPoint>(target);
        var left = originalRegion.Left - (cropWindow ? 1 : 0);
        var top = originalRegion.Top - (cropWindow ? 31 : 0);
        Assert.InRange(point.X, 0, image.Width - 1);
        Assert.InRange(point.Y, 0, image.Height - 1);
        Assert.InRange(point.X, (int)Math.Floor(left * scale),
            (int)Math.Ceiling((left + originalRegion.Width) * scale) - 1);
        Assert.InRange(point.Y, (int)Math.Floor(top * scale),
            (int)Math.Ceiling((top + originalRegion.Height) * scale) - 1);
    }

    /// <summary>从实际生产嵌入资源读取完整类别及分隔符模板，用真实像素构造普通类别及独立分数测量。</summary>
    /// <param name="category">实际类别联合模板名称。</param>
    /// <returns>由调用方释放的连续BGRA原模板。</returns>
    private static Mat LoadHeaderTemplate(string category)
    {
        using var stream = typeof(OpenCvPackRecognizer).Assembly.GetManifestResourceStream(
            $"MasterDuelSwitcher.Core.Assets.PackTemplates.{category}.png");
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var bgr = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
        Assert.False(bgr.Empty());
        var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    /// <summary>将原图完整联合类别158乘43像素仅以Linear放大为163乘45，保留原真实标题和其他界面内容。</summary>
    /// <param name="original">已经具有指定实际类别的完整真实详情。</param>
    /// <returns>由调用方释放的固定真实像素回归变体。</returns>
    private static Mat ResizeRealJointHeader(Mat original)
    {
        using var source = new Mat(original, new Rect(120, 80, 158, 43));
        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(163, 45), 0, 0, InterpolationFlags.Linear);
        var changed = original.Clone();
        var background = original.At<Vec4b>(78, 112);
        using (var erased = new Mat(changed, new Rect(120, 80, 163, 45)))
            erased.SetTo(new Scalar(background.Item0, background.Item1, background.Item2, background.Item3));
        using (var destination = new Mat(changed, new Rect(120, 80, 163, 45))) resized.CopyTo(destination);
        return changed;
    }

    /// <summary>在首帧公开联合类别边界加四像素的局部区域，独立计算旧尺度的实际归一化CCoeff最高分。</summary>
    /// <param name="image">仅改变完整类别尺寸的真实截图。</param>
    /// <param name="template">真实完整类别及分隔符BGRA模板。</param>
    /// <param name="scale">首帧公开日志已经确认的实际匹配尺度。</param>
    /// <param name="originalBounds">首帧公开日志确认的联合类别物理边界。</param>
    /// <returns>原真实模板在该指定尺度下的实际OpenCV匹配分数。</returns>
    private static double MeasureRealHeaderAtOneScale(Mat image, Mat template, double scale, Rect originalBounds)
    {
        var factor = Math.Min(1, 1280d / image.Width);
        using var gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGRA2GRAY);
        using var search = new Mat();
        Cv2.Resize(gray, search, new Size((int)Math.Round(image.Width * factor),
            (int)Math.Round(image.Height * factor)), 0, 0, InterpolationFlags.Area);
        using var templateGray = new Mat();
        Cv2.CvtColor(template, templateGray, ColorConversionCodes.BGRA2GRAY);
        using var resizedTemplate = new Mat();
        Cv2.Resize(templateGray, resizedTemplate, new Size((int)Math.Round(template.Width * scale * factor),
            (int)Math.Round(template.Height * scale * factor)), 0, 0, InterpolationFlags.Area);
        // 直接使用首帧实际联合边界加四个物理像素，明确测量缓存旧尺度对应的局部证据。
        var region = new Rect((int)Math.Floor((originalBounds.X - 4) * factor),
            (int)Math.Floor((originalBounds.Y - 4) * factor),
            (int)Math.Ceiling((originalBounds.Width + 8) * factor),
            (int)Math.Ceiling((originalBounds.Height + 8) * factor));
        using var area = new Mat(search, region);
        using var response = new Mat();
        Cv2.MatchTemplate(area, resizedTemplate, response, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(response, out _, out var score, out _, out _);
        return score;
    }

    /// <summary>通过真实日志筛选指定模板的缓存行为，避免绑定私有字段或匹配算法。</summary>
    /// <param name="logger">本测试收到的真实结构化日志。</param>
    /// <param name="eventName">必须出现的完整缓存事件名。</param>
    /// <param name="template">必须发生该行为的真实模板名。</param>
    private static void AssertCacheEvent(CacheLogger logger, string eventName, string template)
        => Assert.Contains(logger.Events(eventName), entry =>
            Equals(entry.Properties.GetValueOrDefault("Template"), template));

    /// <summary>复制原图中的整个真实免费按钮，再擦除原位置与目标位置的并集，避免遗留重复文字。</summary>
    /// <param name="image">已经按当前比例缩放的真实原图。</param>
    /// <param name="scale">当前真实原图缩放比例。</param>
    /// <param name="offset">复制按钮的物理横向位移。</param>
    /// <returns>由调用方释放的独立位移截图。</returns>
    private static Mat ShiftRealFreeButton(Mat image, double scale, int offset)
    {
        var source = new Rect((int)Math.Floor(1311 * scale), (int)Math.Floor(897 * scale),
            (int)Math.Ceiling(493 * scale), (int)Math.Ceiling(81 * scale));
        using var buttonView = new Mat(image, source);
        using var button = buttonView.Clone();
        var changed = image.Clone();
        var background = image.At<Vec4b>((int)Math.Round(900 * scale), (int)Math.Round(1298 * scale));
        var union = new Rect(source.X + Math.Min(0, offset), source.Y, source.Width + Math.Abs(offset), source.Height);
        using (var erased = new Mat(changed, union))
            erased.SetTo(new Scalar(background.Item0, background.Item1, background.Item2, background.Item3));
        using (var destination = new Mat(changed, new Rect(source.X + offset, source.Y, source.Width, source.Height)))
            button.CopyTo(destination);
        return changed;
    }

    /// <summary>仅改变指定几何字段，宽高变化通过添加一像素背景保持原有真实界面内容完整。</summary>
    /// <param name="image">真实连续BGRA截图。</param>
    /// <param name="frame">原始窗口几何和像素。</param>
    /// <param name="field">当前独立变化的几何字段。</param>
    /// <returns>像素布局正确且只改变指定几何字段的真实帧。</returns>
    private static GameFrame ChangeGeometry(Mat image, GameFrame frame, string field)
    {
        if (field == "window") return frame with { WindowHandle = 2 };
        if (field == "screen-x") return frame with { ScreenX = frame.ScreenX + 1 };
        if (field == "screen-y") return frame with { ScreenY = frame.ScreenY + 1 };
        using var bordered = new Mat();
        Cv2.CopyMakeBorder(image, bordered, 0, field == "height" ? 1 : 0, 0, field == "width" ? 1 : 0,
            BorderTypes.Constant, new Scalar(80, 24, 13, 255));
        return ToFrame(bordered);
    }

    /// <summary>比较真实识别的界面、费用与各动作坐标，不以缓存内部字段代替行为断言。</summary>
    /// <param name="cold">完整多尺度扫描得到的首帧观察。</param>
    /// <param name="warm">局部单尺度复验得到的后续观察。</param>
    private static void AssertSameActions(PackObservation cold, PackObservation warm)
    {
        Assert.Equal(cold.Screen, warm.Screen);
        Assert.Equal(cold.FreeOffer, warm.FreeOffer);
        Assert.Equal(cold.PrimaryTarget, warm.PrimaryTarget);
        Assert.Equal(cold.NextTarget, warm.NextTarget);
        Assert.Equal(cold.AnimationSkipTarget, warm.AnimationSkipTarget);
    }

    /// <summary>从测试程序集读取真实PNG并解码为连续BGRA。</summary>
    /// <param name="name">真实截图嵌入资源名称。</param>
    /// <returns>由调用方释放的原生图像。</returns>
    private static Mat LoadFrame(string name)
    {
        using var stream = typeof(PackTemplateLocalizationCacheTests).Assembly.GetManifestResourceStream(
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

    /// <summary>按既有真实截图规则生成缩小面积插值或放大线性插值的输入。</summary>
    /// <param name="original">真实原始截图。</param>
    /// <param name="scale">当前物理像素缩放比例。</param>
    /// <returns>由调用方释放的连续BGRA缩放图像。</returns>
    private static Mat Resize(Mat original, double scale)
    {
        var resized = new Mat();
        Cv2.Resize(original, resized, new Size((int)Math.Round(original.Width * scale),
            (int)Math.Round(original.Height * scale)), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return resized;
    }

    /// <summary>复制紧密BGRA构造离线帧，所有后续变更通过独立真实图像完成。</summary>
    /// <param name="image">连续四通道截图。</param>
    /// <returns>拥有独立字节数组及固定窗口几何的帧。</returns>
    private static GameFrame ToFrame(Mat image)
    {
        Assert.True(image.IsContinuous());
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(1, image.Width, image.Height, 100, 200, pixels, CapturedAt);
    }

    /// <summary>只隔离系统OCR，记录每次真实费用授权跨越文字识别边界的调用。</summary>
    /// <param name="firstText">首次文字识别的返回值。</param>
    /// <param name="laterText">后续独立识别的返回值；省略时与首次相同。</param>
    private sealed class CountingTextReader(string firstText, string? laterText = null) : IPackTextReader
    {
        /// <summary>生产识别器实际提交OCR的次数。</summary>
        public int Calls { get; private set; }

        /// <summary>每次递增真实边界调用次数，再返回本次指定文字。</summary>
        /// <param name="bgraPixels">实际OCR区域的紧密BGRA像素。</param>
        /// <param name="width">实际区域物理宽度。</param>
        /// <param name="height">实际区域物理高度。</param>
        /// <returns>当前独立识别的受控文字结果。</returns>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            Calls++;
            return Calls > 1 && laterText is not null ? laterText : firstText;
        }
    }

    /// <summary>保留真实日志格式化结果及结构化字段，用事件核验局部命中和完整回退。</summary>
    /// <param name="Message">真实生产日志格式化文本。</param>
    /// <param name="Properties">真实模板日志中的结构化字段。</param>
    private sealed record CacheEntry(string Message, IReadOnlyDictionary<string, object?> Properties);

    /// <summary>读取识别器真实日志事件，不访问其私有缓存结构。</summary>
    private sealed class CacheLogger : ILogger<OpenCvPackRecognizer>
    {
        /// <summary>当前测试按实际顺序收到的全部识别日志。</summary>
        public List<CacheEntry> Entries { get; } = [];

        /// <summary>按明确的缓存诊断事件名称筛选真实输出。</summary>
        /// <param name="name">日志中的完整事件标识。</param>
        /// <returns>包含该事件标识的生产日志条目。</returns>
        public IEnumerable<CacheEntry> Events(string name) => Entries.Where(entry =>
            entry.Message.Contains(name, StringComparison.Ordinal));

        /// <summary>测试日志无需分配作用域。</summary>
        /// <param name="state">作用域状态。</param>
        /// <returns>空作用域。</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>保留全部生产日志级别。</summary>
        /// <param name="logLevel">当前日志级别。</param>
        /// <returns>始终启用。</returns>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>调用真实格式化器并保存实际结构化字段。</summary>
        /// <param name="logLevel">当前事件级别。</param>
        /// <param name="eventId">当前事件标识。</param>
        /// <param name="state">真实日志状态。</param>
        /// <param name="exception">当前事件异常。</param>
        /// <param name="formatter">真实文本格式化器。</param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            Entries.Add(new CacheEntry(formatter(state, exception), properties));
        }
    }
}
