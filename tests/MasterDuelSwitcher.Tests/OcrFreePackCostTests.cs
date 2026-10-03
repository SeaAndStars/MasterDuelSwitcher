using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证费用文本必须具有明确免费证据，同时拒绝收费、余额变化和识别异常。</summary>
public sealed class OcrFreePackCostTests
{
    /// <summary>真实文字判定覆盖中文空白、标点、收费负例和包含宝石的正常卡包名称。</summary>
    [Theory]
    [InlineData("1次免费", true)]
    [InlineData("已 购 买 颠 覆 世 界 恶 魔 之 力 。 0 次 免 费 ）", true)]
    [InlineData("已购买宝石骑士。（1次免费）", true)]
    [InlineData("１ 包，１ 次 免 费！", true)]
    [InlineData("1次免费 消费1000宝石", false)]
    [InlineData("1次免费 消耗100宝石", false)]
    [InlineData("1次免费 花费100宝石", false)]
    [InlineData("1次免费 所持宝石", false)]
    [InlineData("1次免费 100宝石", false)]
    [InlineData("1次免费 宝石：100", false)]
    [InlineData("1次免费 余额扣减100", false)]
    [InlineData("1次免费 6890→5890", false)]
    [InlineData("1次免费 6890->5890", false)]
    [InlineData("1次免费 6890乛5890", false)]
    [InlineData("1次免费 6890—5890", false)]
    [InlineData("（1次免费）消费1000宝石", false)]
    [InlineData("（1次免费）免费已结束", false)]
    [InlineData("不是免费", false)]
    [InlineData("非免费", false)]
    [InlineData("免费已结束", false)]
    [InlineData("免费次数已用完", false)]
    [InlineData("消费1000宝石购买欲望变革。", false)]
    [InlineData("确认购买 宝石骑士", false)]
    [InlineData("已购买免费骑士。（1包）", false)]
    [InlineData("确认购买免费宝石骑士", false)]
    [InlineData("1包 购买", false)]
    [InlineData("免\n费", false)]
    [InlineData("", false)]
    [InlineData(" \t\r\n", false)]
    public void FeeTextRequiresExplicitFreeWithoutPaidEvidence(string text, bool expected)
    {
        var verifier = new OcrFreePackCostVerifier(new FixtureTextReader(text));
        Assert.Equal(expected, verifier.IsFree([0, 0, 0, 255], 1, 1, false));
    }

    /// <summary>费用区已经匹配宝石图标时，文字识别失败或免费字样均不能授权购买。</summary>
    [Fact]
    public void GemIconRejectsBeforeAttemptingTextRecognition()
    {
        var verifier = new OcrFreePackCostVerifier(new FixtureTextReader("免费", new InvalidOperationException("不应读取")));
        Assert.False(verifier.IsFree([0, 0, 0, 255], 1, 1, true));
    }

    /// <summary>原生识别异常必须保留原因并以清晰停止异常传播到自动化调用方。</summary>
    [Fact]
    public void RecognitionFailureStopsAndRecordsOriginalException()
    {
        var original = new InvalidOperationException("测试原生识别失败");
        var logger = new FixtureLogger();
        var verifier = new OcrFreePackCostVerifier(new FixtureTextReader("免费", original), logger);
        var failure = Assert.Throws<InvalidOperationException>(() => verifier.IsFree([0, 0, 0, 255], 1, 1, false));
        Assert.Same(original, failure.InnerException);
        Assert.Contains("停止", failure.Message);
        Assert.Same(original, logger.LastException);
        Assert.Equal(LogLevel.Error, logger.LastLevel);
    }

    /// <summary>费用判定服务必须拒绝缺少文字识别器的无效依赖配置。</summary>
    [Fact]
    public void MissingReaderIsRejected() => Assert.Throws<ArgumentNullException>(() => new OcrFreePackCostVerifier(null!));

    /// <summary>真实免费及收费截图的费用行和完整弹窗均通过系统简体中文引擎进行回归。</summary>
    [Theory]
    [InlineData("free-confirm-long-title.png", 580, 589, 883, 59, .65, true)]
    [InlineData("free-confirm-long-title.png", 580, 589, 883, 59, .85, true)]
    [InlineData("free-confirm-long-title.png", 580, 589, 883, 59, 1, true)]
    [InlineData("free-confirm-long-title.png", 580, 589, 883, 59, 1.25, true)]
    [InlineData("paid-confirm.png", 580, 480, 883, 59, .65, false)]
    [InlineData("paid-confirm.png", 580, 480, 883, 59, .85, false)]
    [InlineData("paid-confirm.png", 580, 480, 883, 59, 1, false)]
    [InlineData("paid-confirm.png", 580, 480, 883, 59, 1.25, false)]
    [InlineData("free-confirm-long-title.png", 530, 415, 989, 384, 1, true)]
    [InlineData("paid-confirm.png", 530, 303, 989, 607, 1, false)]
    [InlineData("free-details-single-row.png", 1312, 897, 491, 78, .65, true)]
    [InlineData("free-details-single-row.png", 1312, 897, 491, 78, .85, true)]
    [InlineData("free-details-single-row.png", 1312, 897, 491, 78, 1, true)]
    [InlineData("free-details-single-row.png", 1312, 897, 491, 78, 1.25, true)]
    public void RealChineseOcrDistinguishesFeeLinesAndCompleteModals(string fixture, int x, int y, int width, int height, double scale, bool expected)
    {
        var frame = ReadFixture(fixture, new Rect(x, y, width, height), scale);
        var reader = new WindowsPackTextReader();
        string text = reader.Read(frame.Pixels, frame.Width, frame.Height);
        Assert.Contains(expected ? "免费" : "消费1000宝石", string.Concat(text.Where(character => !char.IsWhiteSpace(character))));
        Assert.Equal(expected, new OcrFreePackCostVerifier(reader).IsFree(frame.Pixels, frame.Width, frame.Height, false));
    }

    /// <summary>从嵌入真实截图复制紧密排列的BGRA费用区域，脱离本机临时图片路径。</summary>
    private static (byte[] Pixels, int Width, int Height) ReadFixture(string name, Rect area, double scale)
    {
        using var stream = typeof(OcrFreePackCostTests).Assembly.GetManifestResourceStream("MasterDuelSwitcher.Tests.Assets.PackFrames." + name);
        Assert.NotNull(stream);
        using var encoded = new MemoryStream();
        stream.CopyTo(encoded);
        using var original = Cv2.ImDecode(encoded.ToArray(), ImreadModes.Unchanged);
        using var roi = new Mat(original, area);
        using var image = new Mat();
        Cv2.Resize(roi, image, new Size((int)Math.Round(roi.Width * scale), (int)Math.Round(roi.Height * scale)), 0, 0, InterpolationFlags.Area);
        Assert.Equal(MatType.CV_8UC4, image.Type());
        var pixels = new byte[image.Width * image.Height * 4];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        return (pixels, image.Width, image.Height);
    }

    /// <summary>提供固定文字或原生异常，隔离文字判定与系统语言模型的依赖。</summary>
    /// <param name="text">测试输入的独立文字结果。</param>
    /// <param name="failure">调用识别时传播的可控异常。</param>
    private sealed class FixtureTextReader(string text, Exception? failure = null) : IPackTextReader
    {
        /// <summary>按照测试指定的原生结果返回文字或传播原始异常。</summary>
        public string Read(byte[] bgraPixels, int width, int height)
        {
            if (failure is not null) throw failure;
            return text;
        }
    }

    /// <summary>保留停止诊断的真实日志级别和异常，供调用方行为断言。</summary>
    private sealed class FixtureLogger : ILogger<OcrFreePackCostVerifier>
    {
        /// <summary>最后一条记录附带的原始异常。</summary>
        public Exception? LastException { get; private set; }
        /// <summary>最后一条记录的诊断级别。</summary>
        public LogLevel LastLevel { get; private set; }
        /// <summary>测试记录器接收所有诊断级别。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>费用识别日志未使用范围状态。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>保存业务服务实际发出的停止诊断信息。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            LastException = exception;
            LastLevel = logLevel;
        }
    }
}
