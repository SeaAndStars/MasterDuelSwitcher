using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>以客户端物理像素执行游戏截图和普通鼠标输入的 Windows 适配器。</summary>
public sealed class WindowsGameAutomationPlatform : IGameAutomationPlatform
{
    /// <summary>一帧允许占用的最大 BGRA 字节数。</summary>
    private const long MaximumFrameBytes = 128L * 1024 * 1024;
    /// <summary>防止异常客户区维度引起乘法溢出或过量分配。</summary>
    private const int MaximumFrameDimension = 16384;
    /// <summary>经过注入的 Windows 原生函数边界。</summary>
    private readonly IWindowsGameAutomationNativeApi native;
    /// <summary>仅用于自动化诊断的系统数据子目录。</summary>
    private readonly string diagnosticDirectory;
    /// <summary>记录窗口校验、截图与鼠标动作的日志。</summary>
    private readonly ILogger<WindowsGameAutomationPlatform> logger;
    /// <summary>本轮激活并锁定的游戏窗口。</summary>
    private nint activeWindow;
    /// <summary>本轮锁定的游戏进程标识，防止句柄被其他进程复用。</summary>
    private uint activeProcessId;
    /// <summary>激活时确定的客户区位置和大小。</summary>
    private GameFrame? activatedFrame;

    /// <summary>创建独立数据目录与可注入原生边界的适配器。</summary>
    public WindowsGameAutomationPlatform(string stateDirectory, IWindowsGameAutomationNativeApi? nativeApi = null, ILogger<WindowsGameAutomationPlatform>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        diagnosticDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "free-pack-diagnostics");
        this.logger = logger ?? NullLogger<WindowsGameAutomationPlatform>.Instance;
        native = nativeApi ?? new SystemWindowsGameAutomationNativeApi(this.logger);
    }

    /// <summary>激活目标游戏窗口。</summary>
    public void ActivateGame()
    {
        nint window = native.FindGameWindow();
        if (window == 0 || !native.IsGameWindow(window))
            throw new InvalidOperationException("未找到可用的 Master Duel 游戏窗口。");
        uint processId = native.GetWindowProcessId(window);
        if (processId == 0)
            throw new InvalidOperationException("游戏窗口所属进程已退出。");
        logger.LogDebug("FreePackWindowFound ExpectedHWND={ExpectedHWND} ExpectedPID={ExpectedPID}", window, processId);
        if (!native.ActivateWindow(window))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "游戏窗口激活请求未成功。");
        activeWindow = window;
        activeProcessId = processId;
        native.BeginEmergencyStop();
        if (!SpinWait.SpinUntil(() =>
        {
            nint foreground = native.GetForegroundWindow();
            if (native.IsF8Pressed) throw new OperationCanceledException("F8 已请求停止免费开包。");
            return foreground == window;
        }, TimeSpan.FromSeconds(2)))
        {
            logger.LogWarning("FreePackWindowRejected Stage={Stage} ExpectedHWND={ExpectedHWND} ExpectedPID={ExpectedPID} ActualPID={ActualPID} ActualForeground={ActualForeground} Visible={Visible} Iconic={Iconic}",
                "ActivationForegroundTimeout", window, processId, native.GetWindowProcessId(window), native.GetForegroundWindow(), native.IsWindowVisible(window), native.IsWindowMinimized(window));
            throw new InvalidOperationException("游戏窗口在两秒内未取得前台，自动开包已停止。");
        }
        activatedFrame = ReadClientFrame();
        logger.LogInformation("FreePackWindowActivated Width={Width} Height={Height}", activatedFrame.Width, activatedFrame.Height);
    }

    /// <summary>尝试恢复本轮原游戏窗口，并保留窗口身份、几何与紧急停止监听。</summary>
    public bool TryRecoverGame()
    {
        if (native.IsF8Pressed) throw new OperationCanceledException("F8 已请求停止免费开包。");
        if (activeWindow == 0 || activatedFrame is null)
            throw new InvalidOperationException("尚未锁定本轮游戏窗口及客户区，恢复请求已停止。");
        if (!native.IsGameWindow(activeWindow) || native.GetWindowProcessId(activeWindow) != activeProcessId)
            throw new InvalidOperationException("原游戏窗口已退出或所属进程已经变化，恢复请求已停止。");
        bool requested = native.ActivateWindow(activeWindow);
        logger.LogDebug("FreePackWindowRecoveryRequested ExpectedHWND={ExpectedHWND} ExpectedPID={ExpectedPID} RequestAccepted={RequestAccepted}",
            activeWindow, activeProcessId, requested);
        if (native.IsF8Pressed) throw new OperationCanceledException("F8 已请求停止免费开包。");
        GameFrame? recovered = null;
        try { recovered = ReadClientFrame(); }
        catch (GameWindowTemporarilyUnavailableException) { }
        if (native.IsF8Pressed) throw new OperationCanceledException("F8 已请求停止免费开包。");
        if (recovered is null) return false;
        EnsureSameGeometry(activatedFrame, recovered);
        logger.LogInformation("FreePackWindowRecovered ExpectedHWND={ExpectedHWND} ExpectedPID={ExpectedPID} Width={Width} Height={Height}",
            activeWindow, activeProcessId, recovered.Width, recovered.Height);
        return true;
    }

    /// <summary>读取前台游戏客户区。</summary>
    public GameFrame Capture()
    {
        GameFrame geometry = ReadClientFrame();
        EnsureSameGeometry(activatedFrame!, geometry);
        nint windowDc = 0;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previous = 0;
        try
        {
            windowDc = native.GetClientDc(activeWindow);
            if (windowDc == 0) throw CaptureError();
            memoryDc = native.CreateMemoryDc(windowDc);
            if (memoryDc == 0) throw CaptureError();
            bitmap = native.CreateBitmap(windowDc, geometry.Width, geometry.Height);
            if (bitmap == 0) throw CaptureError();
            nint selected = native.SelectBitmap(memoryDc, bitmap);
            if (selected == 0 || selected == -1) throw CaptureError();
            previous = selected;
            if (!native.CopyPixels(memoryDc, windowDc, geometry.Width, geometry.Height, geometry.ScreenX, geometry.ScreenY)) throw CaptureError();
            nint restored = native.SelectBitmap(memoryDc, previous);
            if (restored == 0 || restored == -1) throw CaptureError();
            previous = 0;
            byte[] pixels = new byte[geometry.Width * geometry.Height * 4];
            if (native.ReadPixels(memoryDc, bitmap, geometry.Width, geometry.Height, pixels) != geometry.Height)
                throw CaptureError();
            EnsureSameGeometry(geometry, ReadClientFrame());
            logger.LogDebug("FreePackFrameCaptured Width={Width} Height={Height} SourceX={SourceX} SourceY={SourceY}", geometry.Width, geometry.Height, geometry.ScreenX, geometry.ScreenY);
            return geometry with { Pixels = pixels, CapturedAtUtc = native.UtcNow };
        }
        finally
        {
            if (previous != 0) native.SelectBitmap(memoryDc, previous);
            if (memoryDc != 0) native.DeleteMemoryDc(memoryDc);
            if (bitmap != 0) native.DeleteBitmap(bitmap);
            if (windowDc != 0) native.ReleaseClientDc(activeWindow, windowDc);
        }
    }

    /// <summary>复验画面后发送客户区鼠标点击。</summary>
    public void Click(GameFrame frame, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GameFrame current = ReadClientFrame();
        EnsureSameGeometry(activatedFrame!, current);
        EnsureSameGeometry(frame, current);
        TimeSpan age = native.UtcNow - frame.CapturedAtUtc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("识别画面已经过期，请重新捕获。");
        if (point.X < 0 || point.X >= frame.Width || point.Y < 0 || point.Y >= frame.Height)
            throw new InvalidOperationException("识别的点击目标超出游戏客户区。");
        if (native.IsF8Pressed)
            throw new OperationCanceledException("F8 已请求停止免费开包。");
        AutomationNativeRectangle desktop = native.GetVirtualDesktop();
        long width = (long)desktop.Right - desktop.Left;
        long height = (long)desktop.Bottom - desktop.Top;
        long physicalX = (long)frame.ScreenX + point.X;
        long physicalY = (long)frame.ScreenY + point.Y;
        long x = physicalX - desktop.Left;
        long y = physicalY - desktop.Top;
        if (width <= 1 || height <= 1 || x < 0 || x >= width || y < 0 || y >= height)
            throw new InvalidOperationException("点击目标超出当前虚拟桌面。");
        int normalizedX = (int)Math.Round(x * 65535.0 / (width - 1));
        int normalizedY = (int)Math.Round(y * 65535.0 / (height - 1));
        EnsureSameGeometry(current, ReadClientFrame());
        if (native.IsF8Pressed)
            throw new OperationCanceledException("F8 已请求停止免费开包。");
        var physicalTarget = new AutomationNativePoint(checked((int)physicalX), checked((int)physicalY));
        if (native.SendMouseClick(normalizedX, normalizedY, physicalTarget, () =>
        {
            EnsureSameGeometry(current, ReadClientFrame());
            if (native.IsF8Pressed)
                throw new OperationCanceledException("F8 已请求停止免费开包。");
        }) != 3)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "鼠标输入批次未完整发送。");
        logger.LogDebug("FreePackMouseClick ClientX={X} ClientY={Y} ScreenX={ScreenX} ScreenY={ScreenY}", point.X, point.Y, physicalTarget.X, physicalTarget.Y);
    }

    /// <summary>读取 F8 紧急停止状态。</summary>
    public bool IsStopRequested => native.IsF8Pressed;

    /// <summary>释放本轮 F8 热键并清除锁定的窗口状态。</summary>
    public void EndAutomation()
    {
        try { native.EndEmergencyStop(); }
        finally
        {
            activeWindow = 0;
            activeProcessId = 0;
            activatedFrame = null;
        }
    }

    /// <summary>执行可取消的等待。</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>保存有限数量的本地诊断截图。</summary>
    public void SaveDiagnostic(GameFrame frame, string reason)
    {
        Directory.CreateDirectory(diagnosticDirectory);
        using Mat image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels);
        using Mat opaque = new();
        Cv2.CvtColor(image, opaque, ColorConversionCodes.BGRA2BGR);
        byte[] png = opaque.ImEncode(".png");
        string path = Path.Combine(diagnosticDirectory, "free-pack-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, png);
        foreach (FileInfo old in new DirectoryInfo(diagnosticDirectory).GetFiles("free-pack-*.png")
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name).Skip(5))
            old.Delete();
        logger.LogWarning("FreePackDiagnosticSaved Reason={Reason} RetainedLimit={Limit}", reason, 5);
    }

    /// <summary>读取并校验锁定窗口的物理客户区几何状态。</summary>
    private GameFrame ReadClientFrame()
    {
        bool gameWindow = native.IsGameWindow(activeWindow);
        uint processId = native.GetWindowProcessId(activeWindow);
        nint foreground = native.GetForegroundWindow();
        bool visible = native.IsWindowVisible(activeWindow);
        bool iconic = native.IsWindowMinimized(activeWindow);
        if (activeWindow == 0 || !gameWindow || processId != activeProcessId || foreground != activeWindow || !visible || iconic)
        {
            logger.LogWarning("FreePackWindowRejected Stage={Stage} ExpectedHWND={ExpectedHWND} ExpectedPID={ExpectedPID} ActualPID={ActualPID} ActualForeground={ActualForeground} Visible={Visible} Iconic={Iconic} GameWindow={GameWindow}",
                "ClientValidation", activeWindow, activeProcessId, processId, foreground, visible, iconic, gameWindow);
            if (activeWindow == 0 || !gameWindow || processId != activeProcessId)
                throw new InvalidOperationException("原游戏窗口已退出或所属进程已经变化。");
            throw new GameWindowTemporarilyUnavailableException("游戏窗口暂时失焦、隐藏或最小化。");
        }
        if (!native.GetClientRectangle(activeWindow, out AutomationNativeRectangle rectangle))
            throw new InvalidOperationException("读取游戏客户区失败。");
        AutomationNativePoint origin = new(0, 0);
        if (!native.ClientToScreen(activeWindow, ref origin))
            throw new InvalidOperationException("读取游戏客户区屏幕位置失败。");
        long width = (long)rectangle.Right - rectangle.Left;
        long height = (long)rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > MaximumFrameDimension || height > MaximumFrameDimension
            || width * height * 4 > MaximumFrameBytes)
            throw new InvalidOperationException("游戏客户区为空或超过截图容量限制。");
        return new GameFrame(activeWindow, (int)width, (int)height, origin.X, origin.Y, [], native.UtcNow);
    }

    /// <summary>拒绝窗口句柄、尺寸或物理屏幕原点发生变化的画面。</summary>
    private static void EnsureSameGeometry(GameFrame expected, GameFrame current)
    {
        if (expected.WindowHandle != current.WindowHandle || expected.Width != current.Width || expected.Height != current.Height
            || expected.ScreenX != current.ScreenX || expected.ScreenY != current.ScreenY)
            throw new InvalidOperationException("游戏窗口位置或大小已经变化，自动开包已停止。");
    }

    /// <summary>保留系统错误码并创建统一的截图失败异常。</summary>
    private static Win32Exception CaptureError() => new(Marshal.GetLastWin32Error(), "读取游戏客户区像素失败。");
}

/// <summary>游戏自动化所需的可注入 Windows 原生函数边界。</summary>
public interface IWindowsGameAutomationNativeApi
{
    /// <summary>寻找目标游戏窗口。</summary>
    nint FindGameWindow();
    /// <summary>确认窗口仍属于目标游戏进程。</summary>
    bool IsGameWindow(nint window);
    /// <summary>读取窗口所属进程标识。</summary>
    uint GetWindowProcessId(nint window);
    /// <summary>读取当前前台窗口。</summary>
    nint GetForegroundWindow();
    /// <summary>读取窗口可见状态。</summary>
    bool IsWindowVisible(nint window);
    /// <summary>读取窗口最小化状态。</summary>
    bool IsWindowMinimized(nint window);
    /// <summary>还原并请求激活窗口。</summary>
    bool ActivateWindow(nint window);
    /// <summary>读取物理像素客户区矩形。</summary>
    bool GetClientRectangle(nint window, out AutomationNativeRectangle rectangle);
    /// <summary>把客户区点转换为物理屏幕点。</summary>
    bool ClientToScreen(nint window, ref AutomationNativePoint point);
    /// <summary>取得桌面设备上下文，以可见客户区屏幕原点读取游戏合成画面。</summary>
    nint GetClientDc(nint window);
    /// <summary>创建兼容内存设备上下文。</summary>
    nint CreateMemoryDc(nint source);
    /// <summary>创建兼容客户区位图。</summary>
    nint CreateBitmap(nint source, int width, int height);
    /// <summary>选入位图并返回原对象。</summary>
    nint SelectBitmap(nint dc, nint bitmap);
    /// <summary>从显式物理屏幕原点复制桌面上的可见客户区像素。</summary>
    bool CopyPixels(nint target, nint source, int width, int height, int sourceX, int sourceY);
    /// <summary>读取自顶向下的紧密 BGRA 位图。</summary>
    int ReadPixels(nint dc, nint bitmap, int width, int height, byte[] pixels);
    /// <summary>释放兼容位图。</summary>
    void DeleteBitmap(nint bitmap);
    /// <summary>释放内存设备上下文。</summary>
    void DeleteMemoryDc(nint dc);
    /// <summary>释放桌面截图使用的设备上下文。</summary>
    void ReleaseClientDc(nint window, nint dc);
    /// <summary>读取全部显示器组成的虚拟桌面。</summary>
    AutomationNativeRectangle GetVirtualDesktop();
    /// <summary>先定位并核验物理目标，按下前复验窗口，再跨帧按住并保证松开；完整成功返回三次输入。</summary>
    uint SendMouseClick(int normalizedX, int normalizedY, AutomationNativePoint? physicalTarget = null, Action? beforeButtonDown = null);
    /// <summary>读取 F8 是否处于按下状态。</summary>
    bool IsF8Pressed { get; }
    /// <summary>开始接收运行期间的 F8 热键事件。</summary>
    void BeginEmergencyStop();
    /// <summary>释放本轮 F8 热键资源。</summary>
    void EndEmergencyStop();
    /// <summary>读取当前 UTC 时间。</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>与 Win32 RECT 布局一致的物理像素矩形。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AutomationNativeRectangle
{
    /// <summary>矩形左边界。</summary>
    public int Left;
    /// <summary>矩形上边界。</summary>
    public int Top;
    /// <summary>矩形右边界。</summary>
    public int Right;
    /// <summary>矩形下边界。</summary>
    public int Bottom;
    /// <summary>创建物理像素矩形。</summary>
    public AutomationNativeRectangle(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
}

/// <summary>与 Win32 POINT 布局一致的物理像素点。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AutomationNativePoint
{
    /// <summary>点的水平坐标。</summary>
    public int X;
    /// <summary>点的垂直坐标。</summary>
    public int Y;
    /// <summary>创建物理像素点。</summary>
    public AutomationNativePoint(int x, int y) { X = x; Y = y; }
}

/// <summary>调用 Win32 窗口、GDI 和 SendInput 的真实系统边界。</summary>
public sealed class SystemWindowsGameAutomationNativeApi : IWindowsGameAutomationNativeApi
{
    /// <summary>查找当前目标进程的可替换入口。</summary>
    private readonly Func<Process[]> findProcesses;
    /// <summary>根据进程标识读取进程的可替换入口。</summary>
    private readonly Func<int, Process> findProcess;
    /// <summary>允许操作的进程名称；生产入口固定为 masterduel。</summary>
    private readonly string gameProcessName;
    /// <summary>供故障测试记录批次的输入边界；生产使用真实 SendInput。</summary>
    private readonly Func<NativeInput[], uint> sendInputs;
    /// <summary>跨过游戏鼠标轮询帧的可注入等待；生产使用线程等待。</summary>
    private readonly Action<TimeSpan> holdMouse;
    /// <summary>移动系统鼠标到物理屏幕坐标的可注入入口。</summary>
    private readonly Func<int, int, bool> setCursor;
    /// <summary>读取实际鼠标屏幕坐标的可注入入口。</summary>
    private readonly CursorPositionReader readCursor;
    /// <summary>等待鼠标移动进入游戏画面的可注入入口。</summary>
    private readonly Action<TimeSpan> settleMouse;
    /// <summary>读取停止锁存的可注入入口。</summary>
    private readonly Func<bool> stopRequested;
    /// <summary>兼容两参数调用时取得物理虚拟桌面的入口。</summary>
    private readonly Func<AutomationNativeRectangle> getDesktop;
    /// <summary>读取实际发送线程 DPI 上下文的可注入入口。</summary>
    private readonly Func<nint> getDpiContext;
    /// <summary>记录鼠标实际到达和原生输入故障。</summary>
    private readonly ILogger logger;
    /// <summary>仅在自动化运行期间存在的 F8 消息监听器。</summary>
    private F8EmergencyStopListener? stopListener;

    /// <summary>创建只匹配 Master Duel 的真实系统边界。</summary>
    public SystemWindowsGameAutomationNativeApi(ILogger? logger = null)
        : this(() => Process.GetProcessesByName("masterduel"), Process.GetProcessById, "masterduel", logger: logger) { }

    /// <summary>为隔离 Win32 窗口测试注入进程边界，不对真实游戏发送输入。</summary>
    internal SystemWindowsGameAutomationNativeApi(Func<Process[]> findProcesses, Func<int, Process> findProcess, string gameProcessName,
        Func<NativeInput[], uint>? sendInputs = null, Action<TimeSpan>? holdMouse = null,
        Func<int, int, bool>? setCursor = null, CursorPositionReader? readCursor = null, Action<TimeSpan>? settleMouse = null,
        Func<bool>? stopRequested = null, Func<AutomationNativeRectangle>? getDesktop = null, Func<nint>? getDpiContext = null, ILogger? logger = null)
    {
        this.findProcesses = findProcesses;
        this.findProcess = findProcess;
        this.gameProcessName = gameProcessName;
        this.sendInputs = sendInputs ?? SendSystemInputs;
        this.holdMouse = holdMouse ?? Thread.Sleep;
        this.setCursor = setCursor ?? NativeMethods.SetCursorPos;
        this.readCursor = readCursor ?? NativeMethods.GetCursorPos;
        this.settleMouse = settleMouse ?? Thread.Sleep;
        this.stopRequested = stopRequested ?? (() => IsF8Pressed);
        this.getDesktop = getDesktop ?? GetVirtualDesktop;
        this.getDpiContext = getDpiContext ?? NativeMethods.GetThreadDpiAwarenessContext;
        this.logger = logger ?? NullLogger.Instance;
    }

    /// <summary>寻找目标进程的第一个主窗口，并释放全部进程查询句柄。</summary>
    public nint FindGameWindow()
    {
        nint window = 0;
        foreach (Process process in findProcesses())
        {
            using (process)
                if (window == 0) window = process.MainWindowHandle;
        }
        return window;
    }

    /// <summary>在每次截图或输入前核对窗口进程名称。</summary>
    public bool IsGameWindow(nint window)
    {
        uint id = GetWindowProcessId(window);
        if (id == 0) return false;
        try
        {
            using Process process = findProcess((int)id);
            return string.Equals(process.ProcessName, gameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>读取窗口所属进程，失效窗口返回零。</summary>
    public uint GetWindowProcessId(nint window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint id);
        return id;
    }

    /// <summary>读取前台窗口。</summary>
    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
    /// <summary>读取窗口可见状态。</summary>
    public bool IsWindowVisible(nint window) => NativeMethods.IsWindowVisible(window);
    /// <summary>读取窗口最小化状态。</summary>
    public bool IsWindowMinimized(nint window) => NativeMethods.IsIconic(window);
    /// <summary>还原窗口并请求前台焦点。</summary>
    public bool ActivateWindow(nint window)
    {
        NativeMethods.ShowWindow(window, 9);
        return NativeMethods.SetForegroundWindow(window);
    }
    /// <summary>读取客户区物理像素矩形。</summary>
    public bool GetClientRectangle(nint window, out AutomationNativeRectangle rectangle) => NativeMethods.GetClientRect(window, out rectangle);
    /// <summary>转换客户区点到物理屏幕点。</summary>
    public bool ClientToScreen(nint window, ref AutomationNativePoint point) => NativeMethods.ClientToScreen(window, ref point);
    /// <summary>取得桌面合成后的设备上下文，避免窗口 GDI 表面保留旧 DirectX 帧。</summary>
    public nint GetClientDc(nint window) => NativeMethods.GetDC(0);
    /// <summary>创建兼容内存设备上下文。</summary>
    public nint CreateMemoryDc(nint source) => NativeMethods.CreateCompatibleDC(source);
    /// <summary>创建兼容彩色位图。</summary>
    public nint CreateBitmap(nint source, int width, int height) => NativeMethods.CreateCompatibleBitmap(source, width, height);
    /// <summary>选入或还原 GDI 位图。</summary>
    public nint SelectBitmap(nint dc, nint bitmap) => NativeMethods.SelectObject(dc, bitmap);
    /// <summary>复制前台客户区的实际显示像素。</summary>
    public bool CopyPixels(nint target, nint source, int width, int height, int sourceX, int sourceY) => NativeMethods.BitBlt(target, 0, 0, width, height, source, sourceX, sourceY, 0x40CC0020);

    /// <summary>将已取消选入的兼容位图转换为自顶向下 BGRA。</summary>
    public int ReadPixels(nint dc, nint bitmap, int width, int height, byte[] pixels)
    {
        var info = new NativeBitmapInfo
        {
            Header = new NativeBitmapHeader
            {
                Size = (uint)Marshal.SizeOf<NativeBitmapHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, ImageSize = (uint)(width * height * 4)
            }
        };
        return NativeMethods.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, 0);
    }

    /// <summary>释放创建的兼容位图。</summary>
    public void DeleteBitmap(nint bitmap) => NativeMethods.DeleteObject(bitmap);
    /// <summary>释放创建的内存设备上下文。</summary>
    public void DeleteMemoryDc(nint dc) => NativeMethods.DeleteDC(dc);
    /// <summary>按 GetDC(0) 的配对句柄释放桌面设备上下文。</summary>
    public void ReleaseClientDc(nint window, nint dc) => NativeMethods.ReleaseDC(0, dc);

    /// <summary>读取包含负坐标显示器的完整虚拟桌面。</summary>
    public AutomationNativeRectangle GetVirtualDesktop()
    {
        int left = NativeMethods.GetSystemMetrics(76);
        int top = NativeMethods.GetSystemMetrics(77);
        return new(left, top, left + NativeMethods.GetSystemMetrics(78), top + NativeMethods.GetSystemMetrics(79));
    }

    /// <summary>先定位并单独移动，等待四十毫秒确认实际坐标后按住八十毫秒，按下失败路径均尝试松开。</summary>
    public uint SendMouseClick(int normalizedX, int normalizedY, AutomationNativePoint? physicalTarget = null, Action? beforeButtonDown = null)
    {
        AutomationNativePoint target = physicalTarget ?? ResolvePhysicalTarget(normalizedX, normalizedY);
        bool released = false;
        bool buttonMayBeDown = false;
        try
        {
            ThrowIfStopRequested();
            logger.LogDebug("FreePackMousePositionRequested ExpectedScreenX={ExpectedX} ExpectedScreenY={ExpectedY} NormalizedX={NormalizedX} NormalizedY={NormalizedY} ThreadDpiContext={ThreadDpiContext}",
                target.X, target.Y, normalizedX, normalizedY, getDpiContext());
            SetExpectedCursor(target);
            uint moved = sendInputs([new() { Mouse = new() { X = normalizedX, Y = normalizedY, Flags = 0xC001 } }]);
            if (moved != 1) return 0;
            SetExpectedCursor(target);
            settleMouse(TimeSpan.FromMilliseconds(40));
            ThrowIfStopRequested();
            if (!readCursor(out AutomationNativePoint actual))
            {
                int error = Marshal.GetLastWin32Error();
                logger.LogWarning("FreePackMousePositionReadFailed ExpectedScreenX={ExpectedX} ExpectedScreenY={ExpectedY} Win32Error={Win32Error}", target.X, target.Y, error);
                throw new Win32Exception(error, "读取鼠标实际位置失败。");
            }
            logger.LogDebug("FreePackMousePositionObserved ExpectedScreenX={ExpectedX} ExpectedScreenY={ExpectedY} ActualScreenX={ActualX} ActualScreenY={ActualY}", target.X, target.Y, actual.X, actual.Y);
            if (actual.X != target.X || actual.Y != target.Y)
            {
                logger.LogWarning("FreePackMousePositionRejected ExpectedScreenX={ExpectedX} ExpectedScreenY={ExpectedY} ActualScreenX={ActualX} ActualScreenY={ActualY}", target.X, target.Y, actual.X, actual.Y);
                throw new InvalidOperationException($"鼠标未到达目标：期望 ({target.X},{target.Y})，实际 ({actual.X},{actual.Y})。");
            }
            beforeButtonDown?.Invoke();
            ThrowIfStopRequested();
            buttonMayBeDown = true;
            uint down = sendInputs([new() { Mouse = new() { Flags = 0x0002 } }]);
            if (down != 1) return moved;
            holdMouse(TimeSpan.FromMilliseconds(80));
            ThrowIfStopRequested();
            uint up = sendInputs([new() { Mouse = new() { Flags = 0x0004 } }]);
            released = up == 1;
            return released ? moved + down + up : moved + down;
        }
        finally
        {
            if (buttonMayBeDown && !released)
            {
                int originalError = Marshal.GetLastWin32Error();
                try { sendInputs([new() { Mouse = new() { Flags = 0x0004 } }]); }
                catch (Exception) { /* 松开边界再次失败时，保留首次输入错误或取消异常。 */ }
                Marshal.SetLastPInvokeError(originalError);
            }
        }
    }

    /// <summary>显式定位到原始物理像素，保留真实系统失败代码。</summary>
    private void SetExpectedCursor(AutomationNativePoint target)
    {
        if (setCursor(target.X, target.Y)) return;
        int error = Marshal.GetLastWin32Error();
        logger.LogWarning("FreePackMousePositionFailed ExpectedScreenX={ExpectedX} ExpectedScreenY={ExpectedY} Win32Error={Win32Error}", target.X, target.Y, error);
        throw new Win32Exception(error, "定位鼠标失败。");
    }

    /// <summary>为兼容的两参数调用把虚拟桌面归一化坐标还原为物理像素。</summary>
    private AutomationNativePoint ResolvePhysicalTarget(int normalizedX, int normalizedY)
    {
        AutomationNativeRectangle desktop = getDesktop();
        long width = (long)desktop.Right - desktop.Left;
        long height = (long)desktop.Bottom - desktop.Top;
        if (width <= 1 || height <= 1 || normalizedX < 0 || normalizedX > 65535 || normalizedY < 0 || normalizedY > 65535)
            throw new InvalidOperationException("鼠标目标超出有效虚拟桌面坐标。");
        return new(desktop.Left + (int)Math.Round(normalizedX * (width - 1) / 65535.0), desktop.Top + (int)Math.Round(normalizedY * (height - 1) / 65535.0));
    }

    /// <summary>在移动、就绪等待和按住期间响应本轮 F8 停止锁存。</summary>
    private void ThrowIfStopRequested()
    {
        if (stopRequested()) throw new OperationCanceledException("F8 已请求停止免费开包。");
    }

    /// <summary>与 GetCursorPos 兼容且可替换的坐标读取边界。</summary>
    internal delegate bool CursorPositionReader(out AutomationNativePoint point);

    /// <summary>运行时读取事件锁存，空闲时只检查 F8 当前按住状态。</summary>
    public bool IsF8Pressed => stopListener?.IsStopRequested ?? IsEmergencyStopState(NativeMethods.GetAsyncKeyState(0x77));
    /// <summary>只在当前运行中注册 F8，重复开始先释放上一轮监听。</summary>
    public void BeginEmergencyStop()
    {
        EndEmergencyStop();
        stopListener = new F8EmergencyStopListener();
    }
    /// <summary>解除 F8 注册，即使释放失败也不保留失效的监听引用。</summary>
    public void EndEmergencyStop()
    {
        F8EmergencyStopListener? listener = stopListener;
        stopListener = null;
        listener?.Dispose();
    }
    /// <summary>读取系统当前 UTC 时间。</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>空闲时只接受当前按下高位，避免上一轮遗留的短按低位误停新一轮。</summary>
    internal static bool IsEmergencyStopState(short state) => (state & 0x8000) != 0;

    /// <summary>把普通鼠标批次发送到系统输入队列。</summary>
    private static uint SendSystemInputs(NativeInput[] inputs)
        => NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());

    /// <summary>与 BITMAPINFOHEADER 原生布局一致的 32 位图像描述。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmapHeader
    {
        /// <summary>结构字节数。</summary>
        public uint Size;
        /// <summary>图像宽度。</summary>
        public int Width;
        /// <summary>负高度表示自顶向下排列。</summary>
        public int Height;
        /// <summary>颜色平面数量。</summary>
        public ushort Planes;
        /// <summary>每个像素位数。</summary>
        public ushort BitCount;
        /// <summary>未压缩格式编码。</summary>
        public uint Compression;
        /// <summary>像素总字节数。</summary>
        public uint ImageSize;
        /// <summary>水平分辨率。</summary>
        public int PixelsPerMeterX;
        /// <summary>垂直分辨率。</summary>
        public int PixelsPerMeterY;
        /// <summary>调色板使用数量。</summary>
        public uint ColorsUsed;
        /// <summary>重要颜色数量。</summary>
        public uint ColorsImportant;
    }

    /// <summary>供 GetDIBits 使用的原生位图描述缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmapInfo
    {
        /// <summary>位图信息头。</summary>
        public NativeBitmapHeader Header;
        /// <summary>原生调色板占位；32 位 BGRA 格式无需使用。</summary>
        public uint Color;
    }

    /// <summary>与 Win32 MOUSEINPUT 一致的普通鼠标事件。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouseInput
    {
        /// <summary>绝对横坐标。</summary>
        public int X;
        /// <summary>绝对纵坐标。</summary>
        public int Y;
        /// <summary>滚轮和扩展按键数据。</summary>
        public uint MouseData;
        /// <summary>普通鼠标输入标志。</summary>
        public uint Flags;
        /// <summary>零值采用系统时间。</summary>
        public uint Time;
        /// <summary>零值表示不附加应用数据。</summary>
        public nint ExtraInfo;
    }

    /// <summary>与 Win32 INPUT 在当前指针宽度下保持一致的鼠标批次元素。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        /// <summary>零值表示 INPUT_MOUSE。</summary>
        public uint Type;
        /// <summary>鼠标输入的原生联合体内容。</summary>
        public NativeMouseInput Mouse;
    }

    /// <summary>只声明系统 DLL 入口，不读取或修改游戏进程内存。</summary>
    private static class NativeMethods
    {
        /// <summary>查询窗口线程及所属进程。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        /// <summary>查询前台窗口。</summary>
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
        /// <summary>查询可见状态。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);
        /// <summary>查询最小化状态。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint window);
        /// <summary>还原窗口。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);
        /// <summary>请求前台焦点。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);
        /// <summary>查询客户区。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out AutomationNativeRectangle rectangle);
        /// <summary>转换客户区点。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint window, ref AutomationNativePoint point);
        /// <summary>取得客户区设备上下文。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetDC(nint window);
        /// <summary>创建内存设备上下文。</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateCompatibleDC(nint source);
        /// <summary>创建兼容彩色位图。</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint CreateCompatibleBitmap(nint source, int width, int height);
        /// <summary>选入或还原对象。</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern nint SelectObject(nint dc, nint bitmap);
        /// <summary>复制实际显示像素。</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(nint target, int targetX, int targetY, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        /// <summary>读取 BGRA 位图。</summary>
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int GetDIBits(nint dc, nint bitmap, uint firstLine, uint lines, [Out] byte[] pixels, ref NativeBitmapInfo info, uint usage);
        /// <summary>释放 GDI 对象。</summary>
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint bitmap);
        /// <summary>释放内存设备上下文。</summary>
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint dc);
        /// <summary>归还窗口设备上下文。</summary>
        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint window, nint dc);
        /// <summary>读取虚拟桌面范围。</summary>
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);
        /// <summary>发送普通鼠标输入。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, NativeInput[] inputs, int inputSize);
        /// <summary>以屏幕坐标定位系统鼠标，保留系统裁剪约束。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);
        /// <summary>读取实际系统鼠标屏幕坐标。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out AutomationNativePoint point);
        /// <summary>读取当前原生输入发送线程的 DPI 上下文。</summary>
        [DllImport("user32.dll")]
        internal static extern nint GetThreadDpiAwarenessContext();
        /// <summary>读取紧急停止键状态。</summary>
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}

/// <summary>独立消息线程通过标准系统热键锁存 F8，避免图像计算期间漏掉短按。</summary>
internal sealed class F8EmergencyStopListener : IDisposable
{
    /// <summary>运行期间专用的 F8 热键标识。</summary>
    internal const uint HotKeyId = 0x4D44;
    /// <summary>可注入的热键消息系统边界。</summary>
    private readonly IF8HotKeyNativeApi native;
    /// <summary>注册结果通知，不占用需要额外释放的本机等待句柄。</summary>
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>负责系统热键消息的独立线程。</summary>
    private readonly Thread thread;
    /// <summary>结束时允许线程退出的最长等待。</summary>
    private readonly TimeSpan shutdownTimeout;
    /// <summary>监听线程标识，供退出消息投递使用。</summary>
    private uint threadId;
    /// <summary>首次 F8 事件之后始终保持的停止状态。</summary>
    private int stopRequested;
    /// <summary>请求退出消息线程的状态。</summary>
    private int shutdownRequested;
    /// <summary>确保资源释放最多执行一次。</summary>
    private int disposed;
    /// <summary>热键注册失败时保留的系统错误码。</summary>
    private int startupError;

    /// <summary>启动标准 F8 热键监听，注册或退出等待均有明确上限。</summary>
    internal F8EmergencyStopListener(IF8HotKeyNativeApi? nativeApi = null, TimeSpan? startupTimeout = null, TimeSpan? shutdownTimeout = null,
        Func<Task<bool>, TimeSpan, bool>? waitForStartup = null)
    {
        native = nativeApi ?? new SystemF8HotKeyNativeApi();
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2);
        thread = new Thread(Observe) { IsBackground = true, Name = "Master Duel free-pack F8" };
        thread.Start();
        Func<Task<bool>, TimeSpan, bool> wait = waitForStartup ?? ((task, timeout) => task.Wait(timeout));
        if (!wait(ready.Task, startupTimeout ?? TimeSpan.FromSeconds(3)))
        {
            Interlocked.Exchange(ref shutdownRequested, 1);
            if (ready.Task.IsCompletedSuccessfully && ready.Task.Result)
            {
                native.PostQuit(ThreadId);
                thread.Join(this.shutdownTimeout);
            }
            throw new TimeoutException("F8 热键初始化超时。");
        }
        if (!ready.Task.Result)
        {
            thread.Join();
            throw new Win32Exception(startupError, "F8 热键已经被其他程序占用，自动开包已停止。");
        }
    }

    /// <summary>锁存已经收到的 F8 事件。</summary>
    internal bool IsStopRequested => Volatile.Read(ref stopRequested) != 0;
    /// <summary>供隔离消息测试投递本监听器专属事件的线程标识。</summary>
    internal uint ThreadId => Volatile.Read(ref threadId);

    /// <summary>读取标准热键消息，不拦截、模拟或注入游戏键盘输入。</summary>
    private void Observe()
    {
        threadId = native.CurrentThreadId;
        bool registered = native.Register();
        startupError = Marshal.GetLastWin32Error();
        ready.SetResult(registered);
        if (!registered) return;
        try
        {
            while (Volatile.Read(ref shutdownRequested) == 0)
            {
                int result = native.ReadMessage(out uint message, out nuint keyId);
                if (result <= 0)
                {
                    Interlocked.Exchange(ref stopRequested, 1);
                    break;
                }
                if (message == 0x0312 && keyId == HotKeyId)
                    Interlocked.Exchange(ref stopRequested, 1);
            }
        }
        finally { native.Unregister(); }
    }

    /// <summary>投递退出消息，等待监听线程释放 F8，并拒绝无界等待。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref shutdownRequested, 1);
        if (thread.IsAlive)
        {
            if (!native.PostQuit(ThreadId))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "F8 热键退出消息发送失败。");
            if (!thread.Join(shutdownTimeout))
                throw new TimeoutException("F8 热键监听线程退出超时。");
        }
    }
}

/// <summary>标准 F8 热键注册与消息队列的可注入边界。</summary>
internal interface IF8HotKeyNativeApi
{
    /// <summary>读取当前原生线程标识。</summary>
    uint CurrentThreadId { get; }
    /// <summary>在当前线程注册运行期间的 F8 热键。</summary>
    bool Register();
    /// <summary>解除当前线程持有的 F8 热键。</summary>
    void Unregister();
    /// <summary>读取下一条热键消息；零为退出，负数为系统错误。</summary>
    int ReadMessage(out uint message, out nuint keyId);
    /// <summary>向自有监听线程投递退出消息。</summary>
    bool PostQuit(uint threadId);
}

/// <summary>通过 RegisterHotKey 和自有消息队列接收 F8。</summary>
internal sealed class SystemF8HotKeyNativeApi : IF8HotKeyNativeApi
{
    /// <summary>读取当前线程的系统标识。</summary>
    public uint CurrentThreadId => NativeMethods.GetCurrentThreadId();
    /// <summary>注册 F8 并禁止长按重复触发。</summary>
    public bool Register() => NativeMethods.RegisterHotKey(0, (int)F8EmergencyStopListener.HotKeyId, 0x4000, 0x77);
    /// <summary>解除 F8 注册。</summary>
    public void Unregister() => NativeMethods.UnregisterHotKey(0, (int)F8EmergencyStopListener.HotKeyId);
    /// <summary>从当前线程队列读取下一条消息。</summary>
    public int ReadMessage(out uint message, out nuint keyId)
    {
        int result = NativeMethods.GetMessage(out NativeMessage value, 0, 0, 0);
        message = value.Message;
        keyId = value.WParam;
        return result;
    }
    /// <summary>只向本监听器的线程投递 WM_QUIT。</summary>
    public bool PostQuit(uint threadId) => NativeMethods.PostThreadMessage(threadId, 0x0012, 0, 0);

    /// <summary>与当前平台 MSG 保持一致的原生消息布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        /// <summary>消息所属窗口。</summary>
        public nint Window;
        /// <summary>消息编号。</summary>
        public uint Message;
        /// <summary>热键消息标识。</summary>
        public nuint WParam;
        /// <summary>消息附加参数。</summary>
        public nint LParam;
        /// <summary>消息发送时间。</summary>
        public uint Time;
        /// <summary>发送时的屏幕点。</summary>
        public AutomationNativePoint Point;
        /// <summary>系统保留数据。</summary>
        public uint Private;
    }

    /// <summary>热键监听所需的系统入口。</summary>
    private static class NativeMethods
    {
        /// <summary>读取当前系统线程标识。</summary>
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        /// <summary>注册线程热键。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        /// <summary>解除线程热键。</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint window, int id);
        /// <summary>读取线程消息。</summary>
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
        /// <summary>向自有线程投递退出消息。</summary>
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    }
}
