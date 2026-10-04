using System.Runtime.InteropServices;
using System.Text;
using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>从游戏客户区像素识别免费卡包操作界面。</summary>
public interface IPackRecognizer
{
    /// <summary>识别一帧像素；只有组合验证通过的目标才返回动作坐标。</summary>
    /// <param name="frame">目标游戏客户区的紧密 BGRA 像素帧。</param>
    /// <returns>识别到的界面及已经验证的客户区动作坐标。</returns>
    PackObservation Recognize(GameFrame frame);
}

/// <summary>使用原始截图模板、状态锚点和限定区域多尺度匹配识别免费卡包界面。</summary>
public sealed class OpenCvPackRecognizer : IPackRecognizer, IDisposable
{
    /// <summary>状态及按钮匹配必须达到的归一化相似度。</summary>
    private const double MatchThreshold = .84;
    /// <summary>原始截图模板对应的参考窗口宽度。</summary>
    private const double ReferenceWidth = 2050;
    /// <summary>用于低成本状态锚点搜索的标准缩放候选。</summary>
    private static readonly double[] StandardScales = [.5, .65, .75, .85, 1, 1.25, 1.5, 1.75, 2];
    /// <summary>嵌入截图解码后保留的灰度模板。</summary>
    private readonly Dictionary<string, Template> templates;
    /// <summary>记录界面和最低匹配分数的诊断日志。</summary>
    private readonly ILogger<OpenCvPackRecognizer> logger;
    /// <summary>对局部费用文字和宝石图标建立独立免费证据。</summary>
    private readonly IPackFeeVerifier feeVerifier;
    /// <summary>读取完整卡包标题，以精确文字建立卡包身份。</summary>
    private readonly IPackTextReader titleReader;
    /// <summary>原生模板资源是否已经释放。</summary>
    private bool disposed;

    /// <summary>读取嵌入的真实游戏截图模板，建立可重复使用的原生图像缓存。</summary>
    /// <param name="logger">识别诊断日志；省略时使用空日志。</param>
    /// <param name="feeVerifier">可注入的费用文字与宝石校验器；省略时使用系统中文识别。</param>
    /// <param name="titleReader">可注入的标题文字识别器；省略时使用系统中文识别。</param>
    public OpenCvPackRecognizer(ILogger<OpenCvPackRecognizer>? logger = null, IPackFeeVerifier? feeVerifier = null,
        IPackTextReader? titleReader = null)
    {
        this.logger = logger ?? NullLogger<OpenCvPackRecognizer>.Instance;
        this.feeVerifier = feeVerifier ?? new OcrFreePackCostVerifier(new WindowsPackTextReader());
        this.titleReader = titleReader ?? new WindowsPackTextReader();
        templates = new[]
        {
            LoadTemplate("details-label", 1336, 377), LoadTemplate("next-arrow", 1938, 551),
            LoadTemplate("free-entry-text", 1608, 844), LoadTemplate("one-pack-text", 1404, 844),
            LoadTemplate("dialog-title", 485, 245), LoadTemplate("dialog-free-text", 608, 382),
            LoadTemplate("dialog-cancel", 325, 480), LoadTemplate("dialog-purchase", 713, 480),
            LoadTemplate("dialog-gem", 938, 571),
            LoadTemplate("opening-label", 292, 853), LoadTemplate("skip-label", 1203, 953),
            LoadTemplate("results-title", 26, 82), LoadTemplate("results-confirm", 1690, 1073)
        }.ToDictionary(template => template.Name);
    }

    /// <summary>验证帧布局后，按弹窗、结果、开包和详情的优先级进行组合识别。</summary>
    /// <param name="frame">调用方持有的客户区紧密 BGRA 像素帧。</param>
    /// <returns>只有验证通过的目标才具有动作坐标。</returns>
    public PackObservation Recognize(GameFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Pixels);
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > int.MaxValue / 4)
            throw new ArgumentException("帧尺寸必须为正数且像素总量应在数组范围内。", nameof(frame));
        if (frame.Pixels.Length != frame.Width * frame.Height * 4)
            throw new ArgumentException("帧必须拥有逐行紧密排列的四通道 BGRA 像素。", nameof(frame));
        if (frame.Width < 128 || frame.Height < 100) return Unknown();
        using var bgra = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels);
        using var gray = new Mat();
        Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.MeanStdDev(gray, out _, out var deviation);
        if (deviation.Val0 < 5) return Unknown();
        var factor = Math.Min(1, 1280d / frame.Width);
        using var search = new Mat();
        Cv2.Resize(gray, search, new Size((int)Math.Round(gray.Width * factor), (int)Math.Round(gray.Height * factor)),
            0, 0, InterpolationFlags.Area);
        var observation = RecognizeDialog(search, bgra, factor) ?? RecognizeResults(search, factor)
            ?? RecognizeOpening(search, frame.Width, frame.Height, factor)
            ?? RecognizeDetails(search, gray, bgra, factor) ?? Unknown();
        logger.LogDebug("卡包画面识别：{Screen}，免费 {FreeOffer}，分数 {Confidence:F4}，卡图 {Fingerprint}，标题 {PackTitle}",
            observation.Screen, observation.FreeOffer, observation.Confidence, observation.Fingerprint, observation.PackTitle);
        return observation;
    }

    /// <summary>限定免费文字位于弹窗费用行，同时核验按钮、局部宝石图标和真实费用文字。</summary>
    private PackObservation? RecognizeDialog(Mat image, Mat color, double factor)
    {
        var title = FindAnchor(image, factor, "dialog-title", new Rect(image.Width / 5, image.Height / 8,
            image.Width * 3 / 5, image.Height * 5 / 8));
        if (title is null) return null;
        var cancel = FindRelative(image, factor, title.Value, "dialog-cancel");
        var purchase = FindRelative(image, factor, title.Value, "dialog-purchase");
        var anchor = title.Value;
        var feeRegion = new Rect((int)Math.Floor((anchor.Bounds.X - 370 * anchor.Scale) * factor),
            (int)Math.Floor((anchor.Bounds.Y + 123 * anchor.Scale) * factor),
            (int)Math.Ceiling(880 * anchor.Scale * factor), (int)Math.Ceiling(59 * anchor.Scale * factor));
        var free = Find(image, factor, templates["dialog-free-text"], feeRegion,
            new[] { anchor.Scale * .98, anchor.Scale, anchor.Scale * 1.02 });
        var gemHeight = cancel is null ? 320 * anchor.Scale
            : Math.Max(1, cancel.Value.Bounds.Y - anchor.Bounds.Y - 114 * anchor.Scale);
        var gemRegion = new Rect((int)Math.Floor((anchor.Bounds.X - 370 * anchor.Scale) * factor),
            (int)Math.Floor((anchor.Bounds.Y + 100 * anchor.Scale) * factor),
            (int)Math.Ceiling(880 * anchor.Scale * factor), (int)Math.Ceiling(gemHeight * factor));
        var gem = Find(image, factor, templates["dialog-gem"], gemRegion,
            new[] { anchor.Scale * .98, anchor.Scale, anchor.Scale * 1.02 });
        var feePixels = new Rect((int)Math.Floor(anchor.Bounds.X - 370 * anchor.Scale),
            (int)Math.Floor(anchor.Bounds.Y + 123 * anchor.Scale),
            (int)Math.Ceiling(880 * anchor.Scale), (int)Math.Ceiling(59 * anchor.Scale));
        if (cancel is null || purchase is null || free is null || !VerifyFreeFee(color, feePixels, gem is not null))
            return new(PackScreen.UnverifiedPurchaseDialog, null, null, false, string.Empty, title.Value.Score);
        return new(PackScreen.FreePurchaseDialog, purchase.Value.Center, null, true, string.Empty,
            Math.Min(title.Value.Score, Math.Min(cancel.Value.Score, Math.Min(purchase.Value.Score, free.Value.Score))));
    }

    /// <summary>结果确认必须与左上角卡包开封结果标题具有一致的几何关系。</summary>
    private PackObservation? RecognizeResults(Mat image, double factor)
    {
        var title = FindAnchor(image, factor, "results-title", new Rect(0, 0, image.Width / 2, image.Height / 4));
        if (title is null) return null;
        var confirm = FindRelative(image, factor, title.Value, "results-confirm");
        if (confirm is null) return null;
        return new(PackScreen.Results, confirm.Value.Center, null, false, string.Empty,
            Math.Min(title.Value.Score, confirm.Value.Score));
    }

    /// <summary>开包界面由打开文字确认，优先跳过动画，缺少跳过时使用打开按钮。</summary>
    private PackObservation? RecognizeOpening(Mat image, int width, int height, double factor)
    {
        var open = FindAnchor(image, factor, "opening-label", new Rect(0, image.Height * 2 / 3,
            image.Width, image.Height - image.Height * 2 / 3));
        if (open is null) return null;
        var skip = FindRelative(image, factor, open.Value, "skip-label")
            ?? FindAnchor(image, factor, "skip-label", new Rect(image.Width / 2, image.Height * 2 / 3,
                image.Width - image.Width / 2, image.Height - image.Height * 2 / 3));
        var target = skip ?? open.Value;
        return new(PackScreen.Opening, target.Center, null, false, string.Empty,
            Math.Min(open.Value.Score, target.Score))
        {
            AnimationSkipTarget = skip?.Center ?? InferAnimationSkipTarget(open.Value, width, height)
        };
    }

    /// <summary>仅从已验证打开锚点与原始模板相对位置推导隐藏跳过中心，越界时保留空坐标。</summary>
    /// <param name="open">已通过文字、相似度和对比度验证的打开锚点。</param>
    /// <param name="width">当前客户区原始物理像素宽度。</param>
    /// <param name="height">当前客户区原始物理像素高度。</param>
    /// <returns>位于当前客户区底部右侧的跳过坐标；裁切后位置失效时为空。</returns>
    private PixelPoint? InferAnimationSkipTarget(Match open, int width, int height)
    {
        var skip = templates["skip-label"];
        var point = new PixelPoint(
            (int)Math.Round(open.Bounds.X + (skip.X + skip.Gray.Width / 2d - open.Template.X) * open.Scale),
            (int)Math.Round(open.Bounds.Y + (skip.Y + skip.Gray.Height / 2d - open.Template.Y) * open.Scale));
        var actionRegion = new Rect(width / 2, height * 2 / 3, width - width / 2, height - height * 2 / 3);
        return actionRegion.Contains(new Point(point.X, point.Y)) ? point : null;
    }

    /// <summary>详情必须具备菜单锚点和下一包双箭头，免费入口必须再核验两段文字。</summary>
    private PackObservation? RecognizeDetails(Mat image, Mat original, Mat color, double factor)
    {
        var label = FindAnchor(image, factor, "details-label", new Rect(image.Width / 2, 0,
            image.Width - image.Width / 2, image.Height * 2 / 3));
        if (label is null) return null;
        var next = FindRelative(image, factor, label.Value, "next-arrow");
        if (next is null) return null;
        var fingerprint = Fingerprint(original, label.Value);
        if (fingerprint is null) return null;
        var packTitle = ReadPackTitle(color, label.Value);
        if (packTitle.Length == 0) return Unknown();
        var free = FindFreeEntry(image, factor, label.Value);
        var one = free is null ? null : FindRelative(image, factor, free.Value, "one-pack-text");
        var score = Math.Min(label.Value.Score, next.Value.Score);
        if (free is null || one is null || !OnSameYellowButton(color, one.Value, free.Value)
            || !VerifyFreeFee(color, FeeButtonBounds(one.Value, free.Value), false))
            return new(PackScreen.PackDetails, null, next.Value.Center, false, fingerprint, score) { PackTitle = packTitle };
        return new(PackScreen.PackDetails, free.Value.Center, next.Value.Center, true, fingerprint,
            Math.Min(score, Math.Min(free.Value.Score, one.Value.Score))) { PackTitle = packTitle };
    }

    /// <summary>在必需标题核心与详情导航锚点完整时读取标题，左界排除界面类别分隔线，保留完整Unicode字母数字作为精确身份。</summary>
    /// <param name="color">当前原始客户区的完整 BGRA 像素。</param>
    /// <param name="anchor">已完整匹配且通过标题图像指纹边界验证的详情菜单锚点。</param>
    /// <returns>保留全部中文、字母大小写及数字的标题；没有有效文字时为空。</returns>
    private string ReadPackTitle(Mat color, Match anchor)
    {
        // 菜单模板尺度浮动时，标题左侧留白可能包含分隔线；只收紧左界并保留原右界。
        var suggestedLeft = (int)Math.Round(anchor.Bounds.X - 1052 * anchor.Scale);
        var left = Math.Max(suggestedLeft, (int)Math.Ceiling(color.Width / 8d));
        var right = suggestedLeft + (int)Math.Round(1000 * anchor.Scale);
        var region = new Rect(left,
            Math.Max(0, (int)Math.Round(anchor.Bounds.Y - 307 * anchor.Scale) - (int)Math.Round(15 * anchor.Scale)),
            right - left, (int)Math.Round(90 * anchor.Scale));
        using var area = new Mat(color, region);
        using var isolated = area.Clone();
        var pixels = new byte[region.Width * region.Height * 4];
        Marshal.Copy(isolated.Data, pixels, 0, pixels.Length);
        var text = titleReader.Read(pixels, region.Width, region.Height).Normalize(NormalizationForm.FormKC);
        var title = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (Rune.IsLetterOrDigit(rune)) title.Append(rune.ToString());
        var normalizedTitle = title.ToString();
        logger.LogDebug("FreePackTitleRead RegionX={RegionX} RegionY={RegionY} RegionWidth={RegionWidth} RegionHeight={RegionHeight} AnchorScale={AnchorScale} RawText={RawText} PackTitle={PackTitle}",
            region.X, region.Y, region.Width, region.Height, anchor.Scale, text, normalizedTitle);
        return normalizedTitle;
    }

    /// <summary>由同一按钮上的数量和免费文字推导费用像素矩形，仅补充字形周围的黄色背景。</summary>
    private static Rect FeeButtonBounds(Match one, Match free)
    {
        var margin = 14 * free.Scale;
        var top = Math.Min(one.Bounds.Y, free.Bounds.Y);
        var bottom = Math.Max(one.Bounds.Bottom, free.Bounds.Bottom);
        return new Rect((int)Math.Floor(one.Bounds.X - margin), (int)Math.Floor(top - margin),
            (int)Math.Ceiling(free.Bounds.Right - one.Bounds.X + 2 * margin),
            (int)Math.Ceiling(bottom - top + 2 * margin));
    }

    /// <summary>复制原图局部为紧密 BGRA 后校验费用；宝石图标独立否决文字边界的批准结果。</summary>
    private bool VerifyFreeFee(Mat color, Rect region, bool gemDetected)
    {
        region = region.Intersect(new Rect(0, 0, color.Width, color.Height));
        using var area = new Mat(color, region);
        using var isolated = area.Clone();
        var pixels = new byte[region.Width * region.Height * 4];
        Marshal.Copy(isolated.Data, pixels, 0, pixels.Length);
        var approved = feeVerifier.IsFree(pixels, region.Width, region.Height, gemDetected);
        return !gemDetected && approved;
    }

    /// <summary>只在右下购买区域匹配免费文字，兼容单行免费和双行收费控件布局。</summary>
    private Match? FindFreeEntry(Mat image, double factor, Match anchor)
    {
        var region = new Rect((int)Math.Floor((anchor.Bounds.X - 46 * anchor.Scale) * factor),
            (int)Math.Floor((anchor.Bounds.Y + 418 * anchor.Scale) * factor),
            (int)Math.Ceiling(525 * anchor.Scale * factor), (int)Math.Ceiling(240 * anchor.Scale * factor));
        return Find(image, factor, templates["free-entry-text"], region,
            new[] { anchor.Scale * .98, anchor.Scale, anchor.Scale * 1.02 });
    }

    /// <summary>核验同一行的数量与免费文字下面具有连续黄色按钮背景，拒绝跨按钮拼接。</summary>
    private static bool OnSameYellowButton(Mat color, Match one, Match free)
    {
        var region = new Rect(one.Bounds.X, Math.Max(one.Bounds.Bottom, free.Bounds.Bottom) + (int)Math.Round(3 * free.Scale),
            free.Bounds.Right - one.Bounds.X, Math.Max(1, (int)Math.Round(3 * free.Scale)));
        if (region.Intersect(new Rect(0, 0, color.Width, color.Height)) != region) return false;
        using var band = new Mat(color, region);
        using var bgr = new Mat();
        using var hsv = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(band, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(15, 80, 120), new Scalar(40, 255, 255), mask);
        if (Cv2.CountNonZero(mask) < region.Width * region.Height * .9) return false;
        using var columns = new Mat();
        Cv2.Reduce(mask, columns, ReduceDimension.Row, ReduceTypes.Max, -1);
        var maximumGap = Math.Max(1, (int)Math.Round(3 * free.Scale));
        var gap = 0;
        var columnCount = columns.Width;
        for (var x = 0; x < columnCount; x++)
        {
            gap = columns.At<byte>(0, x) == 0 ? gap + 1 : 0;
            if (gap > maximumGap) return false;
        }
        return true;
    }

    /// <summary>在状态专属大区域中搜索模板，并加入当前窗口宽度对应的细粒度缩放候选。</summary>
    private Match? FindAnchor(Mat image, double factor, string name, Rect region)
    {
        var expected = image.Width / factor / ReferenceWidth;
        var scales = StandardScales.Concat(new[] { expected * .96, expected * .98, expected, expected * 1.02, expected * 1.04 });
        return Find(image, factor, templates[name], region, scales);
    }

    /// <summary>从已识别锚点推导按钮或费用文字区域，只在邻近位置及相近尺度内匹配。</summary>
    private Match? FindRelative(Mat image, double factor, Match anchor, string name)
    {
        var template = templates[name];
        var x = anchor.Bounds.X + (template.X - anchor.Template.X) * anchor.Scale;
        var y = anchor.Bounds.Y + (template.Y - anchor.Template.Y) * anchor.Scale;
        var margin = 14 * anchor.Scale;
        var region = new Rect((int)Math.Floor((x - margin) * factor), (int)Math.Floor((y - margin) * factor),
            (int)Math.Ceiling((template.Gray.Width * anchor.Scale + 2 * margin) * factor),
            (int)Math.Ceiling((template.Gray.Height * anchor.Scale + 2 * margin) * factor));
        return Find(image, factor, template, region, new[] { anchor.Scale * .98, anchor.Scale, anchor.Scale * 1.02 });
    }

    /// <summary>执行真实归一化匹配，拒绝尺寸不适配、相似度不足以及遮罩变暗的候选。</summary>
    private static Match? Find(Mat image, double factor, Template template, Rect region, IEnumerable<double> scales)
    {
        region = region.Intersect(new Rect(0, 0, image.Width, image.Height));
        if (region.Width <= 0 || region.Height <= 0) return null;
        using var area = new Mat(image, region);
        Match? best = null;
        foreach (var scale in scales)
        {
            var size = new Size(Math.Max(1, (int)Math.Round(template.Gray.Width * scale * factor)),
                Math.Max(1, (int)Math.Round(template.Gray.Height * scale * factor)));
            if (size.Width > area.Width || size.Height > area.Height) continue;
            using var resized = new Mat();
            Cv2.Resize(template.Gray, resized, size, 0, 0, InterpolationFlags.Area);
            using var response = new Mat();
            Cv2.MatchTemplate(area, resized, response, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(response, out _, out var score, out _, out var location);
            if (score < MatchThreshold) continue;
            using var candidate = new Mat(area, new Rect(location, size));
            Cv2.MeanStdDev(candidate, out _, out var deviation);
            Cv2.MeanStdDev(resized, out _, out var templateDeviation);
            if (deviation.Val0 < templateDeviation.Val0 * .55) continue;
            if (best is null || score > best.Value.Score)
                best = new(template, new Rect((int)Math.Round((region.X + location.X) / factor),
                    (int)Math.Round((region.Y + location.Y) / factor),
                    (int)Math.Round(size.Width / factor), (int)Math.Round(size.Height / factor)), scale, Math.Clamp(score, 0, 1));
        }
        return best;
    }

    /// <summary>从标题文字的水平前景投影计算六十四位指纹，排除背景纹理和卡图动画。</summary>
    private static string? Fingerprint(Mat image, Match anchor)
    {
        var region = new Rect((int)Math.Round(anchor.Bounds.X - 1052 * anchor.Scale),
            (int)Math.Round(anchor.Bounds.Y - 307 * anchor.Scale),
            (int)Math.Round(500 * anchor.Scale), (int)Math.Round(50 * anchor.Scale));
        if (region.Intersect(new Rect(0, 0, image.Width, image.Height)) != region) return null;
        using var title = new Mat(image, region);
        using var foreground = new Mat();
        Cv2.Threshold(title, foreground, 120, 255, ThresholdTypes.Tozero);
        if (Cv2.CountNonZero(foreground) == 0) return null;
        using var sample = new Mat();
        Cv2.Resize(foreground, sample, new Size(65, 1), 0, 0, InterpolationFlags.Area);
        ulong hash = 0;
        for (var x = 0; x < 64; x++)
            hash = (hash << 1) | (sample.At<byte>(0, x) - sample.At<byte>(0, x + 1) > 1 ? 1UL : 0UL);
        return hash.ToString("x16");
    }

    /// <summary>从程序集内确定存在的截图资源解码并缓存真实灰度模板。</summary>
    private static Template LoadTemplate(string name, int x, int y)
    {
        using var stream = typeof(OpenCvPackRecognizer).Assembly.GetManifestResourceStream(
            $"MasterDuelSwitcher.Core.Assets.PackTemplates.{name}.png")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var gray = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Grayscale);
        return new(name, gray, x, y);
    }

    /// <summary>创建没有可点击目标的未知界面观测。</summary>
    private static PackObservation Unknown() => new(PackScreen.Unknown, null, null, false, string.Empty, 0);

    /// <summary>释放全部缓存模板；重复调用交由原生图像的幂等释放处理。</summary>
    public void Dispose()
    {
        disposed = true;
        foreach (var template in templates.Values) template.Gray.Dispose();
    }

    /// <summary>一张原始截图文字模板及其参考坐标。</summary>
    /// <param name="Name">嵌入模板名称。</param>
    /// <param name="Gray">缓存的原生灰度图像。</param>
    /// <param name="X">参考截图中的模板左边界。</param>
    /// <param name="Y">参考截图中的模板上边界。</param>
    private sealed record Template(string Name, Mat Gray, int X, int Y);

    /// <summary>某一尺度下已通过相似度和对比度验证的模板匹配。</summary>
    /// <param name="Template">所匹配的模板与参考位置。</param>
    /// <param name="Bounds">映射回原始客户区物理像素的匹配边界。</param>
    /// <param name="Scale">相对于原始模板的图像缩放倍数。</param>
    /// <param name="Score">归一化匹配相似度。</param>
    private readonly record struct Match(Template Template, Rect Bounds, double Scale, double Score)
    {
        /// <summary>用于完整按下和抬起动作的模板中心物理像素。</summary>
        public PixelPoint Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
    }
}
