using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用真实类别字样与分隔符验证标题锚点、完整字形和缺失锚点的动作门禁。</summary>
public sealed class PackHeaderAnchorTests
{
    /// <summary>所有离线截图帧使用的固定捕获时间。</summary>
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);

    /// <summary>枚举真实详情四档缩放，分别核验原截图及其实际客户区的原生标题和费用 OCR。</summary>
    /// <returns>真实截图、完整标题、免费标志、缩放比例及是否移除窗口标题栏。</returns>
    public static IEnumerable<object[]> NativeHeaderCases()
    {
        foreach (var item in SecretHeaderCases())
            if ((double)item[3] != .5)
            {
                yield return [item[0], item[1], item[2], item[3], false];
                if (item[0] is "paid-details.png" or "paid-details-after-opening.png"
                    or "free-details-single-row.png" or "free-details-second-title.png")
                    yield return [item[0], item[1], item[2], item[3], true];
            }
    }

    /// <summary>真实 Windows OCR 完成全部详情识别，不以模拟文本替代标题及免费费用门禁。</summary>
    /// <param name="fixture">真实详情截图资源名。</param>
    /// <param name="title">按原图核验的完整标题。</param>
    /// <param name="free">原图是否具有免费入口。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    /// <param name="removeTitlebar">原截图包含窗口边框时，仅保留真实客户区。</param>
    [Theory]
    [MemberData(nameof(NativeHeaderCases))]
    public void NativeOcrReadsExistingRealTitlesAndCosts(string fixture, string title, bool free, double scale, bool removeTitlebar)
    {
        using var original = LoadFrame(fixture);
        using var area = new Mat(original, removeTitlebar ? new Rect(1, 31, 2048, 1152)
            : new Rect(0, 0, original.Width, original.Height));
        using var image = Resize(area, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        using var subject = new OpenCvPackRecognizer();

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        Assert.Equal(free, observation.FreeOffer);
        Assert.Equal(free, observation.PrimaryTarget.HasValue);
        Assert.NotNull(observation.NextTarget);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>仅当第一遍没有有效文字时改变高度重读，保持原横向起点、完整原图像素及合法 Unicode。</summary>
    /// <param name="firstText">第一次识别的空白或标点结果。</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("【】💎?!")]
    public void EmptyTitleRetriesOnlyTheSamplingHeight(string firstText)
    {
        using var image = LoadFrame("free-details-single-row.png");
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var reader = new RecordingTitleReader(firstText, "𠮷丨魔神Ａ２");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(), titleReader: reader);

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("𠮷丨魔神A2", observation.PackTitle);
        Assert.True(observation.FreeOffer);
        Assert.Equal(2, reader.Calls.Count);
        var first = FindCopiedTitleRegion(frame, reader.Calls[0]);
        var second = FindCopiedTitleRegion(frame, reader.Calls[1]);
        Assert.Equal(first.Left, second.Left);
        Assert.Equal(first.Top, second.Top);
        Assert.Equal(first.Width, second.Width);
        Assert.Equal(80, first.Height);
        Assert.Equal(90, second.Height);
        AssertCompleteGlyphs(first, new Rect(290, 85, 254, 30), 1);
        AssertCompleteGlyphs(second, new Rect(290, 85, 254, 30), 1);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>枚举六张真实秘密卡包详情在四种缩放下的独立字形与分隔符边界。</summary>
    /// <returns>真实截图、完整标题、免费标志、缩放比例及原图边界。</returns>
    public static IEnumerable<object[]> SecretHeaderCases()
    {
        (string File, string Title, bool Free, int Left, int Top, int Right, int Bottom, int JointRight)[] fixtures =
        [
            ("paid-details.png", "驱邪祓恶巳神传说", false, 290, 85, 545, 115, 278),
            ("paid-details-after-opening.png", "颠覆世界恶魔之力", false, 290, 85, 544, 115, 278),
            ("free-details-single-row.png", "颠覆世界恶魔之力", true, 290, 85, 544, 115, 278),
            ("free-details-second-title.png", "于毁灭中觉醒", true, 291, 85, 480, 115, 278),
            ("free-details-top-edge.png", "黑之魔导师", true, 282, 23, 440, 53, 268),
            ("paid-details-fire-beast-after-opening.png", "猛火魔兽", false, 267, 48, 391, 78, 253)
        ];
        yield return ["paid-details.png", "驱邪祓恶巳神传说", false, .5, 290, 85, 545, 115, 278];
        foreach (var fixture in fixtures)
            foreach (var scale in new[] { .65, .85, 1, 1.25 })
                yield return [fixture.File, fixture.Title, fixture.Free, scale, fixture.Left, fixture.Top,
                    fixture.Right, fixture.Bottom, fixture.JointRight];
    }

    /// <summary>真实秘密类别的联合右界决定OCR起点，标题首末字完整且调用方像素不变。</summary>
    /// <param name="fixture">真实详情截图资源名。</param>
    /// <param name="title">按原图人工核验的完整标题。</param>
    /// <param name="free">原图是否具有免费入口。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    /// <param name="left">原图标题字形左界。</param>
    /// <param name="top">原图标题字形上界。</param>
    /// <param name="right">原图标题字形排他右界。</param>
    /// <param name="bottom">原图标题字形排他下界。</param>
    /// <param name="jointRight">独立定位的类别与分隔符联合排他右界。</param>
    [Theory]
    [MemberData(nameof(SecretHeaderCases))]
    public void RealSecretHeaderFeedsCompleteTitleFromItsRightBoundary(string fixture, string title, bool free,
        double scale, int left, int top, int right, int bottom, int jointRight)
    {
        using var original = LoadFrame(fixture);
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var reader = new RecordingTitleReader(title);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(), titleReader: reader);

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        Assert.Equal(free, observation.FreeOffer);
        Assert.Equal(free, observation.PrimaryTarget.HasValue);
        Assert.NotNull(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
        var region = FindCopiedTitleRegion(frame, Assert.Single(reader.Calls));
        AssertJointRight(region, jointRight, scale);
        AssertCompleteGlyphs(region, new Rect(left, top, right - left, bottom - top), scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>普通类别仅替换原图中的类别联合区域，标题与全部购买及导航像素保持原位。</summary>
    /// <param name="scale">输入截图的缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void RealNormalCategoryKeepsTheUnchangedPackTitle(double scale)
    {
        using var original = LoadFrame("paid-details.png");
        using var normal = LoadTemplate("header-normal.png");
        using (var destination = new Mat(original, new Rect(120, 80, 158, 43))) normal.CopyTo(destination);
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        const string title = "驱邪祓恶巳神传说";
        var reader = new RecordingTitleReader(title);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(), titleReader: reader);

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        var region = FindCopiedTitleRegion(frame, Assert.Single(reader.Calls));
        AssertJointRight(region, 278, scale);
        AssertCompleteGlyphs(region, new Rect(290, 85, 255, 30), scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>联合前缀左右移动后，OCR左界应跟随联合锚点，原标题与其余界面锚点保持不动。</summary>
    /// <param name="offset">类别联合区域相对原位置的水平位移。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    [Theory]
    [InlineData(-48, .65)]
    [InlineData(-48, .85)]
    [InlineData(-48, 1)]
    [InlineData(-48, 1.25)]
    [InlineData(8, .65)]
    [InlineData(8, .85)]
    [InlineData(8, 1)]
    [InlineData(8, 1.25)]
    public void ShiftedJointHeaderMovesOnlyTheOcrBoundary(int offset, double scale)
    {
        using var original = LoadFrame("paid-details.png");
        using var jointView = new Mat(original, new Rect(120, 80, 158, 43));
        using var joint = jointView.Clone();
        PaintBackground(original, new Rect(120, 80, 158, 43));
        using (var destination = new Mat(original, new Rect(120 + offset, 80, 158, 43))) joint.CopyTo(destination);
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var reader = new RecordingTitleReader("驱邪祓恶巳神传说");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(), titleReader: reader);

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("驱邪祓恶巳神传说", observation.PackTitle);
        Assert.NotNull(observation.NextTarget);
        var region = FindCopiedTitleRegion(frame, Assert.Single(reader.Calls));
        AssertJointRight(region, 278 + offset, scale);
        AssertCompleteGlyphs(region, new Rect(290, 85, 255, 30), scale);
    }

    /// <summary>枚举缺类别、缺联合锚点、缺分隔符、残缺类别及错误分隔符位置的四档缩放。</summary>
    /// <returns>损坏类型与缩放比例。</returns>
    public static IEnumerable<object[]> MissingHeaderCases()
    {
        foreach (var damage in new[] { "only-separator", "missing-joint", "missing-separator", "partial-category", "separator-in-title" })
            foreach (var scale in new[] { .65, .85, 1, 1.25 })
                yield return [damage, scale];
    }

    /// <summary>真实标题和免费入口完整但联合锚点损坏时，应在调用标题及费用OCR前撤回全部动作。</summary>
    /// <param name="damage">仅施加于类别或分隔符的损坏类型。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    [Theory]
    [MemberData(nameof(MissingHeaderCases))]
    public void MissingCompleteHeaderStopsBeforeOcrOrAnyAction(string damage, double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        switch (damage)
        {
            case "only-separator":
                PaintBackground(original, new Rect(120, 80, 145, 43));
                break;
            case "missing-joint":
                PaintBackground(original, new Rect(120, 80, 158, 43));
                break;
            case "partial-category":
                PaintBackground(original, new Rect(120, 80, 38, 43));
                break;
            case "separator-in-title":
                using (var source = new Mat(original, new Rect(270, 84, 2, 35)))
                using (var stroke = source.Clone())
                using (var destination = new Mat(original, new Rect(284, 84, 2, 35))) stroke.CopyTo(destination);
                PaintBackground(original, new Rect(265, 80, 13, 43));
                break;
            default:
                PaintBackground(original, new Rect(265, 80, 13, 43));
                break;
        }
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        var reader = new RecordingTitleReader("颠覆世界恶魔之力");
        var fee = new RecordingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fee, titleReader: reader);

        var observation = subject.Recognize(frame);

        AssertUnknown(observation);
        Assert.Empty(reader.Calls);
        Assert.Equal(0, fee.Calls);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>完整四字类别中的任意一个字缺失时，剩余类别与分隔符不得授权标题或免费动作。</summary>
    /// <param name="left">单个类别字形擦除区域的左界。</param>
    /// <param name="width">覆盖该字完整字形的宽度。</param>
    [Theory]
    [InlineData(120, 38)]
    [InlineData(155, 34)]
    [InlineData(189, 32)]
    [InlineData(220, 34)]
    public void EveryCategoryCharacterIsRequiredForTheCompleteAnchor(int left, int width)
    {
        foreach (var normalCategory in new[] { false, true })
        {
            using var image = LoadFrame("free-details-single-row.png");
            if (normalCategory)
            {
                using var normal = LoadTemplate("header-normal.png");
                using var destination = new Mat(image, new Rect(120, 80, 158, 43));
                normal.CopyTo(destination);
            }
            PaintBackground(image, new Rect(left, 80, width, 43));
            var reader = new RecordingTitleReader("颠覆世界恶魔之力");
            var fee = new RecordingFeeVerifier();
            using var subject = new OpenCvPackRecognizer(feeVerifier: fee, titleReader: reader);

            AssertUnknown(subject.Recognize(ToFrame(image)));
            Assert.Empty(reader.Calls);
            Assert.Equal(0, fee.Calls);
        }
    }

    /// <summary>低于二十物理像素的类别锚点在不同采样相位下均不得授权标题或费用OCR。</summary>
    /// <param name="scale">将真实完整类别缩小到最低支持高度以下的图像比例。</param>
    [Theory]
    [InlineData(.25)]
    [InlineData(.35)]
    [InlineData(.4)]
    [InlineData(.45)]
    public void TinyHeaderStopsBeforeTitleOrFeeOcr(double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using var image = Resize(original, scale);
        var reader = new RecordingTitleReader("颠覆世界恶魔之力");
        var fee = new RecordingFeeVerifier();
        var logger = new HeaderLogger();
        using var subject = new OpenCvPackRecognizer(logger, fee, reader);

        AssertUnknown(subject.Recognize(ToFrame(image)));
        Assert.Empty(reader.Calls);
        Assert.Equal(0, fee.Calls);
        if (scale > .25) Assert.Equal(new[] { "BelowMinimumSize" }, logger.Reasons);
    }

    /// <summary>只暗化真实标题前缀而菜单和包名仍清晰时，类别锚点的亮度门禁须阻止免费动作。</summary>
    /// <param name="scale">输入截图的缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void DimmedHeaderAloneStopsBeforeTitleOrFeeOcr(double scale)
    {
        using var original = LoadFrame("free-details-single-row.png");
        using (var header = new Mat(original, new Rect(120, 80, 158, 43)))
        using (var source = header.Clone())
        {
            source.ConvertTo(header, MatType.CV_8UC4, .2);
            var height = header.Height;
            var width = header.Width;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var pixel = header.At<Vec4b>(y, x);
                    pixel.Item3 = 255;
                    header.Set(y, x, pixel);
                }
        }
        using var image = Resize(original, scale);
        var reader = new RecordingTitleReader("颠覆世界恶魔之力");
        var fee = new RecordingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fee, titleReader: reader);

        AssertUnknown(subject.Recognize(ToFrame(image)));
        Assert.Empty(reader.Calls);
        Assert.Equal(0, fee.Calls);
    }

    /// <summary>将真实竖笔复制到标题首尾的受控位置，标题里的合法丨须完整保留且不得被选作界面分隔符。</summary>
    /// <param name="scale">输入截图的缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void LegalLeadingAndInternalVerticalRuneRemainInsideTitlePixels(double scale)
    {
        using var original = LoadFrame("paid-details.png");
        using var source = new Mat(original, new Rect(270, 84, 2, 35));
        using var stroke = source.Clone();
        foreach (var x in new[] { 284, 580 })
            using (var destination = new Mat(original, new Rect(x, 84, 2, 35))) stroke.CopyTo(destination);
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        const string title = "丨驱邪祓恶巳神传说丨";
        var reader = new RecordingTitleReader(title);
        using var subject = new OpenCvPackRecognizer(feeVerifier: new RecordingFeeVerifier(), titleReader: reader);

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        var region = FindCopiedTitleRegion(frame, Assert.Single(reader.Calls));
        AssertJointRight(region, 278, scale);
        AssertCompleteGlyphs(region, new Rect(284, 84, 298, 35), scale);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>类别与主体缩放不一致导致完整标题区域越界时，撤回身份与动作而不裁读局部标题。</summary>
    [Fact]
    public void MixedHeaderScaleRejectsAnIncompleteTitleRegion()
    {
        using var image = LoadFrame("paid-details.png");
        using var header = LoadTemplate("header-secret.png");
        using var enlarged = Resize(header, 2);
        using (var destination = new Mat(image, new Rect(120, 80, enlarged.Width, enlarged.Height)))
            enlarged.CopyTo(destination);
        var reader = new RecordingTitleReader("残缺标题");
        var fee = new RecordingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fee, titleReader: reader);

        AssertUnknown(subject.Recognize(ToFrame(image)));
        Assert.Empty(reader.Calls);
        Assert.Equal(0, fee.Calls);
    }

    /// <summary>真实大蛇咒缚详情须经原生中文OCR建立完整标题与免费入口，不得因空检测停在未知画面。</summary>
    /// <param name="scale">现场客户区截图的四档缩放比例。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void NativeOcrReadsTheRealSerpentTitleAndFreeEntry(double scale)
    {
        using var original = LoadFrame("free-details-serpent-live.png");
        using var image = Resize(original, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        using var subject = new OpenCvPackRecognizer();

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal("大蛇咒缚", observation.PackTitle);
        Assert.True(observation.FreeOffer);
        Assert.NotNull(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>普通类别原暗图仍是付费购买弹窗，加入类别模板不得将其降级为可点击详情。</summary>
    [Fact]
    public void RealDimmedNormalPurchaseDialogKeepsItsPurchaseGate()
    {
        using var image = LoadFrame("paid-confirm.png");
        var reader = new RecordingTitleReader("欲望变革");
        using var subject = new OpenCvPackRecognizer(titleReader: reader);

        var observation = subject.Recognize(ToFrame(image));

        Assert.Equal(PackScreen.UnverifiedPurchaseDialog, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Empty(reader.Calls);
    }

    /// <summary>核验OCR左界随已由原图人工标注的联合右界移动，只允许多尺度像素取整容差。</summary>
    /// <param name="region">实际传给OCR的原图区域。</param>
    /// <param name="jointRight">原始截图中的联合排他右界。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    private static void AssertJointRight(Rect region, int jointRight, double scale)
    {
        var tolerance = (int)Math.Ceiling(3 * scale);
        var expected = (int)Math.Round(jointRight * scale);
        Assert.InRange(region.Left, expected - tolerance, expected + tolerance);
    }

    /// <summary>核验实际OCR区域包含人工标注的完整标题及受控合法竖笔。</summary>
    /// <param name="region">实际OCR区域。</param>
    /// <param name="glyphs">原图完整字形的独立包围矩形。</param>
    /// <param name="scale">输入截图的缩放比例。</param>
    private static void AssertCompleteGlyphs(Rect region, Rect glyphs, double scale)
    {
        Assert.True(region.Contains(new Point((int)Math.Floor(glyphs.Left * scale), (int)Math.Floor(glyphs.Top * scale))),
            "OCR区域须完整保留标题首字及顶部。");
        Assert.True(region.Contains(new Point((int)Math.Ceiling(glyphs.Right * scale) - 1,
            (int)Math.Ceiling(glyphs.Bottom * scale) - 1)), "OCR区域须完整保留标题末字及底部。");
    }

    /// <summary>通过逐行字节匹配独立找回实际OCR原图区域，避免依赖生产内部坐标或日志。</summary>
    /// <param name="frame">识别器收到的完整BGRA帧。</param>
    /// <param name="title">文字边界收到的独立BGRA副本。</param>
    /// <returns>逐行像素完全一致的原图矩形。</returns>
    private static Rect FindCopiedTitleRegion(GameFrame frame, TitleCall title)
    {
        Assert.Equal(title.Width * title.Height * 4, title.Pixels.Length);
        for (var y = 0; y <= Math.Min(frame.Height - title.Height, frame.Height / 6); y++)
            for (var x = 0; x <= Math.Min(frame.Width - title.Width, frame.Width * 2 / 5); x++)
            {
                var matched = true;
                for (var row = 0; row < title.Height && matched; row++)
                    matched = frame.Pixels.AsSpan(((y + row) * frame.Width + x) * 4, title.Width * 4)
                        .SequenceEqual(title.Pixels.AsSpan(row * title.Width * 4, title.Width * 4));
                if (matched) return new Rect(x, y, title.Width, title.Height);
            }
        Assert.Fail("标题像素必须逐行来自输入截图，且位于真实标题栏。");
        return default;
    }

    /// <summary>用邻近真实背景像素擦除仅属于类别或分隔符的指定矩形。</summary>
    /// <param name="image">独立持有像素的测试图像。</param>
    /// <param name="region">需擦除的原图矩形。</param>
    private static void PaintBackground(Mat image, Rect region)
    {
        var pixel = image.At<Vec4b>(80, 900);
        using var destination = new Mat(image, region);
        destination.SetTo(new Scalar(pixel.Item0, pixel.Item1, pixel.Item2, pixel.Item3));
    }

    /// <summary>读取测试程序集中的真实截图并解码为连续BGRA。</summary>
    /// <param name="name">真实截图文件名。</param>
    /// <returns>调用方负责释放的原生图像。</returns>
    private static Mat LoadFrame(string name) => LoadPng(typeof(PackHeaderAnchorTests).Assembly,
        $"MasterDuelSwitcher.Tests.Assets.PackFrames.{name}");

    /// <summary>记录结构化拒绝原因，防止小字号回归在其他前置门禁提前结束而表面通过。</summary>
    private sealed class HeaderLogger : ILogger<OpenCvPackRecognizer>
    {
        /// <summary>每次类别门禁拒绝时记录的实际原因。</summary>
        public List<string> Reasons { get; } = [];

        /// <summary>测试记录不创建作用域。</summary>
        /// <param name="state">日志作用域状态。</param>
        /// <returns>空作用域。</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>接收全部日志等级以保留Debug门禁证据。</summary>
        /// <param name="logLevel">当前日志等级。</param>
        /// <returns>始终启用日志。</returns>
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>从结构化日志状态中仅提取门禁的Reason字段。</summary>
        /// <param name="logLevel">当前日志等级。</param>
        /// <param name="eventId">当前事件标识。</param>
        /// <param name="state">真实结构化日志状态。</param>
        /// <param name="exception">当前异常。</param>
        /// <param name="formatter">日志文字格式化器。</param>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties) return;
            foreach (var property in properties)
                if (property.Key == "Reason" && property.Value is string reason) Reasons.Add(reason);
        }
    }

    /// <summary>读取生产程序集中的真实裁剪模板，不访问运行游戏。</summary>
    /// <param name="name">真实裁剪模板文件名。</param>
    /// <returns>调用方负责释放的模板BGRA图像。</returns>
    private static Mat LoadTemplate(string name) => LoadPng(typeof(OpenCvPackRecognizer).Assembly,
        $"MasterDuelSwitcher.Core.Assets.PackTemplates.{name}");

    /// <summary>从指定嵌入资源读取PNG并统一为BGRA像素布局。</summary>
    /// <param name="assembly">持有嵌入资源的程序集。</param>
    /// <param name="name">嵌入资源的完整名称。</param>
    /// <returns>调用方负责释放的连续BGRA图像。</returns>
    private static Mat LoadPng(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var bgr = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
        Assert.False(bgr.Empty());
        var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    /// <summary>使用实际OpenCV插值生成四档缩放图像。</summary>
    /// <param name="original">真实原图或仅修改类别的受控原图。</param>
    /// <param name="scale">图像缩放比例。</param>
    /// <returns>调用方负责释放的缩放图像。</returns>
    private static Mat Resize(Mat original, double scale)
    {
        var result = new Mat();
        Cv2.Resize(original, result, new Size((int)Math.Round(original.Width * scale),
            (int)Math.Round(original.Height * scale)), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return result;
    }

    /// <summary>复制连续BGRA构造离线游戏帧，保留输入像素检查所需的独立数组。</summary>
    /// <param name="image">连续BGRA图像。</param>
    /// <returns>完全在内存中构造的截图帧。</returns>
    private static GameFrame ToFrame(Mat image)
    {
        Assert.True(image.IsContinuous());
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(1, image.Width, image.Height, 0, 0, pixels, CapturedAt);
    }

    /// <summary>核验缺失完整标题锚点时既无身份，也无免费或导航动作授权。</summary>
    /// <param name="observation">真实识别器返回的观察结果。</param>
    private static void AssertUnknown(PackObservation observation)
    {
        Assert.Equal(PackScreen.Unknown, observation.Screen);
        Assert.Empty(observation.PackTitle);
        Assert.Empty(observation.Fingerprint);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
    }

    /// <summary>一次标题OCR边界接收到的实际紧密像素。</summary>
    /// <param name="Pixels">独立BGRA字节。</param>
    /// <param name="Width">区域物理宽度。</param>
    /// <param name="Height">区域物理高度。</param>
    private sealed record TitleCall(byte[] Pixels, int Width, int Height);

    /// <summary>隔离系统OCR并记录真实区域，供标题边界而非伪造文字定位断言使用。</summary>
    /// <param name="text">指定的原始完整标题。</param>
    /// <param name="secondText">可选的第二次独立识别结果。</param>
    private sealed class RecordingTitleReader(string text, string? secondText = null) : IPackTextReader
    {
        /// <summary>识别器实际提交的全部标题区域。</summary>
        public List<TitleCall> Calls { get; } = [];

        /// <summary>保存实际提交的独立像素并返回指定完整文字。</summary>
        /// <param name="bgraPixels">实际OCR区域像素。</param>
        /// <param name="width">区域宽度。</param>
        /// <param name="height">区域高度。</param>
        /// <returns>测试指定的完整Unicode文字。</returns>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            Calls.Add(new(bgraPixels.ToArray(), width, height));
            return Calls.Count > 1 && secondText is not null ? secondText : text;
        }
    }

    /// <summary>只隔离系统费用OCR，保留真实按钮与模板验证，并记录是否越过标题门禁。</summary>
    private sealed class RecordingFeeVerifier : IPackFeeVerifier
    {
        /// <summary>识别器实际调用费用边界的次数。</summary>
        public int Calls { get; private set; }

        /// <summary>记录费用边界调用并为已通过真实按钮验证的测试入口提供免费文字证据。</summary>
        /// <param name="bgraPixels">真实费用区域像素。</param>
        /// <param name="width">费用区域宽度。</param>
        /// <param name="height">费用区域高度。</param>
        /// <param name="gemDetected">真实局部宝石模板是否命中。</param>
        /// <returns>隔离标题测试所需的固定免费文字证据。</returns>
        public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected)
        {
            Calls++;
            return true;
        }
    }
}
