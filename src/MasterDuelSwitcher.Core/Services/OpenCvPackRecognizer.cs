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
    /// <summary>原生模板资源是否已经释放。</summary>
    private bool disposed;

    /// <summary>读取嵌入的真实游戏截图模板，建立可重复使用的原生图像缓存。</summary>
    /// <param name="logger">识别诊断日志；省略时使用空日志。</param>
    public OpenCvPackRecognizer(ILogger<OpenCvPackRecognizer>? logger = null)
    {
        this.logger = logger ?? NullLogger<OpenCvPackRecognizer>.Instance;
        templates = new[]
        {
            LoadTemplate("details-label", 1336, 377), LoadTemplate("next-arrow", 1938, 551),
            LoadTemplate("free-entry-text", 1608, 844), LoadTemplate("one-pack-text", 1404, 844),
            LoadTemplate("dialog-title", 485, 245), LoadTemplate("dialog-free-text", 608, 382),
            LoadTemplate("dialog-cancel", 325, 480), LoadTemplate("dialog-purchase", 713, 480),
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
        var observation = RecognizeDialog(search, factor) ?? RecognizeResults(search, factor)
            ?? RecognizeOpening(search, factor) ?? RecognizeDetails(search, gray, factor) ?? Unknown();
        logger.LogDebug("卡包画面识别：{Screen}，免费 {FreeOffer}，分数 {Confidence:F4}，卡图 {Fingerprint}",
            observation.Screen, observation.FreeOffer, observation.Confidence, observation.Fingerprint);
        return observation;
    }

    /// <summary>限定免费文字位于弹窗费用行，并同时核验取消和购买按钮。</summary>
    private PackObservation? RecognizeDialog(Mat image, double factor)
    {
        var title = FindAnchor(image, factor, "dialog-title", new Rect(image.Width / 5, image.Height / 8,
            image.Width * 3 / 5, image.Height * 5 / 8));
        if (title is null) return null;
        var cancel = FindRelative(image, factor, title.Value, "dialog-cancel");
        var purchase = FindRelative(image, factor, title.Value, "dialog-purchase");
        var free = FindRelative(image, factor, title.Value, "dialog-free-text");
        if (cancel is null || purchase is null || free is null)
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
    private PackObservation? RecognizeOpening(Mat image, double factor)
    {
        var open = FindAnchor(image, factor, "opening-label", new Rect(0, image.Height * 2 / 3,
            image.Width / 2, image.Height - image.Height * 2 / 3));
        if (open is null) return null;
        var skip = FindRelative(image, factor, open.Value, "skip-label");
        var target = skip ?? open.Value;
        return new(PackScreen.Opening, target.Center, null, false, string.Empty,
            Math.Min(open.Value.Score, target.Score));
    }

    /// <summary>详情必须具备菜单锚点和下一包双箭头，免费入口必须再核验两段文字。</summary>
    private PackObservation? RecognizeDetails(Mat image, Mat original, double factor)
    {
        var label = FindAnchor(image, factor, "details-label", new Rect(image.Width / 2, 0,
            image.Width - image.Width / 2, image.Height * 2 / 3));
        if (label is null) return null;
        var next = FindRelative(image, factor, label.Value, "next-arrow");
        if (next is null) return null;
        var fingerprint = Fingerprint(original, label.Value);
        if (fingerprint is null) return null;
        var free = FindRelative(image, factor, label.Value, "free-entry-text");
        var one = FindRelative(image, factor, label.Value, "one-pack-text");
        var score = Math.Min(label.Value.Score, next.Value.Score);
        if (free is null || one is null)
            return new(PackScreen.PackDetails, null, next.Value.Center, false, fingerprint, score);
        return new(PackScreen.PackDetails, free.Value.Center, next.Value.Center, true, fingerprint,
            Math.Min(score, Math.Min(free.Value.Score, one.Value.Score)));
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
            if (deviation.Val0 < template.Deviation * .55) continue;
            if (best is null || score > best.Value.Score)
                best = new(template, new Rect((int)Math.Round((region.X + location.X) / factor),
                    (int)Math.Round((region.Y + location.Y) / factor),
                    (int)Math.Round(size.Width / factor), (int)Math.Round(size.Height / factor)), scale, Math.Clamp(score, 0, 1));
        }
        return best;
    }

    /// <summary>从插画中心的稳定区域计算六十四位差值指纹，避开 NEW、日期、余额和免费按钮。</summary>
    private static string? Fingerprint(Mat image, Match anchor)
    {
        var region = new Rect((int)Math.Round(anchor.Bounds.X - 916 * anchor.Scale),
            (int)Math.Round(anchor.Bounds.Y - 77 * anchor.Scale),
            (int)Math.Round(650 * anchor.Scale), (int)Math.Round(330 * anchor.Scale));
        if (region.Intersect(new Rect(0, 0, image.Width, image.Height)) != region) return null;
        using var artwork = new Mat(image, region);
        using var sample = new Mat();
        Cv2.Resize(artwork, sample, new Size(9, 8), 0, 0, InterpolationFlags.Area);
        ulong hash = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                hash = (hash << 1) | (sample.At<byte>(y, x) > sample.At<byte>(y, x + 1) ? 1UL : 0UL);
        return hash.ToString("x16");
    }

    /// <summary>从程序集内确定存在的截图资源解码模板，并保留其对比度作为遮罩检测基准。</summary>
    private static Template LoadTemplate(string name, int x, int y)
    {
        using var stream = typeof(OpenCvPackRecognizer).Assembly.GetManifestResourceStream(
            $"MasterDuelSwitcher.Core.Assets.PackTemplates.{name}.png")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var gray = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Grayscale);
        Cv2.MeanStdDev(gray, out _, out var deviation);
        return new(name, gray, x, y, deviation.Val0);
    }

    /// <summary>创建没有可点击目标的未知界面观测。</summary>
    private static PackObservation Unknown() => new(PackScreen.Unknown, null, null, false, string.Empty, 0);

    /// <summary>释放全部缓存模板；重复调用交由原生图像的幂等释放处理。</summary>
    public void Dispose()
    {
        disposed = true;
        foreach (var template in templates.Values) template.Gray.Dispose();
    }

    /// <summary>一张原始截图文字模板及其参考坐标和对比度。</summary>
    /// <param name="Name">嵌入模板名称。</param>
    /// <param name="Gray">缓存的原生灰度图像。</param>
    /// <param name="X">参考截图中的模板左边界。</param>
    /// <param name="Y">参考截图中的模板上边界。</param>
    /// <param name="Deviation">原始模板的灰度标准差。</param>
    private sealed record Template(string Name, Mat Gray, int X, int Y, double Deviation);

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
