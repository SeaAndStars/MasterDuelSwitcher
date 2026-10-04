using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using OpenCvSharp;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用真实截图及OpenCV验证标题OCR单槽缓存，费用和界面门禁仍逐帧独立检查。</summary>
public sealed class PackTitleRecognitionCacheTests
{
    /// <summary>离线截图固定捕获时间，避免测试依赖游戏和系统时钟。</summary>
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>同字形三帧只读取一次标题，第三帧收费证据及时撤回免费动作而保留导航。</summary>
    [Fact]
    public void SameGlyphReusesTitleButChecksFeeOnEveryFrame()
    {
        using var image = LoadFrame("free-details-single-row.png");
        var frame = ToFrame(image);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        var fees = new CountingFeeVerifier(true, true, false);
        using var subject = new OpenCvPackRecognizer(feeVerifier: fees, titleReader: reader);

        var first = subject.Recognize(frame);
        var second = subject.Recognize(frame);
        var third = subject.Recognize(frame);

        AssertFreeDetails(first, "颠覆世界恶魔之力");
        AssertFreeDetails(second, "颠覆世界恶魔之力");
        Assert.Equal(PackScreen.PackDetails, third.Screen);
        Assert.Equal("颠覆世界恶魔之力", third.PackTitle);
        Assert.False(third.FreeOffer);
        Assert.Null(third.PrimaryTarget);
        Assert.NotNull(third.NextTarget);
        Assert.Equal(first.TitleVisualSignature, second.TitleVisualSignature);
        Assert.Equal(first.TitleVisualSignature, third.TitleVisualSignature);
        Assert.Equal(3, fees.Calls);
        Assert.Single(reader.Calls);
    }

    /// <summary>真实不同卡包各自读取标题，返回首包时单槽位已替换而必须重新读取。</summary>
    [Fact]
    public void DifferentRealPacksReplaceTheSingleTitleSlot()
    {
        using var firstImage = LoadFrame("free-details-single-row.png");
        using var secondImage = LoadFrame("free-details-second-title.png");
        var firstFrame = ToFrame(firstImage);
        var secondFrame = ToFrame(secondImage);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        var fees = new CountingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fees, titleReader: reader);

        var first = subject.Recognize(firstFrame);
        var firstRepeat = subject.Recognize(firstFrame);
        reader.Text = "于毁灭中觉醒";
        var second = subject.Recognize(secondFrame);
        var secondRepeat = subject.Recognize(secondFrame);
        reader.Text = "颠覆世界恶魔之力";
        var returned = subject.Recognize(firstFrame);

        AssertFreeDetails(first, "颠覆世界恶魔之力");
        AssertFreeDetails(firstRepeat, "颠覆世界恶魔之力");
        AssertFreeDetails(second, "于毁灭中觉醒");
        AssertFreeDetails(secondRepeat, "于毁灭中觉醒");
        AssertFreeDetails(returned, "颠覆世界恶魔之力");
        Assert.NotEqual(first.TitleVisualSignature, second.TitleVisualSignature);
        Assert.Equal(first.TitleVisualSignature, returned.TitleVisualSignature);
        Assert.Equal(5, fees.Calls);
        Assert.Equal(3, reader.Calls.Count);
    }

    /// <summary>同一窗口几何内白字像素变化即重新读取，随后同字形重复才复用新的结果。</summary>
    [Fact]
    public void ChangedWhiteGlyphForcesANewTitleRead()
    {
        using var image = LoadFrame("free-details-single-row.png");
        var firstFrame = ToFrame(image);
        RemoveOneWhiteGlyphPixel(image);
        var changedFrame = ToFrame(image);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new CountingFeeVerifier(), titleReader: reader);

        var first = subject.Recognize(firstFrame);
        var changed = subject.Recognize(changedFrame);
        var repeated = subject.Recognize(changedFrame);

        AssertFreeDetails(first, "颠覆世界恶魔之力");
        AssertFreeDetails(changed, "颠覆世界恶魔之力");
        AssertFreeDetails(repeated, "颠覆世界恶魔之力");
        Assert.NotEqual(first.TitleVisualSignature, changed.TitleVisualSignature);
        Assert.Equal(changed.TitleVisualSignature, repeated.TitleVisualSignature);
        Assert.Equal(2, reader.Calls.Count);
    }

    /// <summary>窗口句柄、屏幕原点或客户区任一尺寸变化时，相同白字也须重新OCR。</summary>
    /// <param name="change">独立变化的几何字段名称。</param>
    [Theory]
    [InlineData("handle")]
    [InlineData("screen-x")]
    [InlineData("screen-y")]
    [InlineData("width")]
    [InlineData("height")]
    public void ChangedFrameGeometryInvalidatesTheTitleSlot(string change)
    {
        using var image = LoadFrame("free-details-single-row.png");
        using var enlarged = new Mat();
        Cv2.CopyMakeBorder(image, enlarged, 0, change == "height" ? 1 : 0,
            0, change == "width" ? 1 : 0, BorderTypes.Constant, Scalar.Black);
        var firstFrame = ToFrame(image);
        var changedFrame = ToFrame(enlarged, change == "handle" ? 2 : 1,
            change == "screen-x" ? 101 : 0, change == "screen-y" ? 151 : 0);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        using var subject = new OpenCvPackRecognizer(feeVerifier: new CountingFeeVerifier(), titleReader: reader);

        var first = subject.Recognize(firstFrame);
        var firstRepeat = subject.Recognize(firstFrame);
        var changed = subject.Recognize(changedFrame);
        var changedRepeat = subject.Recognize(changedFrame);

        AssertFreeDetails(first, "颠覆世界恶魔之力");
        AssertFreeDetails(firstRepeat, "颠覆世界恶魔之力");
        AssertFreeDetails(changed, "颠覆世界恶魔之力");
        AssertFreeDetails(changedRepeat, "颠覆世界恶魔之力");
        Assert.Equal(first.TitleVisualSignature, changed.TitleVisualSignature);
        Assert.Equal(2, reader.Calls.Count);
    }

    /// <summary>第一次两次高度采样均空时不缓存，下一帧成功后才能复用非空标题。</summary>
    [Fact]
    public void EmptyOcrResultIsRetriedBeforeCachingTheSuccessfulTitle()
    {
        using var image = LoadFrame("free-details-single-row.png");
        var frame = ToFrame(image);
        var reader = new CountingTitleReader("颠覆世界恶魔之力", "", "");
        var fees = new CountingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fees, titleReader: reader);

        var empty = subject.Recognize(frame);
        var successful = subject.Recognize(frame);
        var repeated = subject.Recognize(frame);

        AssertUnknown(empty);
        AssertFreeDetails(successful, "颠覆世界恶魔之力");
        AssertFreeDetails(repeated, "颠覆世界恶魔之力");
        Assert.Equal(2, fees.Calls);
        Assert.Equal(3, reader.Calls.Count);
    }

    /// <summary>标题仍有灰度前景但没有强白字形时，每帧独立读取而不建立空签名缓存。</summary>
    [Fact]
    public void EmptyWhiteGlyphSignatureNeverCachesAnOcrTitle()
    {
        using var image = LoadFrame("free-details-single-row.png");
        DimWhiteTitleGlyphs(image);
        var frame = ToFrame(image);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        var fees = new CountingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fees, titleReader: reader);

        var first = subject.Recognize(frame);
        var second = subject.Recognize(frame);

        Assert.Equal(PackScreen.PackDetails, first.Screen);
        Assert.Equal(PackScreen.PackDetails, second.Screen);
        Assert.Equal("颠覆世界恶魔之力", first.PackTitle);
        Assert.Equal("颠覆世界恶魔之力", second.PackTitle);
        Assert.Empty(first.TitleVisualSignature);
        Assert.Empty(second.TitleVisualSignature);
        Assert.Equal(2, fees.Calls);
        Assert.Equal(2, reader.Calls.Count);
    }

    /// <summary>预热标题缓存后，破损完整类别或分隔符仍在任何文字及费用边界前撤回全部动作。</summary>
    /// <param name="damage">破损锚点的类别。</param>
    [Theory]
    [InlineData("category")]
    [InlineData("separator")]
    public void DamagedHeaderCannotReuseACachedTitleToAuthorizeActions(string damage)
    {
        using var image = LoadFrame("free-details-single-row.png");
        var validFrame = ToFrame(image);
        var reader = new CountingTitleReader("颠覆世界恶魔之力");
        var fees = new CountingFeeVerifier();
        using var subject = new OpenCvPackRecognizer(feeVerifier: fees, titleReader: reader);
        AssertFreeDetails(subject.Recognize(validFrame), "颠覆世界恶魔之力");
        var region = damage == "category" ? new Rect(120, 80, 38, 43) : new Rect(265, 80, 13, 43);
        using (var area = new Mat(image, region)) area.SetTo(new Scalar(12, 12, 12, 255));

        var invalid = subject.Recognize(ToFrame(image));

        AssertUnknown(invalid);
        Assert.Single(reader.Calls);
        Assert.Equal(1, fees.Calls);
    }

    /// <summary>确认真实详情身份及免费动作完整，并要求存在可用于缓存的非空字形签名。</summary>
    /// <param name="observation">实际识别器返回的观察结果。</param>
    /// <param name="title">按真实截图核对的完整标题。</param>
    private static void AssertFreeDetails(PackObservation observation, string title)
    {
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        Assert.True(observation.FreeOffer);
        Assert.NotNull(observation.PrimaryTarget);
        Assert.NotNull(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
        Assert.Equal(64, observation.TitleVisualSignature.Length);
    }

    /// <summary>标题或类别门禁失效时，不得返回任何身份或可执行动作。</summary>
    /// <param name="observation">实际识别器返回的观察结果。</param>
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

    /// <summary>将标题中第一个真实强白字像素降低亮度，独立制造同文字的字形签名变化。</summary>
    /// <param name="image">独立解码的真实BGRA截图。</param>
    private static void RemoveOneWhiteGlyphPixel(Mat image)
    {
        for (var y = 85; y < 115; y++)
            for (var x = 290; x < 544; x++)
            {
                var pixel = image.At<Vec4b>(y, x);
                if (pixel.Item0 < 240 || pixel.Item1 < 240 || pixel.Item2 < 240) continue;
                image.Set(y, x, new Vec4b(12, 12, 12, pixel.Item3));
                return;
            }
        throw new InvalidOperationException("真实标题区域未找到强白字像素。");
    }

    /// <summary>保留真实文字二维形状和灰度前景，只降低强白字亮度以制造空白字签名。</summary>
    /// <param name="image">独立解码的真实BGRA截图。</param>
    private static void DimWhiteTitleGlyphs(Mat image)
    {
        for (var y = 60; y < 140; y++)
            for (var x = 280; x < 1280; x++)
            {
                var pixel = image.At<Vec4b>(y, x);
                if (pixel.Item0 < 210 || pixel.Item1 < 210 || pixel.Item2 < 210) continue;
                image.Set(y, x, new Vec4b(180, 180, 180, pixel.Item3));
            }
    }

    /// <summary>解码嵌入的真实PNG截图为连续BGRA，不访问运行游戏或桌面。</summary>
    /// <param name="name">真实截图资源文件名。</param>
    /// <returns>调用方负责释放的BGRA图像。</returns>
    private static Mat LoadFrame(string name)
    {
        using var stream = typeof(PackTitleRecognitionCacheTests).Assembly.GetManifestResourceStream(
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

    /// <summary>复制真实图像像素构建离线帧，可独立改变句柄及屏幕原点以验证几何失效。</summary>
    /// <param name="image">连续排列的BGRA图像。</param>
    /// <param name="handle">离线目标窗口句柄标识。</param>
    /// <param name="screenX">客户区屏幕横坐标。</param>
    /// <param name="screenY">客户区屏幕纵坐标。</param>
    /// <returns>不对应任何真实输入操作的内存游戏帧。</returns>
    private static GameFrame ToFrame(Mat image, nint handle = 1, int screenX = 0, int screenY = 0)
    {
        Assert.True(image.IsContinuous());
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(handle, image.Width, image.Height, screenX, screenY, pixels, CapturedAt);
    }

    /// <summary>只隔离系统标题OCR，保留真实区域像素并计数生产识别器实际调用。</summary>
    private sealed class CountingTitleReader : IPackTextReader
    {
        /// <summary>指定的前置读取结果，用于模拟首次系统OCR为空。</summary>
        private readonly Queue<string> initialResults;
        /// <summary>当前真实截图对应的完整文字，由测试切换真实卡包时独立指定。</summary>
        public string Text { get; set; }
        /// <summary>实际提交至标题OCR边界的独立像素，用于检查真实调用次数。</summary>
        public List<byte[]> Calls { get; } = [];

        /// <summary>设置当前完整标题及可选的前置空识别结果。</summary>
        /// <param name="text">真实截图对应的完整标题。</param>
        /// <param name="initialResults">先于固定标题返回的独立读取结果。</param>
        public CountingTitleReader(string text, params string[] initialResults)
        {
            Text = text;
            this.initialResults = new Queue<string>(initialResults);
        }

        /// <summary>保存实际紧密BGRA并返回对应标题，不承担模板定位或缓存判断。</summary>
        /// <param name="bgraPixels">生产识别器提交的真实标题区域像素。</param>
        /// <param name="width">区域物理像素宽度。</param>
        /// <param name="height">区域物理像素高度。</param>
        /// <returns>当前指定的OCR文字。</returns>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            Assert.Equal(width * height * 4, bgraPixels.Length);
            Calls.Add(bgraPixels.ToArray());
            return initialResults.Count > 0 ? initialResults.Dequeue() : Text;
        }
    }

    /// <summary>只隔离系统费用OCR，逐次返回独立费用结论而不保存免费授权。</summary>
    /// <param name="results">逐帧费用批准结果；空序列表示每帧都提供免费文字证据。</param>
    private sealed class CountingFeeVerifier(params bool[] results) : IPackFeeVerifier
    {
        /// <summary>调用方指定的逐帧费用结论。</summary>
        private readonly bool[] results = results;
        /// <summary>生产识别器实际提交费用区域的次数。</summary>
        public int Calls { get; private set; }

        /// <summary>核验费用区域完整布局并返回该帧的独立结论。</summary>
        /// <param name="bgraPixels">真实费用区域像素。</param>
        /// <param name="width">费用区域物理像素宽度。</param>
        /// <param name="height">费用区域物理像素高度。</param>
        /// <param name="gemDetected">当前帧局部宝石模板是否命中。</param>
        /// <returns>默认免费，指定序列则按当前调用返回结论。</returns>
        public bool IsFree(byte[] bgraPixels, int width, int height, bool gemDetected)
        {
            Assert.Equal(width * height * 4, bgraPixels.Length);
            var index = Calls++;
            return !gemDetected && (results.Length == 0 || results[Math.Min(index, results.Length - 1)]);
        }
    }
}
