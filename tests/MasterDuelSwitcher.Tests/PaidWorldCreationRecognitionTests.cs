using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using OpenCvSharp;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>用用户提供的原生1920收费详情验证完整标题、下一包及免费购买门禁。</summary>
public sealed class PaidWorldCreationRecognitionTests
{
    /// <summary>对完整原窗和精确客户区分别验证四档输入，避免旧截图缩放替代新栅格。</summary>
    /// <param name="clientOnly">是否仅保留原像素客户区。</param>
    /// <param name="scale">原图上的测试缩放比例；1为未重采样原始输入。</param>
    [Theory]
    [InlineData(false, .65)]
    [InlineData(false, .85)]
    [InlineData(false, 1)]
    [InlineData(false, 1.25)]
    [InlineData(true, .65)]
    [InlineData(true, .85)]
    [InlineData(true, 1)]
    [InlineData(true, 1.25)]
    public void NativePaidDetailsKeepsTheWholeTitleAndOnlyAllowsNext(bool clientOnly, double scale)
    {
        using var original = LoadOriginal();
        using var area = new Mat(original, clientOnly ? new Rect(1, 31, 1920, 1080)
            : new Rect(0, 0, 1922, 1112));
        using var image = new Mat();
        Cv2.Resize(area, image, new Size((int)Math.Round(area.Width * scale),
            (int)Math.Round(area.Height * scale)), 0, 0,
            scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        var before = pixels.ToArray();
        var frame = new GameFrame(1, image.Width, image.Height, 0, 0, pixels, DateTimeOffset.UnixEpoch);
        using var subject = new OpenCvPackRecognizer();

        var cold = subject.Recognize(frame);
        var warm = subject.Recognize(frame);

        foreach (var observation in new[] { cold, warm })
        {
            Assert.Equal(PackScreen.PackDetails, observation.Screen);
            Assert.Equal("律世葬创", observation.PackTitle);
            Assert.False(observation.FreeOffer);
            Assert.Null(observation.PrimaryTarget);
            Assert.Null(observation.AnimationSkipTarget);
            Assert.NotNull(observation.NextTarget);
            var next = observation.NextTarget.Value;
            Assert.InRange(next.X, (int)Math.Round((1810 - (clientOnly ? 1 : 0)) * scale),
                (int)Math.Round((1890 - (clientOnly ? 1 : 0)) * scale));
            Assert.InRange(next.Y, (int)Math.Round((519 - (clientOnly ? 31 : 0)) * scale),
                (int)Math.Round((626 - (clientOnly ? 31 : 0)) * scale));
            Assert.Equal(64, observation.TitleVisualSignature.Length);
        }
        Assert.Equal(cold.PackTitle, warm.PackTitle);
        Assert.Equal(cold.NextTarget, warm.NextTarget);
        Assert.Equal(before, frame.Pixels);
    }

    /// <summary>从测试程序集读取未经编辑的用户原截图，转换为紧密四通道像素。</summary>
    /// <returns>由调用方释放的原始1922×1112图像。</returns>
    private static Mat LoadOriginal()
    {
        using var stream = typeof(PaidWorldCreationRecognitionTests).Assembly.GetManifestResourceStream(
            "MasterDuelSwitcher.Tests.Assets.PackFrames.paid-details-world-creation-full-window.png");
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var decoded = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Color);
        Assert.Equal(1922, decoded.Width);
        Assert.Equal(1112, decoded.Height);
        var bgra = new Mat();
        Cv2.CvtColor(decoded, bgra, ColorConversionCodes.BGR2BGRA);
        return bgra;
    }
}
