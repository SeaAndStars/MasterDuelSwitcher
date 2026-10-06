using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using OpenCvSharp;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>用真实详情截图验证完整二维标题字形身份独立于文字识别和有色背景。</summary>
public sealed class TitleVisualIdentityTests
{
    /// <summary>离线截图帧统一使用的协调世界捕获时间。</summary>
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>同一原始标题即使被识别为星生或星尘，也应具有相同且完整的视觉签名。</summary>
    [Fact]
    public void SameOriginalImageWithDifferentOcrTitlesKeepsTheSameSignature()
    {
        using var image = LoadFrame("paid-details.png");

        var first = Observe(image, "编织羁绊的星生");
        var second = Observe(image, "编织羁绊的星尘");

        Assert.Equal("编织羁绊的星生", first.PackTitle);
        Assert.Equal("编织羁绊的星尘", second.PackTitle);
        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>标题行中的暗背景变为强蓝色时，全部原始白字及其视觉签名保持不变。</summary>
    [Fact]
    public void StrongBlueTitleBackgroundKeepsTheOriginalWhiteGlyphSignature()
    {
        using var original = LoadFrame("paid-details.png");
        using var changed = original.Clone();
        var modifiedPixels = 0;
        // 只修改远离联合类别锚点的暗像素，保留强白字及全部亮色抗锯齿像素。
        var titleRegion = new Rect(284, 55, 980, 80);
        for (var y = titleRegion.Top; y < titleRegion.Bottom; y++)
            for (var x = titleRegion.Left; x < titleRegion.Right; x++)
            {
                var pixel = original.At<Vec4b>(y, x);
                if (Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2)) >= 100) continue;
                changed.Set(y, x, new Vec4b(255, 40, 0, pixel.Item3));
                modifiedPixels++;
            }
        Assert.True(modifiedPixels > 0);

        var first = Observe(original, "驱邪祓恶巳神传说");
        var second = Observe(changed, "驱邪祓恶巳神传说");

        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>真实末字尘的土竖笔少一个中性白像素时，即使OCR文字相同也应改变完整二维签名。</summary>
    /// <param name="blue">使用纯蓝色移除该前景像素；否则使用黑色。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOneRealWhitePixelFromTheLastGlyphChangesTheSignature(bool blue)
    {
        using var original = LoadFrame("paid-details-stardust-live.png");
        using var changed = original.Clone();
        var point = FindNeutralWhitePixel(original, new Rect(494, 76, 3, 4));
        changed.Set(point.Y, point.X, new Vec4b(blue ? (byte)255 : (byte)0, 0, 0, 255));

        var first = Observe(original, "编织羁绊的星尘");
        var second = Observe(changed, "编织羁绊的星尘");

        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.PackTitle, second.PackTitle);
        Assert.NotEqual(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>枚举七张真实详情客户区在四档物理缩放下的独立视觉身份验证。</summary>
    /// <returns>截图资源名、是否移除原窗口边框及当前缩放比例。</returns>
    public static IEnumerable<object[]> RealClientAreaCases()
    {
        (string File, bool CropWindow)[] fixtures =
        [
            ("paid-details.png", true),
            ("paid-details-after-opening.png", true),
            ("free-details-single-row.png", true),
            ("free-details-second-title.png", true),
            ("free-details-top-edge.png", false),
            ("paid-details-fire-beast-after-opening.png", false),
            ("paid-details-stardust-live.png", false)
        ];
        foreach (var fixture in fixtures)
            foreach (var scale in new[] { .65, .85, 1, 1.25 })
                yield return [fixture.File, fixture.CropWindow, scale];
    }

    /// <summary>每档实际客户区缩放都应建立完整签名，保留补充平面Unicode及合法丨，且同图异文不改签名。</summary>
    /// <param name="fixture">真实详情截图资源名。</param>
    /// <param name="cropWindow">原图为窗口截图时先移除一像素边框和三十一像素标题栏。</param>
    /// <param name="scale">该用例的物理像素缩放比例。</param>
    [Theory]
    [MemberData(nameof(RealClientAreaCases))]
    public void RealClientAreasAtFourScalesKeepCompleteVisualIdentity(string fixture, bool cropWindow, double scale)
    {
        using var original = LoadFrame(fixture);
        using var clientView = new Mat(original, cropWindow ? new Rect(1, 31, 2048, 1152)
            : new Rect(0, 0, original.Width, original.Height));
        using var client = clientView.Clone();
        using var image = Resize(client, scale);

        var first = Observe(image, "𠮷丨魔神Ａ２");
        var second = Observe(image, "另一个OCR标题");

        Assert.Equal("𠮷丨魔神A2", first.PackTitle);
        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>仅改变客户区坐标原点而保留全部原始字形时，签名不得包含标题位置。</summary>
    [Fact]
    public void RemovingTheWindowBorderKeepsTheUnchangedGlyphSignature()
    {
        using var original = LoadFrame("paid-details.png");
        using var clientView = new Mat(original, new Rect(1, 31, 2048, 1152));
        using var client = clientView.Clone();

        var first = Observe(original, "驱邪祓恶巳神传说");
        var second = Observe(client, "驱邪祓恶巳神传说");

        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>不同原始完整标题即使得到相同OCR文字，也应具有不同视觉签名。</summary>
    [Fact]
    public void DifferentRealTitlesWithTheSameOcrTextHaveDifferentSignatures()
    {
        using var firstImage = LoadFrame("paid-details.png");
        using var secondImage = LoadFrame("free-details-second-title.png");

        var first = Observe(firstImage, "同一个OCR标题");
        var second = Observe(secondImage, "同一个OCR标题");

        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.PackTitle, second.PackTitle);
        Assert.NotEqual(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>真实免费详情在费用证据明确通过时，也应附带完整标题视觉签名。</summary>
    [Fact]
    public void RealFreeDetailsKeepTheSameSignatureWhenFeeEvidenceIsApproved()
    {
        using var image = LoadFrame("free-details-single-row.png");
        var rejected = Observe(image, "颠覆世界恶魔之力");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new AcceptingFeeVerifier(),
            titleReader: new FixedTitleReader("颠覆世界恶魔之力"));

        var approved = subject.Recognize(ToFrame(image));

        AssertSignature(rejected);
        Assert.Equal(PackScreen.PackDetails, approved.Screen);
        Assert.True(approved.FreeOffer);
        Assert.NotNull(approved.PrimaryTarget);
        Assert.NotNull(approved.NextTarget);
        Assert.Matches("\\A[0-9A-F]{64}\\z", approved.TitleVisualSignature);
        Assert.Equal(rejected.TitleVisualSignature, approved.TitleVisualSignature);
    }

    /// <summary>真实末字空隙中的单个蓝色动效像素应被排除，不改变完整白字身份。</summary>
    [Fact]
    public void BlueParticleInsideTheLastGlyphBoundsDoesNotChangeTheSignature()
    {
        using var original = LoadFrame("paid-details-stardust-live.png");
        using var changed = original.Clone();
        var point = FindDarkPixel(original, new Rect(482, 56, 28, 27));
        changed.Set(point.Y, point.X, new Vec4b(255, 40, 0, 255));

        var first = Observe(original, "编织羁绊的星尘");
        var second = Observe(changed, "编织羁绊的星尘");

        AssertSignature(first);
        AssertSignature(second);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>新增像素只有达到中性强白的亮度和色差边界时，才应改变真实二维标题身份。</summary>
    /// <param name="maximum">新像素最大颜色通道亮度。</param>
    /// <param name="spread">最大和最小颜色通道之间的色差。</param>
    /// <param name="included">该真实空隙像素是否满足明确的白字前景契约。</param>
    [Theory]
    [InlineData(209, 0, false)]
    [InlineData(210, 0, true)]
    [InlineData(255, 8, true)]
    [InlineData(255, 9, false)]
    public void WhiteBrightnessAndNeutralityBoundariesDetermineMaskMembership(int maximum, int spread, bool included)
    {
        using var original = LoadFrame("paid-details-stardust-live.png");
        using var changed = original.Clone();
        var point = FindDarkPixel(original, new Rect(482, 56, 28, 27));
        changed.Set(point.Y, point.X, new Vec4b((byte)maximum, (byte)maximum, (byte)(maximum - spread), 255));

        var first = Observe(original, "编织羁绊的星尘");
        var second = Observe(changed, "编织羁绊的星尘");

        AssertSignature(first);
        AssertSignature(second);
        if (included) Assert.NotEqual(first.TitleVisualSignature, second.TitleVisualSignature);
        else Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>高灰度蓝色标题背景保留旧灰度指纹前置条件，但没有中性强白字时视觉签名为空。</summary>
    [Fact]
    public void CompleteHeaderWithNoWhiteTitleForegroundHasNoVisualSignature()
    {
        using var image = LoadFrame("paid-details.png");
        using (var title = new Mat(image, new Rect(278, 76, 1000, 50)))
            title.SetTo(new Scalar(255, 130, 80, 255));

        var observation = Observe(image, "驱邪祓恶巳神传说");

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("驱邪祓恶巳神传说", observation.PackTitle);
        Assert.NotEmpty(observation.Fingerprint);
        Assert.Empty(observation.TitleVisualSignature);
    }

    /// <summary>缺少完整类别或其末端分隔符时，实际详情锚点及有效OCR文字仍不得建立字形身份。</summary>
    /// <param name="removeSeparator">仅擦除联合分隔符；否则擦除第一个类别字。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCompleteCategoryOrSeparatorHasNoVisualSignature(bool removeSeparator)
    {
        using var image = LoadFrame("paid-details.png");
        using (var damaged = new Mat(image, removeSeparator ? new Rect(265, 80, 13, 43)
            : new Rect(120, 80, 38, 43)))
            damaged.SetTo(new Scalar(36, 28, 27, 255));

        var observation = Observe(image, "驱邪祓恶巳神传说");

        Assert.Equal(PackScreen.Unknown, observation.Screen);
        Assert.Empty(observation.TitleVisualSignature);
        Assert.Empty(observation.PackTitle);
    }

    /// <summary>标题首末合法丨的原始竖笔均参与完整字形签名，文字过滤保留两端Unicode语义。</summary>
    /// <param name="leading">将真实白字竖笔放在标题首端；否则放在末端，分别验证两端完整参与。</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealLeadingAndTrailingVerticalGlyphsParticipateInTheCompleteSignature(bool leading)
    {
        using var original = LoadFrame("paid-details.png");
        using var changed = original.Clone();
        // 从原标题字形中复制连续二十八像素的真实中性白竖笔；类别分隔符是蓝灰色装饰线。
        using var strokeView = new Mat(original, new Rect(470, 84, 2, 35));
        using var stroke = strokeView.Clone();
        Assert.True(IsNeutralWhite(stroke.At<Vec4b>(5, 0)));
        using (var destination = new Mat(changed, new Rect(leading ? 284 : 580, 84, 2, 35))) stroke.CopyTo(destination);

        var first = Observe(original, "丨驱邪祓恶巳神传说丨");
        var second = Observe(changed, "丨驱邪祓恶巳神传说丨");

        Assert.Equal("丨驱邪祓恶巳神传说丨", second.PackTitle);
        AssertSignature(first);
        AssertSignature(second);
        Assert.NotEqual(first.TitleVisualSignature, second.TitleVisualSignature);
    }

    /// <summary>空灰帧及低于最小帧尺寸的输入均返回未知状态和空视觉身份。</summary>
    /// <param name="width">输入帧物理宽度。</param>
    /// <param name="height">输入帧物理高度。</param>
    /// <param name="level">所有颜色通道的统一灰度。</param>
    [Theory]
    [InlineData(2048, 1152, 0)]
    [InlineData(2048, 1152, 128)]
    [InlineData(100, 100, 128)]
    public void BlankOrTinyFramesHaveNoVisualSignature(int width, int height, int level)
    {
        using var image = new Mat(height, width, MatType.CV_8UC4, new Scalar(level, level, level, 255));

        var observation = Observe(image, "𠮷丨魔神Ａ２");

        Assert.Equal(PackScreen.Unknown, observation.Screen);
        Assert.Empty(observation.TitleVisualSignature);
    }

    /// <summary>真实开包和结果界面即使注入有效OCR文字，也始终没有详情标题字形身份。</summary>
    /// <param name="fixture">真实非详情截图资源名。</param>
    /// <param name="screen">原图按已有组合锚点建立的实际界面状态。</param>
    [Theory]
    [InlineData("opening.png", PackScreen.Opening)]
    [InlineData("results.png", PackScreen.Results)]
    public void NonDetailsScreensHaveNoTitleVisualSignature(string fixture, PackScreen screen)
    {
        using var image = LoadFrame(fixture);

        var observation = Observe(image, "𠮷丨魔神Ａ２");

        Assert.Equal(screen, observation.Screen);
        Assert.Empty(observation.TitleVisualSignature);
        Assert.Empty(observation.PackTitle);
    }

    /// <summary>仅替换系统文字和费用识别边界，保留真实模板、锚点、像素及签名计算。</summary>
    /// <param name="image">拥有真实界面锚点的原始BGRA图像。</param>
    /// <param name="title">受控OCR返回的完整文字。</param>
    /// <returns>真实OpenCV识别器建立的观察结果。</returns>
    private static PackObservation Observe(Mat image, string title)
    {
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RejectingFeeVerifier(),
            titleReader: new FixedTitleReader(title));
        return subject.Recognize(ToFrame(image));
    }

    /// <summary>核验真实详情具有完整SHA256十六进制签名，并且费用替身未授权免费动作。</summary>
    /// <param name="observation">真实识别器返回的详情观察。</param>
    private static void AssertSignature(PackObservation observation)
    {
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Matches("\\A[0-9A-F]{64}\\z", observation.TitleVisualSignature);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
    }

    /// <summary>在人工确认的末字真实笔画内寻找中性强白像素，不生成替代文字。</summary>
    /// <param name="image">最新真实星尘客户区截图。</param>
    /// <param name="region">末字土竖笔内部的真实物理坐标。</param>
    /// <returns>位于完整字形边界内部的真实强白像素坐标。</returns>
    private static Point FindNeutralWhitePixel(Mat image, Rect region)
    {
        for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
                if (IsNeutralWhite(image.At<Vec4b>(y, x))) return new Point(x, y);
        throw new Xunit.Sdk.XunitException("真实末字笔画区域未找到中性强白像素。");
    }

    /// <summary>按批准的亮度和通道色差筛选真实中性白字，仅用于选择测试变更像素。</summary>
    /// <param name="pixel">原图中一个BGRA像素。</param>
    /// <returns>三个颜色通道均至少210且最大通道差不超过8时为真。</returns>
    private static bool IsNeutralWhite(Vec4b pixel)
    {
        var minimum = Math.Min(pixel.Item0, Math.Min(pixel.Item1, pixel.Item2));
        var maximum = Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2));
        return minimum >= 210 && maximum - minimum <= 8;
    }

    /// <summary>在真实字形包围框中寻找暗背景空隙，避开所有亮字和抗锯齿像素。</summary>
    /// <param name="image">真实原始BGRA截图。</param>
    /// <param name="region">人工确认包含标题末字的真实区域。</param>
    /// <returns>三个颜色通道均低于100的原始背景像素坐标。</returns>
    private static Point FindDarkPixel(Mat image, Rect region)
    {
        for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
            {
                var pixel = image.At<Vec4b>(y, x);
                if (Math.Max(pixel.Item0, Math.Max(pixel.Item1, pixel.Item2)) < 100) return new Point(x, y);
            }
        throw new Xunit.Sdk.XunitException("真实末字包围框未找到暗背景空隙。");
    }

    /// <summary>读取嵌入的真实截图并解码为连续BGRA像素。</summary>
    /// <param name="name">真实截图资源文件名。</param>
    /// <returns>由调用方释放的原始OpenCV图像。</returns>
    private static Mat LoadFrame(string name)
    {
        using var stream = typeof(TitleVisualIdentityTests).Assembly.GetManifestResourceStream(
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

    /// <summary>按已有真实截图测试的插值规则生成客户区四档缩放，不要求跨尺度签名相等。</summary>
    /// <param name="original">完整原始客户区BGRA像素。</param>
    /// <param name="scale">当前用例的物理缩放比例。</param>
    /// <returns>由调用方释放的连续缩放图像。</returns>
    private static Mat Resize(Mat original, double scale)
    {
        var resized = new Mat();
        Cv2.Resize(original, resized, new Size((int)Math.Round(original.Width * scale),
            (int)Math.Round(original.Height * scale)), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return resized;
    }

    /// <summary>复制原始紧密BGRA像素构造离线帧，隔离临时原生图像的生命周期。</summary>
    /// <param name="image">连续四通道图像。</param>
    /// <returns>拥有独立像素数组的离线游戏帧。</returns>
    private static GameFrame ToFrame(Mat image)
    {
        Assert.Equal(MatType.CV_8UC4, image.Type());
        Assert.True(image.IsContinuous());
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(1, image.Width, image.Height, 0, 0, pixels, CapturedAt);
    }

    /// <summary>仅隔离依赖系统语言模型的OCR调用，保留生产Unicode规范化流程。</summary>
    /// <param name="text">受控OCR原始返回文字。</param>
    private sealed class FixedTitleReader(string text) : IPackTextReader
    {
        /// <summary>向真实识别器返回指定文字，字形身份仍由原始截图计算。</summary>
        /// <param name="bgraPixels">识别器传入的完整标题区域。</param>
        /// <param name="width">标题区域物理宽度。</param>
        /// <param name="height">标题区域物理高度。</param>
        /// <returns>本测试指定的OCR文字。</returns>
        public string Read(byte[] bgraPixels, int width, int height) => text;
    }

    /// <summary>明确拒绝所有免费费用证据，隔离系统OCR及免费动作授权。</summary>
    private sealed class RejectingFeeVerifier : IPackFeeVerifier
    {
        /// <summary>始终返回未确认免费，实际费用模板和状态匹配仍由生产识别器完成。</summary>
        /// <param name="bgraPixels">真实费用区域像素。</param>
        /// <param name="width">费用区域物理宽度。</param>
        /// <param name="height">费用区域物理高度。</param>
        /// <param name="gemDetected">真实模板是否检测到宝石标志。</param>
        /// <returns>始终为假。</returns>
        public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected) => false;
    }

    /// <summary>只在真实免费详情测试中确认费用证据，保留模板及宝石的生产组合门禁。</summary>
    private sealed class AcceptingFeeVerifier : IPackFeeVerifier
    {
        /// <summary>提供明确的免费费用证据，实际界面与按钮授权仍由生产识别器完成。</summary>
        /// <param name="bgraPixels">真实费用区域像素。</param>
        /// <param name="width">费用区域物理宽度。</param>
        /// <param name="height">费用区域物理高度。</param>
        /// <param name="gemDetected">真实模板是否检测到宝石标志。</param>
        /// <returns>始终确认局部免费文字。</returns>
        public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected) => true;
    }
}
