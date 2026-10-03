using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using System.ComponentModel;
using System.Diagnostics;
using Xunit;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace MasterDuelSwitcher.Tests;

/// <summary>通过可注入 Windows 边界验证免费卡包截图、输入及诊断。</summary>
public sealed class WindowsGameAutomationTests : IDisposable
{
    /// <summary>本次测试的独立状态目录。</summary>
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FreePackPlatform-" + Guid.NewGuid().ToString("N"));

    /// <summary>验证异步前台切换需要多次观察时，初始化仍能获得完整客户区。</summary>
    [Fact]
    public void ActivationWaitsForAsynchronousForegroundBeforeReadingClient()
    {
        var native = new FixtureNativeApi { Foreground = 99, ForegroundReadyAfterReads = 3 };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        Assert.Equal(1, native.BeginCount);
        Assert.Equal((nint)42, platform.Capture().WindowHandle);
        Assert.True(native.ForegroundReadCount >= 3);
    }

    /// <summary>验证前台始终未到位时等待有上限，禁止鼠标输入并记录窗口状态和释放停止监听。</summary>
    [Fact]
    public void ActivationNeverReceivingForegroundStopsWithinBoundAndLogsItsState()
    {
        var native = new FixtureNativeApi { Foreground = 99 };
        var logger = new WindowLogger();
        var platform = new WindowsGameAutomationPlatform(directory, native, logger);
        var wait = Stopwatch.StartNew();
        Assert.Throws<InvalidOperationException>(() => platform.ActivateGame());
        Assert.InRange(wait.Elapsed.TotalSeconds, 1.8, 3);
        Assert.Equal(1, native.BeginCount);
        Assert.Equal(0u, native.InputCount);
        var state = Assert.Single(logger.Warnings);
        Assert.Equal((nint)42, state["ExpectedHWND"]);
        Assert.Equal(7u, state["ExpectedPID"]);
        Assert.Equal(7u, state["ActualPID"]);
        Assert.Equal((nint)99, state["ActualForeground"]);
        Assert.Equal(true, state["Visible"]);
        Assert.Equal(false, state["Iconic"]);
        platform.EndAutomation();
        Assert.Equal(1, native.EndCount);
    }

    /// <summary>验证启动等待结束后失焦仍立即拒绝截图，不通过再次等待恢复自动化。</summary>
    [Fact]
    public void RuntimeForegroundLossStillStopsImmediatelyAndLogsTheRejectedSnapshot()
    {
        var native = new FixtureNativeApi();
        var logger = new WindowLogger();
        var platform = new WindowsGameAutomationPlatform(directory, native, logger);
        platform.ActivateGame();
        native.Foreground = 99;
        native.ForegroundReadyAfterReads = native.ForegroundReadCount + 2;
        var wait = Stopwatch.StartNew();
        Assert.Throws<InvalidOperationException>(() => platform.Capture());
        Assert.True(wait.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(0u, native.InputCount);
        Assert.Equal((nint)99, Assert.Single(logger.Warnings)["ActualForeground"]);
    }

    /// <summary>验证等待异步前台期间短按并松开的 F8 被锁存，焦点后来到位也不继续读取客户区。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActivationForegroundWaitLatchesReleasedF8AndStopsBeforeClientRead(bool foregroundEventuallyArrives)
    {
        var native = new FixtureNativeApi
        {
            Foreground = 99,
            ForegroundReadyAfterReads = foregroundEventuallyArrives ? 3 : 0,
            TapF8OnForegroundRead = true
        };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        var wait = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => platform.ActivateGame());
        Assert.True(wait.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(1, native.BeginCount);
        Assert.Equal(0, native.ClientReadCount);
        Assert.Equal(0u, native.InputCount);
        platform.EndAutomation();
        Assert.Equal(1, native.EndCount);
    }

    /// <summary>验证仅激活已确认的游戏窗口并获取紧密 BGRA 客户区。</summary>
    [Fact]
    public void ActivationAndCaptureReturnClientPixelsAndReleaseAllGdiResources()
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native, NullLogger<WindowsGameAutomationPlatform>.Instance);
        platform.ActivateGame();
        GameFrame frame = platform.Capture();
        Assert.Equal((nint)42, frame.WindowHandle);
        Assert.Equal(320, frame.Width);
        Assert.Equal(240, frame.Height);
        Assert.Equal(-300, frame.ScreenX);
        Assert.Equal(200, frame.ScreenY);
        Assert.Equal(320 * 240 * 4, frame.Pixels.Length);
        Assert.Equal(native.UtcNow, frame.CapturedAtUtc);
        Assert.Equal(["dc:2", "bitmap:3", "windowdc:1"], native.Released);
        Assert.Equal(2, native.SelectionCount);
    }

    /// <summary>验证桌面截图使用复验后的客户区物理屏幕原点，包含多屏负坐标。</summary>
    [Theory]
    [InlineData(-300, 200)]
    [InlineData(125, -240)]
    public void CaptureCopiesVisibleClientFromItsPhysicalScreenOrigin(int screenX, int screenY)
    {
        var native = new FixtureNativeApi { Origin = new(screenX, screenY) };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        GameFrame frame = platform.Capture();
        Assert.Equal((frame.ScreenX, frame.ScreenY), native.LastCopySource);
        Assert.Equal((frame.Width, frame.Height), native.LastCopySize);
    }

    /// <summary>验证多屏负坐标使用虚拟桌面绝对鼠标输入。</summary>
    [Fact]
    public void ClickMapsNegativeClientCoordinatesAndUsesOneCompleteInputBatch()
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        var frame = platform.Capture();
        platform.Click(frame, new PixelPoint(100, 50));
        Assert.Equal(3u, native.InputCount);
        Assert.Equal((31137, 8196), native.LastInput);
        native.F8Pressed = true;
        Assert.True(platform.IsStopRequested);
        native.F8Pressed = false;
        Assert.False(platform.IsStopRequested);
    }

    /// <summary>验证未激活和不可信窗口均保持输入为空。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-process")]
    [InlineData("zero-pid")]
    [InlineData("activation-failed")]
    [InlineData("foreground-lost")]
    public void ActivationRejectsUntrustedWindows(string fault)
    {
        var native = new FixtureNativeApi();
        switch (fault)
        {
            case "missing": native.Window = 0; break;
            case "wrong-process": native.GameWindow = false; break;
            case "zero-pid": native.ProcessId = 0; break;
            case "activation-failed": native.ActivationSuccess = false; break;
            case "foreground-lost": native.Foreground = 99; break;
        }
        var platform = new WindowsGameAutomationPlatform(directory, native);
        Assert.ThrowsAny<Exception>(() => platform.ActivateGame());
        Assert.Equal(0u, native.InputCount);
    }

    /// <summary>验证截图前拒绝失焦、隐藏、最小化、移动及无效客户区。</summary>
    [Theory]
    [InlineData("unactivated")]
    [InlineData("wrong-process")]
    [InlineData("pid-changed")]
    [InlineData("foreground")]
    [InlineData("hidden")]
    [InlineData("minimized")]
    [InlineData("client-failed")]
    [InlineData("origin-failed")]
    [InlineData("empty-width")]
    [InlineData("empty-height")]
    [InlineData("too-large")]
    [InlineData("too-wide")]
    [InlineData("too-tall")]
    public void CaptureRejectsUnavailableClientBeforeAllocatingGdi(string fault)
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native);
        if (fault != "unactivated") platform.ActivateGame();
        switch (fault)
        {
            case "wrong-process": native.GameWindow = false; break;
            case "pid-changed": native.ProcessId = 8; break;
            case "foreground": native.Foreground = 99; break;
            case "hidden": native.Visible = false; break;
            case "minimized": native.Minimized = true; break;
            case "client-failed": native.ClientSuccess = false; break;
            case "origin-failed": native.OriginSuccess = false; break;
            case "empty-width": native.Client = new(0, 0, 0, 240); break;
            case "empty-height": native.Client = new(0, 0, 320, 0); break;
            case "too-large": native.Client = new(0, 0, 16384, 16384); break;
            case "too-wide": native.Client = new(0, 0, 16385, 240); break;
            case "too-tall": native.Client = new(0, 0, 320, 16385); break;
        }
        Assert.Throws<InvalidOperationException>(() => platform.Capture());
        Assert.Empty(native.Released);
    }

    /// <summary>验证每个 GDI 失败点均释放此前已分配的资源。</summary>
    [Theory]
    [InlineData("window-dc", 0)]
    [InlineData("memory-dc", 1)]
    [InlineData("bitmap", 2)]
    [InlineData("select-zero", 3)]
    [InlineData("select-error", 3)]
    [InlineData("copy", 3)]
    [InlineData("restore", 3)]
    [InlineData("restore-error", 3)]
    [InlineData("restore-persistent", 3)]
    [InlineData("pixels", 3)]
    public void CaptureReleasesResourcesWhenNativeCaptureFails(string fault, int releases)
    {
        var native = new FixtureNativeApi { Fault = fault };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        Assert.Throws<Win32Exception>(() => platform.Capture());
        Assert.Equal(releases, native.Released.Count);
        if (fault == "restore-persistent") Assert.Equal(["dc:2", "bitmap:3", "windowdc:1"], native.Released);
    }

    /// <summary>验证截图期间几何变化会丢弃画面并释放所有资源。</summary>
    [Fact]
    public void CaptureRejectsWindowMovingDuringRead()
    {
        var native = new FixtureNativeApi { MoveAfterPixelRead = true };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        Assert.Throws<InvalidOperationException>(() => platform.Capture());
        Assert.Equal(3, native.Released.Count);
    }

    /// <summary>验证过期、未来、越界、窗口变化、F8及虚拟桌面异常均中止输入。</summary>
    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("top")]
    [InlineData("bottom")]
    [InlineData("handle")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("f8")]
    [InlineData("late-f8")]
    [InlineData("desktop-width")]
    [InlineData("desktop-height")]
    [InlineData("outside-x")]
    [InlineData("outside-y")]
    [InlineData("past-desktop-x")]
    [InlineData("past-desktop-y")]
    public void ClickRejectsUntrustedFramesWithoutSendingInput(string fault)
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        var frame = platform.Capture();
        var point = new PixelPoint(100, 50);
        switch (fault)
        {
            case "stale": frame = frame with { CapturedAtUtc = native.UtcNow.AddSeconds(-3) }; break;
            case "future": frame = frame with { CapturedAtUtc = native.UtcNow.AddSeconds(1) }; break;
            case "left": point = new(-1, 50); break;
            case "right": point = new(frame.Width, 50); break;
            case "top": point = new(100, -1); break;
            case "bottom": point = new(100, frame.Height); break;
            case "handle": frame = frame with { WindowHandle = 43 }; break;
            case "width": frame = frame with { Width = 319 }; break;
            case "height": frame = frame with { Height = 239 }; break;
            case "x": frame = frame with { ScreenX = -299 }; break;
            case "y": frame = frame with { ScreenY = 199 }; break;
            case "f8": native.F8Pressed = true; break;
            case "late-f8": native.F8OnSecondRead = true; break;
            case "desktop-width": native.Desktop = new(0, 0, 1, 2000); break;
            case "desktop-height": native.Desktop = new(0, 0, 4000, 1); break;
            case "outside-x": native.Desktop = new(0, 0, 4000, 2000); break;
            case "outside-y": native.Desktop = new(-2100, 300, 1900, 2300); break;
            case "past-desktop-x": native.Desktop = new(-2100, 0, -300, 2000); break;
            case "past-desktop-y": native.Desktop = new(-2100, 0, 1900, 200); break;
        }
        Assert.ThrowsAny<Exception>(() => platform.Click(frame, point));
        Assert.Equal(0u, native.InputCount);
    }

    /// <summary>验证不完整的鼠标输入批次明确报告系统错误。</summary>
    [Fact]
    public void ClickRejectsPartialInputBatch()
    {
        var native = new FixtureNativeApi { SendCount = 2 };
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        Assert.Throws<Win32Exception>(() => platform.Click(platform.Capture(), new(10, 10)));
        Assert.Throws<ArgumentNullException>(() => platform.Click(null!, new(10, 10)));
    }

    /// <summary>验证每轮结束释放紧急停止，并在释放失败时仍清除窗口锁定。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndAutomationAlwaysClearsWindowState(bool cleanupFailure)
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native);
        platform.ActivateGame();
        Assert.Equal(1, native.BeginCount);
        native.Fault = cleanupFailure ? "end-stop" : "";
        if (cleanupFailure) Assert.Throws<Win32Exception>(() => platform.EndAutomation());
        else platform.EndAutomation();
        Assert.Equal(1, native.EndCount);
        Assert.Throws<InvalidOperationException>(() => platform.Capture());
    }

    /// <summary>验证等待尊重取消，诊断截图仅保留五张并完整编码 PNG。</summary>
    [Fact]
    public async Task DelayAndBoundedDiagnosticsUseIsolatedStateDirectory()
    {
        var native = new FixtureNativeApi();
        var platform = new WindowsGameAutomationPlatform(directory, native, NullLogger<WindowsGameAutomationPlatform>.Instance);
        platform.ActivateGame();
        await platform.DelayAsync(TimeSpan.Zero, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => platform.DelayAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        var frame = platform.Capture();
        for (int count = 0; count < 7; count++) platform.SaveDiagnostic(frame, "识别未知");
        string diagnostic = Path.Combine(directory, "free-pack-diagnostics");
        string[] files = Directory.GetFiles(diagnostic, "free-pack-*.png");
        Assert.Equal(5, files.Length);
        foreach (string file in files)
        {
            using Mat image = Cv2.ImRead(file, ImreadModes.Unchanged);
            Assert.Equal(frame.Width, image.Width);
            Assert.Equal(frame.Height, image.Height);
            Assert.Equal(3, image.Channels());
        }
        Assert.Throws<ArgumentException>(() => new WindowsGameAutomationPlatform(" ", native));
        _ = new WindowsGameAutomationPlatform(directory);
    }

    /// <summary>移除本次测试专属数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    /// <summary>记录句柄生命周期和输入的可控 Windows API 边界。</summary>
    private sealed class FixtureNativeApi : IWindowsGameAutomationNativeApi
    {
        /// <summary>测试窗口句柄。</summary>
        public nint Window { get; set; } = 42;
        /// <summary>前台窗口句柄。</summary>
        public nint Foreground { get; set; } = 42;
        /// <summary>模拟前台异步切换在指定观察次数之后完成；零表示保持原前台。</summary>
        public int ForegroundReadyAfterReads { get; set; }
        /// <summary>前台窗口查询次数。</summary>
        public int ForegroundReadCount { get; private set; }
        /// <summary>模拟第一次前台观察期间短按并松开 F8。</summary>
        public bool TapF8OnForegroundRead { get; set; }
        /// <summary>已注册监听在短按松开之后保留的停止事件。</summary>
        private bool f8TapLatched;
        /// <summary>读取客户区矩形的次数。</summary>
        public int ClientReadCount { get; private set; }
        /// <summary>窗口所属进程。</summary>
        public uint ProcessId { get; set; } = 7;
        /// <summary>是否仍属于目标游戏。</summary>
        public bool GameWindow { get; set; } = true;
        /// <summary>是否可见。</summary>
        public bool Visible { get; set; } = true;
        /// <summary>是否最小化。</summary>
        public bool Minimized { get; set; }
        /// <summary>激活结果。</summary>
        public bool ActivationSuccess { get; set; } = true;
        /// <summary>客户区查询结果。</summary>
        public bool ClientSuccess { get; set; } = true;
        /// <summary>原点查询结果。</summary>
        public bool OriginSuccess { get; set; } = true;
        /// <summary>客户区矩形。</summary>
        public AutomationNativeRectangle Client { get; set; } = new(0, 0, 320, 240);
        /// <summary>客户区屏幕原点。</summary>
        public AutomationNativePoint Origin { get; set; } = new(-300, 200);
        /// <summary>虚拟桌面矩形。</summary>
        public AutomationNativeRectangle Desktop { get; set; } = new(-2100, 0, 1900, 2000);
        /// <summary>当前测试时间。</summary>
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
        /// <summary>F8 状态。</summary>
        public bool F8Pressed { get; set; }
        /// <summary>是否在输入前最后一次复核时触发停止。</summary>
        public bool F8OnSecondRead { get; set; }
        /// <summary>紧急停止监听启动次数。</summary>
        public int BeginCount { get; private set; }
        /// <summary>紧急停止监听释放次数。</summary>
        public int EndCount { get; private set; }
        /// <summary>读取紧急停止状态的次数。</summary>
        private int f8ReadCount;
        /// <summary>本次注入的系统失败点。</summary>
        public string Fault { get; set; } = "";
        /// <summary>是否在像素读出后移动窗口。</summary>
        public bool MoveAfterPixelRead { get; set; }
        /// <summary>位图选入次数。</summary>
        public int SelectionCount { get; private set; }
        /// <summary>已释放资源记录。</summary>
        public List<string> Released { get; } = [];
        /// <summary>发送的输入数量。</summary>
        public uint InputCount { get; private set; }
        /// <summary>系统接受的输入数量。</summary>
        public uint SendCount { get; set; } = 3;
        /// <summary>最后输入坐标。</summary>
        public (int X, int Y) LastInput { get; private set; }
        /// <summary>最后截图在源设备上下文中的起点。</summary>
        public (int X, int Y) LastCopySource { get; private set; }
        /// <summary>最后截图复制的客户区尺寸。</summary>
        public (int Width, int Height) LastCopySize { get; private set; }
        /// <summary>返回游戏窗口。</summary>
        public nint FindGameWindow() => Window;
        /// <summary>确认窗口身份。</summary>
        public bool IsGameWindow(nint window) => GameWindow;
        /// <summary>返回窗口所属进程。</summary>
        public uint GetWindowProcessId(nint window) => ProcessId;
        /// <summary>返回前台窗口。</summary>
        public nint GetForegroundWindow()
        {
            ForegroundReadCount++;
            if (TapF8OnForegroundRead && ForegroundReadCount == 1 && BeginCount > 0) f8TapLatched = true;
            return ForegroundReadyAfterReads > 0 && ForegroundReadCount >= ForegroundReadyAfterReads ? Window : Foreground;
        }
        /// <summary>返回窗口可见状态。</summary>
        public bool IsWindowVisible(nint window) => Visible;
        /// <summary>返回窗口最小化状态。</summary>
        public bool IsWindowMinimized(nint window) => Minimized;
        /// <summary>激活窗口。</summary>
        public bool ActivateWindow(nint window) => ActivationSuccess;
        /// <summary>查询客户区。</summary>
        public bool GetClientRectangle(nint window, out AutomationNativeRectangle rectangle) { ClientReadCount++; rectangle = Client; return ClientSuccess; }
        /// <summary>转换客户区原点。</summary>
        public bool ClientToScreen(nint window, ref AutomationNativePoint point) { point = Origin; return OriginSuccess; }
        /// <summary>分配窗口 DC。</summary>
        public nint GetClientDc(nint window) => Fault == "window-dc" ? 0 : 1;
        /// <summary>分配内存 DC。</summary>
        public nint CreateMemoryDc(nint source) => Fault == "memory-dc" ? 0 : 2;
        /// <summary>分配位图。</summary>
        public nint CreateBitmap(nint source, int width, int height) => Fault == "bitmap" ? 0 : 3;
        /// <summary>选入或还原位图。</summary>
        public nint SelectBitmap(nint dc, nint bitmap)
        {
            SelectionCount++;
            if (Fault == "select-zero" || Fault == "restore" && SelectionCount == 2) return 0;
            if (Fault == "restore-persistent" && SelectionCount >= 2) return 0;
            if (Fault == "select-error" || Fault == "restore-error" && SelectionCount == 2) return -1;
            return 4;
        }
        /// <summary>复制客户区像素。</summary>
        public bool CopyPixels(nint target, nint source, int width, int height, int sourceX, int sourceY)
        {
            LastCopySource = (sourceX, sourceY);
            LastCopySize = (width, height);
            return Fault != "copy";
        }
        /// <summary>读取位图像素。</summary>
        public int ReadPixels(nint dc, nint bitmap, int width, int height, byte[] pixels)
        {
            if (MoveAfterPixelRead) Origin = new(Origin.X + 1, Origin.Y);
            return Fault == "pixels" ? 0 : height;
        }
        /// <summary>释放位图。</summary>
        public void DeleteBitmap(nint bitmap) => Released.Add("bitmap:" + bitmap);
        /// <summary>释放内存 DC。</summary>
        public void DeleteMemoryDc(nint dc) => Released.Add("dc:" + dc);
        /// <summary>释放窗口 DC。</summary>
        public void ReleaseClientDc(nint window, nint dc) => Released.Add("windowdc:" + dc);
        /// <summary>返回虚拟桌面范围。</summary>
        public AutomationNativeRectangle GetVirtualDesktop() => Desktop;
        /// <summary>发送完整鼠标批次。</summary>
        public uint SendMouseClick(int normalizedX, int normalizedY) { InputCount = 3; LastInput = (normalizedX, normalizedY); return SendCount; }
        /// <summary>返回 F8 紧急停止状态。</summary>
        public bool IsF8Pressed => f8TapLatched || F8Pressed || F8OnSecondRead && ++f8ReadCount == 2;
        /// <summary>记录紧急停止监听开始。</summary>
        public void BeginEmergencyStop() => BeginCount++;
        /// <summary>记录紧急停止监听结束。</summary>
        public void EndEmergencyStop()
        {
            EndCount++;
            if (Fault == "end-stop") throw new Win32Exception();
        }
    }

    /// <summary>记录窗口拒绝事件的结构化字段，避免日志丢失实际失焦原因。</summary>
    private sealed class WindowLogger : ILogger<WindowsGameAutomationPlatform>
    {
        /// <summary>收到的窗口拒绝诊断字段。</summary>
        internal List<Dictionary<string, object?>> Warnings { get; } = [];
        /// <summary>测试无需额外日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>所有日志等级均参与记录。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>提取警告中的结构化状态，不记录用户账号信息。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }
}
