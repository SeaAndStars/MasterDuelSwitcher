using System.Numerics;
using System.Runtime.InteropServices;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using OpenCvSharp;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>以用户提供的真实截图和真实 OpenCV 分类器验证免费开包完整状态转换，输入始终只记录于内存。</summary>
public sealed class FreePackAutomationIntegrationTests
{
    /// <summary>真实免费详情、确认、开包、结果及收费轮转须只完成一次免费购买，并在返回第一标题时结束。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.9375)]
    public async Task RecordedFreePurchaseReturnsToSameTitleThenNavigatesWithoutPaidPurchase(double scale)
    {
        using var recognizer = new OpenCvPackRecognizer();
        var sequence = CreateSequence(scale, false);
        var observations = sequence.Select(item => recognizer.Recognize(item.Frame)).ToArray();
        AssertRecordedClassifications(observations);
        var progress = new RecordingProgress();
        var platform = new RecordedPlatform(sequence, progress);
        var service = new FreePackAutomationService(recognizer, platform);

        var result = await service.RunAsync(progress);

        AssertCompletedRound(result, platform);
        AssertActions(platform, observations, scale);
    }

    /// <summary>下一包切换中原标题的插画动画仍是同一包，须等待不同标题而非重新购买或重复导航。</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.9375)]
    public async Task SameTitleArtworkAnimationDoesNotAdvanceScanOrRepeatNext(double scale)
    {
        using var recognizer = new OpenCvPackRecognizer();
        var sequence = CreateSequence(scale, true);
        var observations = sequence.Select(item => recognizer.Recognize(item.Frame)).ToArray();
        AssertRecordedClassifications(observations.Where((_, index) => index != 5).ToArray());
        Assert.Equal(PackScreen.PackDetails, observations[5].Screen);
        Assert.False(observations[5].FreeOffer);
        Assert.Null(observations[5].PrimaryTarget);
        Assert.Equal(observations[4].NextTarget, observations[5].NextTarget);
        Assert.InRange(HammingDistance(observations[4].Fingerprint, observations[5].Fingerprint), 0, 4);
        Assert.False(sequence[4].Frame.Pixels.SequenceEqual(sequence[5].Frame.Pixels));
        var progress = new RecordingProgress();
        var platform = new RecordedPlatform(sequence, progress);
        var service = new FreePackAutomationService(recognizer, platform);

        var result = await service.RunAsync(progress);

        AssertCompletedRound(result, platform);
        AssertActions(platform, observations.Where((_, index) => index != 5).ToArray(), scale);
        Assert.DoesNotContain(platform.Clicks, item => item.Name == "same-title-animation");
        var animationCaptures = platform.Captures.Where(item => item.Name == "same-title-animation").ToArray();
        Assert.Equal(2, animationCaptures.Length);
        Assert.All(animationCaptures, item =>
        {
            Assert.Equal(1, item.ScannedPacks);
            Assert.Equal(1, item.OpenedPacks);
        });
    }

    /// <summary>构造同一固定客户区内的真实页面序列，所有截图均保留原始比例，免费入口没有拼贴。</summary>
    private static RecordedFrame[] CreateSequence(double scale, bool includeAnimation)
    {
        var sequence = new List<RecordedFrame>
        {
            new("free-details", LoadFrame("free-details-single-row.png", scale)),
            new("free-confirm", LoadFrame("free-confirm-long-title.png", scale)),
            new("opening", LoadFrame("opening.png", scale)),
            new("results", LoadFrame("results.png", scale)),
            new("returned-details", LoadFrame("paid-details-after-opening.png", scale))
        };
        if (includeAnimation)
            sequence.Add(new("same-title-animation", LoadFrame("paid-details-after-opening.png", scale, true)));
        sequence.Add(new("different-details", LoadFrame("paid-details.png", scale)));
        sequence.Add(new("loop-details", LoadFrame("paid-details-after-opening.png", scale)));
        return sequence.ToArray();
    }

    /// <summary>核对真实分类结果和标题身份；两张不同标题必须超过状态机同包容差。</summary>
    private static void AssertRecordedClassifications(PackObservation[] observations)
    {
        Assert.Equal(new[] { PackScreen.PackDetails, PackScreen.FreePurchaseDialog, PackScreen.Opening,
            PackScreen.Results, PackScreen.PackDetails, PackScreen.PackDetails, PackScreen.PackDetails },
            observations.Select(item => item.Screen));
        Assert.True(observations[0].FreeOffer);
        Assert.NotNull(observations[0].PrimaryTarget);
        Assert.True(observations[1].FreeOffer);
        Assert.NotNull(observations[1].PrimaryTarget);
        Assert.NotNull(observations[2].PrimaryTarget);
        Assert.NotNull(observations[3].PrimaryTarget);
        Assert.All(observations.Skip(4), item =>
        {
            Assert.False(item.FreeOffer);
            Assert.Null(item.PrimaryTarget);
            Assert.NotNull(item.NextTarget);
        });
        Assert.InRange(HammingDistance(observations[0].Fingerprint, observations[4].Fingerprint), 0, 4);
        Assert.True(HammingDistance(observations[4].Fingerprint, observations[5].Fingerprint) > 4);
        Assert.InRange(HammingDistance(observations[0].Fingerprint, observations[6].Fingerprint), 0, 4);
    }

    /// <summary>一轮扫描只确认一个免费包，并以重新遇到第一标题正常结束且释放会话。</summary>
    private static void AssertCompletedRound(FreePackRunResult result, RecordedPlatform platform)
    {
        Assert.Equal(2, result.ScannedPacks);
        Assert.Equal(1, result.OpenedPacks);
        Assert.False(result.IsCancelled);
        Assert.Contains("一轮", result.Reason);
        Assert.Empty(platform.Diagnostics);
        Assert.Equal(1, platform.Activations);
        Assert.Equal(1, platform.EndCalls);
    }

    /// <summary>逐项核对实际记录的六次动作及独立按钮区域，所有收费详情点击必须是下一包箭头。</summary>
    private static void AssertActions(RecordedPlatform platform, PackObservation[] observations, double scale)
    {
        Assert.Equal(new[] { "free-details", "free-confirm", "opening", "results", "returned-details", "different-details" },
            platform.Clicks.Select(item => item.Name));
        Assert.Equal(new[] { observations[0].PrimaryTarget, observations[1].PrimaryTarget,
            observations[2].PrimaryTarget, observations[3].PrimaryTarget,
            observations[4].NextTarget, observations[5].NextTarget },
            platform.Clicks.Select(item => (PixelPoint?)item.Point));
        var regions = new[]
        {
            new Rect(1310, 866, 493, 81),
            new Rect(1040, 652, 354, 64),
            new Rect(1109, 941, 243, 58),
            new Rect(1518, 1032, 402, 65),
            new Rect(1930, 510, 75, 129),
            new Rect(1930, 510, 75, 129)
        };
        for (var index = 0; index < regions.Length; index++)
        {
            var point = platform.Clicks[index].Point;
            var region = regions[index];
            Assert.InRange(point.X, (int)Math.Floor(region.Left * scale), (int)Math.Ceiling(region.Right * scale) - 1);
            Assert.InRange(point.Y, (int)Math.Floor(region.Top * scale), (int)Math.Ceiling(region.Bottom * scale) - 1);
        }
        Assert.DoesNotContain(platform.Clicks, item => item.Name == "loop-details");
    }

    /// <summary>读取嵌入 PNG，裁掉整窗边框或嵌入原比例局部截图，最终只做统一等比缩放。</summary>
    private static GameFrame LoadFrame(string name, double scale, bool replaceArtwork = false)
    {
        using var original = LoadImage(name);
        if (replaceArtwork)
        {
            using var opening = LoadImage("opening.png");
            using var artwork = new Mat(opening, new Rect(228, 185, 253, 344));
            using var replacement = new Mat();
            var region = new Rect(255, 274, 995, 380);
            Cv2.Resize(artwork, replacement, region.Size, 0, 0, InterpolationFlags.Linear);
            using var destination = new Mat(original, region);
            replacement.CopyTo(destination);
        }
        using var canvas = new Mat(1152, 2048, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        if (original.Width == 2050 && original.Height == 1184)
        {
            using var client = new Mat(original, new Rect(1, 31, 2048, 1152));
            client.CopyTo(canvas);
        }
        else
        {
            var origin = new Point(0, 0);
            using var destination = new Mat(canvas, new Rect(origin.X, origin.Y, original.Width, original.Height));
            original.CopyTo(destination);
        }
        using var resized = new Mat();
        Cv2.Resize(canvas, resized, new Size((int)Math.Round(2048 * scale), (int)Math.Round(1152 * scale)),
            0, 0, InterpolationFlags.Area);
        var pixels = new byte[checked(resized.Width * resized.Height * 4)];
        Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
        return new GameFrame(17, resized.Width, resized.Height, 40, 60, pixels,
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    }

    /// <summary>用真实 OpenCV 解码测试程序集中的用户截图，并转换为紧密 BGRA。</summary>
    private static Mat LoadImage(string name)
    {
        using var stream = typeof(FreePackAutomationIntegrationTests).Assembly.GetManifestResourceStream(
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

    /// <summary>按生产同包规则计算两个真实标题指纹的六十四位汉明距离。</summary>
    private static int HammingDistance(string first, string second)
        => BitOperations.PopCount(Convert.ToUInt64(first, 16) ^ Convert.ToUInt64(second, 16));

    /// <summary>保存一张完整录制帧及其来源页面名称。</summary>
    /// <param name="Name">用于核对点击所属页面的录制名称。</param>
    /// <param name="Frame">已经归一到固定客户区的真实像素帧。</param>
    private sealed record RecordedFrame(string Name, GameFrame Frame);

    /// <summary>只回放真实截图并记录输入意图，不寻找或操作任何系统窗口。</summary>
    private sealed class RecordedPlatform : IGameAutomationPlatform
    {
        /// <summary>每个页面连续回放两帧，为生产稳定性核验提供独立捕获。</summary>
        private readonly RecordedFrame[] frames;
        /// <summary>捕获时记录状态机已经发布的计数。</summary>
        private readonly RecordingProgress progress;
        /// <summary>下一次捕获对应的录制索引。</summary>
        private int nextFrame;
        /// <summary>最近捕获页面名称，用于核对真实动作出处。</summary>
        private string currentName = "";
        /// <summary>虚拟截图 UTC，仅由请求的观察等待推进。</summary>
        private DateTimeOffset clock = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        /// <summary>实际记录的页面名称与客户区点击坐标。</summary>
        public List<(string Name, PixelPoint Point)> Clicks { get; } = [];
        /// <summary>实际捕获时所属页面和已发布的业务计数。</summary>
        public List<(string Name, int ScannedPacks, int OpenedPacks)> Captures { get; } = [];
        /// <summary>仅记录于内存的诊断原因。</summary>
        public List<string> Diagnostics { get; } = [];
        /// <summary>实际激活请求次数。</summary>
        public int Activations { get; private set; }
        /// <summary>实际会话清理请求次数。</summary>
        public int EndCalls { get; private set; }
        /// <summary>正常录制流程不产生停止按键请求。</summary>
        public bool IsStopRequested => false;
        /// <summary>保存录制页面和进度接收器，页面内部帧不会修改。</summary>
        public RecordedPlatform(RecordedFrame[] sequence, RecordingProgress progress)
        {
            frames = sequence.SelectMany(item => new[] { item, item }).ToArray();
            this.progress = progress;
        }
        /// <summary>仅记录激活请求。</summary>
        public void ActivateGame() => Activations++;
        /// <summary>返回下一张真实截图副本；末帧保持以便异常流程自然超时停止。</summary>
        public GameFrame Capture()
        {
            var recorded = frames[Math.Min(nextFrame++, frames.Length - 1)];
            currentName = recorded.Name;
            Captures.Add((currentName, progress.Latest.ScannedPacks, progress.Latest.OpenedPacks));
            return recorded.Frame with { CapturedAtUtc = clock, Pixels = recorded.Frame.Pixels.ToArray() };
        }
        /// <summary>记录动作时再次检查点位属于固定客户区，不发送鼠标输入。</summary>
        public void Click(GameFrame frame, PixelPoint point)
        {
            Assert.InRange(point.X, 0, frame.Width - 1);
            Assert.InRange(point.Y, 0, frame.Height - 1);
            Clicks.Add((currentName, point));
        }
        /// <summary>推进请求等待的虚拟时间，真实取消仍立即生效。</summary>
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock += delay;
            return Task.CompletedTask;
        }
        /// <summary>只保留诊断原因，测试不创建截图文件。</summary>
        public void SaveDiagnostic(GameFrame frame, string reason) => Diagnostics.Add(reason);
        /// <summary>记录会话结束，不注册或卸载真实热键。</summary>
        public void EndAutomation() => EndCalls++;
    }

    /// <summary>同步保留最新进度，使录制帧可核对扫描身份没有提前改变。</summary>
    private sealed class RecordingProgress : IProgress<FreePackProgress>
    {
        /// <summary>状态机最近发布的完整计数快照。</summary>
        public FreePackProgress Latest { get; private set; } = new();
        /// <summary>保存最新阶段与计数。</summary>
        public void Report(FreePackProgress value) => Latest = value;
    }
}
