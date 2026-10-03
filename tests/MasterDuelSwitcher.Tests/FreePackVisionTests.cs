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
    /// <summary>被测的默认截图识别器。</summary>
    private readonly OpenCvPackRecognizer recognizer = new();

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

    /// <summary>同一真实卡图在四种缩放下应得到相近指纹；免费入口叠加也不得改变卡包身份。</summary>
    [Theory]
    [InlineData(0.65)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    public void ArtworkFingerprintIsStableAcrossScaleAndFreeOffer(double scale)
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
        Assert.InRange(HammingDistance(baseline, scaled), 0, 8);
        Assert.InRange(HammingDistance(baseline, free), 0, 8);
    }

    /// <summary>将另一张真实卡图放入详情插画区后，状态锚点保留而卡包指纹应改变。</summary>
    [Fact]
    public void DifferentRealArtworkChangesFingerprint()
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
        Assert.True(HammingDistance(first.Fingerprint, second.Fingerprint) > 4);
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

    /// <summary>免费入口缺少单包文字时保留详情与卡图身份，但撤回免费购买授权。</summary>
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

    /// <summary>原免费文字被移到费用行其他位置或弹窗外背景时，几何费用组合应验证失败。</summary>
    [Theory]
    [InlineData(830, 382)]
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

    /// <summary>菜单及双箭头仍在而左侧插画被截断时，不得为缺少完整卡图身份的详情返回导航。</summary>
    [Fact]
    public void TruncatedArtworkWithMenuAndNextArrowDoesNotAuthorizeDetails()
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
        using var loggedRecognizer = new OpenCvPackRecognizer(factory.CreateLogger<OpenCvPackRecognizer>());
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

    /// <summary>计算两个六十四位图像指纹的真实位汉明距离，允许缩放插值产生少量位差。</summary>
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
