using System.Runtime.InteropServices.WindowsRuntime;
using MasterDuelSwitcher.Core.Services;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证系统OCR输入校验、缺语言模型、原生异常和位图资源生命周期。</summary>
public sealed class WindowsPackTextReaderTests
{
    /// <summary>简体中文模型缺失时，读取必须以明确语言组件异常停止。</summary>
    [Fact]
    public void MissingChineseModelStopsWithActionableMessage()
    {
        var reader = new WindowsPackTextReader(() => null);
        var failure = Assert.Throws<InvalidOperationException>(() => reader.Read([0, 0, 0, 255], 1, 1));
        Assert.Contains("简体中文", failure.Message);
    }

    /// <summary>正常读取保持BGRA像素顺序，返回原生文本并在返回后释放位图。</summary>
    [Fact]
    public void NativeSuccessReturnsTextAndDisposesBitmap()
    {
        SoftwareBitmap? captured = null;
        var reader = new WindowsPackTextReader(CreateChineseEngine, (_, image) =>
        {
            captured = image;
            var copied = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyToBuffer(copied.AsBuffer());
            Assert.Equal(new byte[] { 12, 34, 56, 255 }, copied[..4]);
            return "1 次 免 费";
        });
        Assert.Equal("1 次 免 费", reader.Read([12, 34, 56, 255], 1, 1));
        AssertDisposed(captured);
    }

    /// <summary>原生识别失败也必须释放位图，并保留同一个识别异常供上层停止记录。</summary>
    [Fact]
    public void NativeFailureDisposesBitmapAndPreservesException()
    {
        SoftwareBitmap? captured = null;
        var original = new InvalidOperationException("原生失败");
        var reader = new WindowsPackTextReader(CreateChineseEngine, (_, image) =>
        {
            captured = image;
            throw original;
        });
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => reader.Read([0, 0, 0, 255], 1, 1)));
        AssertDisposed(captured);
    }

    /// <summary>空像素参数必须在调用系统识别之前被输入契约拒绝。</summary>
    [Fact]
    public void NullPixelsAreRejected() => Assert.Throws<ArgumentNullException>(() => new WindowsPackTextReader().Read(null!, 1, 1));

    /// <summary>任一非正维度都必须被拒绝，避免创建异常原生位图。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void NonpositiveDimensionsAreRejected(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsPackTextReader().Read([], width, height));

    /// <summary>宽高分别超过系统引擎上限时都必须被拒绝，且不会分配超大位图。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DimensionsBeyondNativeLimitAreRejected(bool excessiveWidth)
    {
        int excessive = checked((int)OcrEngine.MaxImageDimension + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsPackTextReader().Read([], excessiveWidth ? excessive : 1, excessiveWidth ? 1 : excessive));
    }

    /// <summary>像素缓冲长度必须精确对应紧密排列的四通道布局。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void IncompleteOrExtraBgraBytesAreRejected(int length) =>
        Assert.Throws<ArgumentException>(() => new WindowsPackTextReader().Read(new byte[length], 1, 1));

    /// <summary>从本机已安装的模型创建真实原生引擎，供回调边界测试复用。</summary>
    private static OcrEngine? CreateChineseEngine() => OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));

    /// <summary>验证回调持有的原生位图已经失效，避免成功和失败路径泄漏资源。</summary>
    private static void AssertDisposed(SoftwareBitmap? image)
    {
        Assert.NotNull(image);
        Assert.ThrowsAny<Exception>(() => image.CopyToBuffer(new byte[64].AsBuffer()));
    }
}
