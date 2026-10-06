using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using OpenCvSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>从紧密排列的卡包BGRA费用区域读取文字。</summary>
public interface IPackTextReader
{
    /// <summary>读取指定像素区域的文字；识别组件缺失时明确停止。</summary>
    /// <param name="bgraPixels">逐行紧密排列的BGRA字节。</param>
    /// <param name="width">区域物理像素宽度。</param>
    /// <param name="height">区域物理像素高度。</param>
    /// <returns>原生识别文本。</returns>
    string Read(byte[] bgraPixels, int width, int height);
}

/// <summary>通过系统简体中文OCR读取卡包费用区域。</summary>
public sealed class WindowsPackTextReader : IPackTextReader
{
    /// <summary>仅在首次识别时创建并复用系统简体中文引擎。</summary>
    private readonly Lazy<OcrEngine> engine;
    /// <summary>读取原生位图并返回保留行边界的文字。</summary>
    private readonly Func<OcrEngine, SoftwareBitmap, string> recognize;

    /// <summary>配置可注入的原生引擎创建与文字识别边界。</summary>
    /// <param name="engineFactory">原生简体中文引擎创建函数。</param>
    /// <param name="recognize">原生位图文字识别函数。</param>
    public WindowsPackTextReader(Func<OcrEngine?>? engineFactory = null, Func<OcrEngine, SoftwareBitmap, string>? recognize = null)
    {
        Func<OcrEngine?> factory = engineFactory ?? CreateChineseEngine;
        engine = new Lazy<OcrEngine>(() => factory() ?? throw new InvalidOperationException(
            "缺少 Windows 简体中文 OCR 语言组件，请在系统语言设置中安装中文文字识别后重试。"));
        this.recognize = recognize ?? RecognizeText;
    }

    /// <summary>读取经过验证的紧密BGRA费用区域。</summary>
    /// <param name="bgraPixels">逐行紧密排列的BGRA字节。</param>
    /// <param name="width">区域物理像素宽度。</param>
    /// <param name="height">区域物理像素高度。</param>
    /// <returns>原生识别文本。</returns>
    public string Read(byte[] bgraPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        uint maximum = OcrEngine.MaxImageDimension;
        if (width <= 0 || height <= 0 || width > maximum || height > maximum)
            throw new ArgumentOutOfRangeException(nameof(width), "OCR 区域宽高必须为正数且不超过系统引擎上限。");
        if ((long)width * height * 4 != bgraPixels.Length)
            throw new ArgumentException("OCR 区域必须拥有逐行紧密排列的四通道 BGRA 像素。", nameof(bgraPixels));
        double scale = Math.Min(4d, Math.Min(160d / height, Math.Min(maximum / (double)width, maximum / (double)height)));
        if (scale > 1)
        {
            using var original = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgraPixels);
            using var enlarged = new Mat();
            Cv2.Resize(original, enlarged, new Size((int)Math.Round(width * scale), (int)Math.Round(height * scale)),
                0, 0, InterpolationFlags.Cubic);
            width = enlarged.Width;
            height = enlarged.Height;
            bgraPixels = new byte[width * height * 4];
            Marshal.Copy(enlarged.Data, bgraPixels, 0, bgraPixels.Length);
        }
        using var image = SoftwareBitmap.CreateCopyFromBuffer(bgraPixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            width, height, BitmapAlphaMode.Ignore);
        return recognize(engine.Value, image);
    }

    /// <summary>只创建已安装简体中文模型的引擎，避免英文模型产生空费用结果。</summary>
    /// <returns>系统已安装的简体中文引擎；缺少模型时返回null。</returns>
    private static OcrEngine? CreateChineseEngine() => OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));

    /// <summary>同步等待原生异步识别并保留行分隔，避免跨行拼接出不存在的免费短语。</summary>
    /// <param name="engine">已经创建的简体中文系统引擎。</param>
    /// <param name="image">当前调用持有的原生BGRA位图。</param>
    /// <returns>按原生行顺序拼接的识别文字。</returns>
    private static string RecognizeText(OcrEngine engine, SoftwareBitmap image)
    {
        var result = engine.RecognizeAsync(image).AsTask().GetAwaiter().GetResult();
        return string.Join('\n', result.Lines.Select(line => line.Text));
    }
}
