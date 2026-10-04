using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using OpenCvSharp;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用两张真实红色提示截图和明确标注的派生帧，验证原生OCR及类别定位缓存门禁。</summary>
public sealed class AlertHeaderRobustnessTests
{
    /// <summary>本轮用户提供、按原始字节保存的绿色下一包截图资源。</summary>
    private const string NewAlert = "free-details-nature-selection-alert-green-next-full-window.png";
    /// <summary>此前保存的自然选择红色提示原截图资源。</summary>
    private const string OldAlert = "free-details-nature-selection-alert-full-window.png";
    /// <summary>无红色提示的颠覆世界恶魔之力原截图资源。</summary>
    private const string NoAlert = "free-details-single-row.png";

    /// <summary>枚举新旧原图、精确客户区与九档常见缩放，不以模拟OCR替代标题或费用。</summary>
    /// <returns>原图资源、是否裁取客户区以及当前图像缩放比例。</returns>
    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var fixture in new[] { NewAlert, OldAlert })
            foreach (var clientOnly in new[] { false, true })
                foreach (var scale in new[] { .65, .75, .85, .9, .9375, 1, 1.1, 1.25, 1.5 })
                    yield return [fixture, clientOnly, scale];
    }

    /// <summary>真实Windows标题及费用OCR须在冷暖两帧完整识别自然选择，并把动作点限制在实际按钮内。</summary>
    /// <param name="fixture">未经编辑的新或旧红色提示截图资源。</param>
    /// <param name="clientOnly">是否仅保留原像素客户区。</param>
    /// <param name="scale">当前输入图像相对于原截图的缩放比例。</param>
    [Theory]
    [MemberData(nameof(NativeCases))]
    public void NativeAlertFramesKeepExactTitlesAndRealTargets(string fixture, bool clientOnly, double scale)
    {
        using var original = LoadOriginal(fixture);
        using var image = Prepare(original, clientOnly, scale);
        var frame = ToFrame(image);
        var before = frame.Pixels.ToArray();
        using var subject = new OpenCvPackRecognizer();

        var cold = subject.Recognize(frame);
        var warm = subject.Recognize(frame);

        AssertFreeDetails(cold, "自然选择", clientOnly, scale, image);
        AssertFreeDetails(warm, "自然选择", clientOnly, scale, image);
        AssertSameEvidence(cold, warm);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>在相同几何下连续切换无提示、新提示、旧提示和无提示，每帧原生身份须与独立冷定位一致。</summary>
    /// <param name="scale">四档输入缩放之一；每步窗口几何保持一致。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void RealAlertTransitionsRebuildOnlyFromTheCurrentFrame(double scale)
    {
        using var subject = new OpenCvPackRecognizer();
        foreach (var fixture in new[] { NoAlert, NewAlert, OldAlert, NoAlert })
        {
            using var original = LoadOriginal(fixture);
            using var image = Prepare(original, false, scale);
            var frame = ToFrame(image);
            var before = frame.Pixels.ToArray();
            using var coldSubject = new OpenCvPackRecognizer();
            var cold = coldSubject.Recognize(frame);
            var current = subject.Recognize(frame);
            var expectedTitle = fixture == NoAlert ? "颠覆世界恶魔之力" : "自然选择";

            AssertFreeDetails(cold, expectedTitle, false, scale, image);
            AssertFreeDetails(current, expectedTitle, false, scale, image);
            AssertSameEvidence(cold, current);
            Assert.Equal(before, frame.Pixels);
        }
    }

    /// <summary>派生同标题帧仅将真实类别及标题左移五十三像素覆盖提示图标，出现和消失均须保持精确原生标题。</summary>
    /// <param name="scale">四档输入缩放之一；派生帧不作为独立现场录制。</param>
    [Theory]
    [InlineData(.65)]
    [InlineData(.85)]
    [InlineData(1)]
    [InlineData(1.25)]
    public void DerivedSameTitleAlertAppearanceKeepsTheNativeIdentity(double scale)
    {
        using var original = LoadOriginal(NewAlert);
        using var withoutAlert = CreateDerivedWithoutAlert(original);
        using var subject = new OpenCvPackRecognizer();
        foreach (var source in new[] { withoutAlert, original, withoutAlert })
        {
            using var image = Prepare(source, false, scale);
            var frame = ToFrame(image);
            var before = frame.Pixels.ToArray();
            using var coldSubject = new OpenCvPackRecognizer();
            var cold = coldSubject.Recognize(frame);
            var current = subject.Recognize(frame);

            AssertFreeDetails(cold, "自然选择", false, scale, image);
            AssertFreeDetails(current, "自然选择", false, scale, image);
            AssertSameEvidence(cold, current);
            Assert.Equal(before, frame.Pixels);
        }
    }

    /// <summary>暖缓存后遮挡完整类别或分隔符时，红色提示仍在也须撤回全部动作，并在OCR之前拒绝。</summary>
    /// <param name="removeSeparator">仅遮挡分隔符；否则遮挡类别四个字而保留红色图标。</param>
    /// <param name="scale">当前原图派生输入的缩放比例。</param>
    [Theory]
    [InlineData(false, .65)]
    [InlineData(false, .85)]
    [InlineData(false, 1)]
    [InlineData(false, 1.25)]
    [InlineData(true, .65)]
    [InlineData(true, .85)]
    [InlineData(true, 1)]
    [InlineData(true, 1.25)]
    public void MissingAlertHeaderWithdrawsWarmApprovalBeforeAnyOcr(bool removeSeparator, double scale)
    {
        using var original = LoadOriginal(NewAlert);
        using var changed = original.Clone();
        // 这两个遮挡框不覆盖原图中位于(125,84)附近的红色感叹号。
        using (var damaged = new Mat(changed, removeSeparator ? new Rect(316, 75, 20, 52)
            : new Rect(172, 75, 144, 52))) damaged.SetTo(new Scalar(0, 0, 0, 255));
        using var image = Prepare(original, false, scale);
        using var altered = Prepare(changed, false, scale);
        var titleReader = new GuardedNativeReader();
        var feeReader = new GuardedNativeReader();
        using var subject = new OpenCvPackRecognizer(feeVerifier: new OcrFreePackCostVerifier(feeReader),
            titleReader: titleReader);
        var cold = subject.Recognize(ToFrame(image));
        var warm = subject.Recognize(ToFrame(image));
        AssertFreeDetails(cold, "自然选择", false, scale, image);
        AssertFreeDetails(warm, "自然选择", false, scale, image);
        var titleCalls = titleReader.Calls;
        var feeCalls = feeReader.Calls;
        titleReader.RejectRead = true;
        feeReader.RejectRead = true;
        var frame = ToFrame(altered);
        var before = frame.Pixels.ToArray();

        var observation = subject.Recognize(frame);

        Assert.Equal(PackScreen.Unknown, observation.Screen);
        Assert.False(observation.FreeOffer);
        Assert.Null(observation.PrimaryTarget);
        Assert.Null(observation.NextTarget);
        Assert.Null(observation.AnimationSkipTarget);
        Assert.Empty(observation.PackTitle);
        Assert.Empty(observation.TitleVisualSignature);
        Assert.Equal(titleCalls, titleReader.Calls);
        Assert.Equal(feeCalls, feeReader.Calls);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>核验真实免费详情的原生标题、完整身份与独立人工确认的按钮区域。</summary>
    /// <param name="observation">当前生产识别器的完整观察。</param>
    /// <param name="title">从原截图人工核对的完整Unicode标题。</param>
    /// <param name="clientOnly">是否裁去一像素左边框及三十一像素标题栏。</param>
    /// <param name="scale">相对于原截图的输入缩放比例。</param>
    /// <param name="image">当前输入像素，用于核验实际帧边界。</param>
    private static void AssertFreeDetails(PackObservation observation, string title, bool clientOnly,
        double scale, Mat image)
    {
        Assert.Equal(PackScreen.PackDetails, observation.Screen);
        Assert.Equal(title, observation.PackTitle);
        Assert.True(observation.FreeOffer);
        Assert.Matches("\\A[0-9A-F]{64}\\z", observation.TitleVisualSignature);
        Assert.Null(observation.AnimationSkipTarget);
        AssertTargetInside(observation.PrimaryTarget, new Rect(1311, 897, 493, 81), clientOnly, scale, image);
        AssertTargetInside(observation.NextTarget, new Rect(1931, 541, 75, 129), clientOnly, scale, image);
    }

    /// <summary>比较冷暖或前后帧的实际动作和身份，不把浮点评分微差当作业务变化。</summary>
    /// <param name="expected">独立冷定位或首次识别得到的观察。</param>
    /// <param name="actual">同一输入在既有缓存中的识别结果。</param>
    private static void AssertSameEvidence(PackObservation expected, PackObservation actual)
    {
        Assert.Equal(expected.Screen, actual.Screen);
        Assert.Equal(expected.FreeOffer, actual.FreeOffer);
        Assert.Equal(expected.PackTitle, actual.PackTitle);
        Assert.Equal(expected.TitleVisualSignature, actual.TitleVisualSignature);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.PrimaryTarget, actual.PrimaryTarget);
        Assert.Equal(expected.NextTarget, actual.NextTarget);
        Assert.Equal(expected.AnimationSkipTarget, actual.AnimationSkipTarget);
    }

    /// <summary>把人工确认的原图动作区域映射到客户区和当前缩放，拒绝帧外或区域外坐标。</summary>
    /// <param name="target">识别返回的物理动作点。</param>
    /// <param name="original">人工及像素核验的原始按钮框。</param>
    /// <param name="clientOnly">是否移除窗口左边框和标题栏。</param>
    /// <param name="scale">输入图像的缩放比例。</param>
    /// <param name="image">当前输入像素。</param>
    private static void AssertTargetInside(PixelPoint? target, Rect original, bool clientOnly, double scale, Mat image)
    {
        var point = Assert.IsType<PixelPoint>(target);
        var left = original.Left - (clientOnly ? 1 : 0);
        var top = original.Top - (clientOnly ? 31 : 0);
        Assert.InRange(point.X, 0, image.Width - 1);
        Assert.InRange(point.Y, 0, image.Height - 1);
        Assert.InRange(point.X, (int)Math.Floor(left * scale), (int)Math.Ceiling((left + original.Width) * scale) - 1);
        Assert.InRange(point.Y, (int)Math.Floor(top * scale), (int)Math.Ceiling((top + original.Height) * scale) - 1);
    }

    /// <summary>只在内存移位真实类别和标题；保留按钮与其余像素，派生无提示同标题帧。</summary>
    /// <param name="original">本轮未经编辑的红色提示原截图。</param>
    /// <returns>由调用方释放的派生图；仅上方类别及标题带发生变化。</returns>
    private static Mat CreateDerivedWithoutAlert(Mat original)
    {
        var result = original.Clone();
        var source = new Rect(172, 75, 360, 52);
        using var area = new Mat(original, source);
        using var copied = area.Clone();
        using (var erased = new Mat(result, new Rect(119, 75, 413, 52)))
            erased.SetTo(new Scalar(0, 0, 0, 255));
        using (var destination = new Mat(result, new Rect(source.X - 53, source.Y, source.Width, source.Height)))
            copied.CopyTo(destination);
        return result;
    }

    /// <summary>按原像素裁取客户区后缩放；一倍输入直接复制，保留原始采样。</summary>
    /// <param name="original">完整原窗或已明确标注的派生BGRA图像。</param>
    /// <param name="clientOnly">是否仅保留固定原始客户区。</param>
    /// <param name="scale">输入缩放比例。</param>
    /// <returns>由调用方释放的连续四通道输入图。</returns>
    private static Mat Prepare(Mat original, bool clientOnly, double scale)
    {
        using var area = new Mat(original, clientOnly ? new Rect(1, 31, 2048, 1152)
            : new Rect(0, 0, original.Width, original.Height));
        if (scale == 1) return area.Clone();
        var result = new Mat();
        Cv2.Resize(area, result, new Size((int)Math.Round(area.Width * scale),
            (int)Math.Round(area.Height * scale)), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        return result;
    }

    /// <summary>从嵌入资源读取原始截图并转换为连续BGRA，不改磁盘素材。</summary>
    /// <param name="fixture">已按原始字节保存的截图资源名。</param>
    /// <returns>由调用方释放的2050乘1184原窗图像。</returns>
    private static Mat LoadOriginal(string fixture)
    {
        using var stream = typeof(AlertHeaderRobustnessTests).Assembly.GetManifestResourceStream(
            "MasterDuelSwitcher.Tests.Assets.PackFrames." + fixture);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var decoded = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
        Assert.Equal(2050, decoded.Width);
        Assert.Equal(1184, decoded.Height);
        var bgra = new Mat();
        Cv2.CvtColor(decoded, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }

    /// <summary>复制紧密BGRA像素为独立离线帧，各序列保持相同五项窗口几何。</summary>
    /// <param name="image">调用方持有的连续四通道图像。</param>
    /// <returns>独立像素数组和固定窗口几何组成的识别输入。</returns>
    private static GameFrame ToFrame(Mat image)
    {
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return new GameFrame(1, image.Width, image.Height, 0, 0, pixels, DateTimeOffset.UnixEpoch);
    }

    /// <summary>正例委托真实Windows OCR；锚点损坏后任何标题或费用读取直接使测试失败。</summary>
    private sealed class GuardedNativeReader : IPackTextReader
    {
        /// <summary>实际系统简体中文OCR读取器，不返回预设标题或免费文本。</summary>
        private readonly WindowsPackTextReader native = new();
        /// <summary>切换到负例后阻止任何OCR调用。</summary>
        public bool RejectRead { get; set; }
        /// <summary>真实读取总数，用于确认坏帧没有越过OCR前置门禁。</summary>
        public int Calls { get; private set; }

        /// <summary>正例原样调用系统OCR，负例拒绝越过完整类别及分隔符门禁。</summary>
        /// <param name="bgraPixels">生产识别器提交的原始紧密BGRA文字区域。</param>
        /// <param name="width">文字区域物理像素宽度。</param>
        /// <param name="height">文字区域物理像素高度。</param>
        /// <returns>实际Windows OCR原始结果。</returns>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            Calls++;
            if (RejectRead) throw new InvalidOperationException("缺少完整类别或分隔符时不应调用OCR。");
            return native.Read(bgraPixels, width, height);
        }
    }
}
