using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>在自有 Win32 窗口验证真实 GDI、物理客户区和鼠标事件。</summary>
[Collection("WindowsGameAutomationNative")]
public sealed class WindowsGameAutomationNativeTests
{
    /// <summary>验证系统仅部分接受输入时补发左键松开，并保留原错误。</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public void PartialMouseBatchAlwaysReleasesTheButtonAndPreservesOriginalError(uint accepted)
    {
        var batches = new List<uint[]>();
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            batches.Add(inputs.Select(input => input.Mouse.Flags).ToArray());
            Marshal.SetLastPInvokeError(batches.Count == 1 ? 5 : 0);
            return batches.Count == 1 ? accepted : 1;
        }, _ => { });
        uint result = api.SendMouseClick(100, 200);
        int originalError = Marshal.GetLastWin32Error();
        Assert.Equal(accepted == 2 ? 3u : accepted, result);
        Assert.Equal([0xC001u, 2u], batches[0]);
        Assert.Equal(2, batches.Count);
        if (accepted != 2)
        {
            Assert.Equal([4u], batches[1]);
            Assert.Equal(5, originalError);
        }
    }

    /// <summary>验证空闲阶段只识别当前按住的 F8，忽略上一轮遗留的短按位。</summary>
    [Theory]
    [InlineData((short)0, false)]
    [InlineData((short)1, false)]
    [InlineData(short.MinValue, true)]
    public void IdleF8IgnoresPreviousRunShortTapAndRecognizesCurrentPress(short state, bool expected)
        => Assert.Equal(expected, SystemWindowsGameAutomationNativeApi.IsEmergencyStopState(state));

    /// <summary>以批次到达时刻模拟每秒六十帧的鼠标轮询，验证按住状态至少跨过一帧。</summary>
    [Fact]
    public void MousePressRemainsObservableAcrossAFramePollingConsumer()
    {
        long downAt = 0;
        long releasedAt = 0;
        bool pressed = false;
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            long batchAt = Stopwatch.GetTimestamp();
            foreach (var input in inputs)
            {
                if (input.Mouse.Flags == 2) { pressed = true; downAt = batchAt; }
                if (input.Mouse.Flags == 4) { pressed = false; releasedAt = batchAt; }
            }
            return (uint)inputs.Length;
        });
        Assert.Equal(3u, api.SendMouseClick(100, 200));
        Assert.False(pressed);
        TimeSpan duration = Stopwatch.GetElapsedTime(downAt, releasedAt);
        int observableFrames = (int)Math.Floor(duration.TotalSeconds * 60);
        Assert.True(observableFrames >= 1, $"按住状态持续 {duration.TotalMilliseconds:F3}ms，60Hz 轮询只可观察 {observableFrames} 帧。");
        Assert.True(duration >= TimeSpan.FromMilliseconds(70), $"鼠标按住仅 {duration.TotalMilliseconds:F3}ms，未达到开包按钮的跨帧时序。");
    }

    /// <summary>验证边界接受按下后发生取消或异常时仍发送松开，并保留原异常。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MousePressAlwaysReleasesAfterAnAcceptedDownThrows(bool cancelled)
    {
        bool pressed = false;
        bool released = false;
        int calls = 0;
        Exception expected = cancelled ? new OperationCanceledException("F8 已请求停止。") : new Win32Exception(5);
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            calls++;
            foreach (var input in inputs)
            {
                if (input.Mouse.Flags == 2)
                {
                    pressed = true;
                    if (calls == 1) throw expected;
                }
                if (input.Mouse.Flags == 4) { pressed = false; released = true; }
            }
            return (uint)inputs.Length;
        });
        Assert.Same(expected, Record.Exception(() => api.SendMouseClick(100, 200)));
        Assert.True(released, "已接受左键按下后的异常路径没有补发松开。");
        Assert.False(pressed);
    }

    /// <summary>验证等待只在按下已接受之后发生，且使用固定八十毫秒再发送松开。</summary>
    [Fact]
    public void MouseHoldIsInjectedBetweenAcceptedDownAndUp()
    {
        bool pressed = false;
        bool held = false;
        var order = new List<string>();
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            if (inputs.Length == 2) { pressed = true; order.Add("down"); }
            else { Assert.True(held); pressed = false; order.Add("up"); }
            return (uint)inputs.Length;
        }, duration =>
        {
            Assert.True(pressed);
            Assert.Equal(TimeSpan.FromMilliseconds(80), duration);
            held = true;
            order.Add("hold");
        });
        Assert.Equal(3u, api.SendMouseClick(100, 200));
        Assert.Equal(["down", "hold", "up"], order);
        Assert.False(pressed);
    }

    /// <summary>验证等待阶段的取消仍补发松开，松开本身失败时仍保留原停止异常。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MouseHoldCancellationAttemptsReleaseAndKeepsOriginalStop(bool releaseFails)
    {
        bool pressed = false;
        int releaseAttempts = 0;
        var stop = new OperationCanceledException("F8 已请求停止。");
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            if (inputs.Length == 2) { pressed = true; Marshal.SetLastPInvokeError(7); return 2; }
            Assert.Equal(4u, Assert.Single(inputs).Mouse.Flags);
            releaseAttempts++;
            Marshal.SetLastPInvokeError(9);
            if (releaseFails) throw new Win32Exception(9);
            pressed = false;
            return 1;
        }, _ => throw stop);
        Assert.Same(stop, Record.Exception(() => api.SendMouseClick(100, 200)));
        Assert.Equal(1, releaseAttempts);
        Assert.Equal(releaseFails, pressed);
        Assert.Equal(7, Marshal.GetLastWin32Error());
    }

    /// <summary>验证首个松开批次部分失败时重试一次，并保持原始 Win32 错误。</summary>
    [Fact]
    public void MouseUpPartialFailureRetriesReleaseAndKeepsOriginalWin32Error()
    {
        bool pressed = false;
        int calls = 0;
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            calls++;
            if (calls == 1) { pressed = true; return 2; }
            if (calls == 2) { Marshal.SetLastPInvokeError(5); return 0; }
            pressed = false;
            Marshal.SetLastPInvokeError(0);
            return 1;
        }, _ => { });
        Assert.Equal(2u, api.SendMouseClick(100, 200));
        Assert.Equal(5, Marshal.GetLastWin32Error());
        Assert.Equal(3, calls);
        Assert.False(pressed);
    }

    /// <summary>验证首个松开批次抛出异常时重试释放，原松开异常继续向上传递。</summary>
    [Fact]
    public void MouseUpExceptionRetriesReleaseAndKeepsOriginalException()
    {
        bool pressed = false;
        int calls = 0;
        var original = new Win32Exception(5);
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            calls++;
            if (calls == 1) { pressed = true; return 2; }
            if (calls == 2) throw original;
            pressed = false;
            return 1;
        }, _ => { });
        Assert.Same(original, Record.Exception(() => api.SendMouseClick(100, 200)));
        Assert.Equal(3, calls);
        Assert.False(pressed);
    }

    /// <summary>验证真实边界只向测试自有窗口发送普通鼠标点击。</summary>
    [Fact]
    public async Task RealWindowCapturesBgraAndReceivesTheVerifiedMouseClick()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => VerifyOwnWindow(completion)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    /// <summary>验证后台调用线程激活另一输入队列的自有窗口后可取得前台和截图。</summary>
    [Fact]
    public async Task RealBackgroundActivationWaitsForOwnedWindowInputQueue()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => VerifyCrossQueueActivation(completion)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    /// <summary>让自有窗口 STA 持续处理消息，后台线程只激活并捕获指定测试窗口。</summary>
    private static void VerifyCrossQueueActivation(TaskCompletionSource completion)
    {
        try
        {
            VerifyCrossQueueActivation();
            completion.SetResult();
        }
        catch (Exception exception) { completion.SetException(exception); }
    }

    /// <summary>完整执行跨队列窗口验证并先释放资源，再向调用方报告测试结果。</summary>
    private static void VerifyCrossQueueActivation()
    {
        nint previousDpi = Native.SetThreadDpiAwarenessContext(-4);
        nint target = 0;
        nint previousForeground = 0;
        string className = "FreePackCrossQueue-" + Guid.NewGuid().ToString("N");
        bool initialClicked = false;
        Native.WindowProcedure procedure = (handle, message, wParam, lParam) =>
        {
            if (handle == previousForeground && message == 0x0201) Native.SetCapture(handle);
            if (handle == previousForeground && message == 0x0202)
            {
                initialClicked = true;
                Native.ReleaseCapture();
            }
            return Native.DefWindowProc(handle, message, wParam, lParam);
        };
        SystemWindowsGameAutomationNativeApi? runningApi = null;
        Task? activation = null;
        Task<uint>? initialClick = null;
        AutomationNativePoint? originalCursor = null;
        Exception? mainFailure = null;
        string stage = "创建自有窗口";
        using var shutdown = new CancellationTokenSource();
        try
        {
            var registration = new Native.WindowClass { Procedure = procedure, Instance = Native.GetModuleHandle(null), ClassName = className };
            Assert.NotEqual((ushort)0, Native.RegisterClass(ref registration));
            Assert.True(Native.SystemParametersInfo(0x0030, 0, out AutomationNativeRectangle workArea, 0));
            Assert.True(Native.GetCursorPos(out AutomationNativePoint cursor));
            originalCursor = cursor;
            target = Native.CreateWindowEx(0, className, "FreePack cross-queue target", 0x10CF0000,
                workArea.Left + 50, workArea.Top + 50, 420, 340, 0, 0, registration.Instance, 0);
            previousForeground = Native.CreateWindowEx(0, className, "FreePack cross-queue initial foreground", 0x10CF0000,
                cursor.X - 100, cursor.Y - 100, 420, 340, 0, 0, registration.Instance, 0);
            Assert.NotEqual((nint)0, target);
            Assert.NotEqual((nint)0, previousForeground);
            PumpOwnWindowMessages();
            using var process = Process.GetCurrentProcess();
            var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, process.ProcessName);
            runningApi = api;
            stage = "自有窗口物理点击激活";
            Assert.True(Native.SetWindowPos(previousForeground, -1, 0, 0, 0, 0, 0x0053));
            Assert.Equal((uint)Environment.ProcessId, api.GetWindowProcessId(previousForeground));
            Assert.True(api.GetClientRectangle(previousForeground, out AutomationNativeRectangle initialClient));
            var initialOrigin = new AutomationNativePoint(0, 0);
            Assert.True(api.ClientToScreen(previousForeground, ref initialOrigin));
            AutomationNativePoint initialPoint = cursor;
            Assert.InRange(initialPoint.X - initialOrigin.X, 0, initialClient.Right - 1);
            Assert.InRange(initialPoint.Y - initialOrigin.Y, 0, initialClient.Bottom - 1);
            Assert.Equal(previousForeground, Native.GetAncestor(Native.WindowFromPoint(initialPoint), 2));
            AutomationNativeRectangle desktop = api.GetVirtualDesktop();
            int normalizedX = (int)Math.Round((initialPoint.X - (double)desktop.Left) * 65535 / (desktop.Right - (double)desktop.Left - 1));
            int normalizedY = (int)Math.Round((initialPoint.Y - (double)desktop.Top) * 65535 / (desktop.Bottom - (double)desktop.Top - 1));
            initialClick = Task.Run(() =>
            {
                nint clickDpi = Native.SetThreadDpiAwarenessContext(-4);
                try
                {
                    Assert.Equal(previousForeground, Native.GetAncestor(Native.WindowFromPoint(initialPoint), 2));
                    SystemWindowsGameAutomationNativeApi.NativeInput[] move =
                        [new() { Mouse = new() { X = normalizedX, Y = normalizedY, Flags = 0xC001 } }];
                    Assert.Equal(1u, Native.SendInput(1, move, Marshal.SizeOf<SystemWindowsGameAutomationNativeApi.NativeInput>()));
                    Assert.True(SpinWait.SpinUntil(() => Native.GetCursorPos(out AutomationNativePoint actual)
                        && Math.Abs(actual.X - initialPoint.X) <= 1 && Math.Abs(actual.Y - initialPoint.Y) <= 1
                        && Native.GetAncestor(Native.WindowFromPoint(actual), 2) == previousForeground, TimeSpan.FromMilliseconds(250)),
                        "按下前实际鼠标没有落在自有测试窗口中。");
                    return api.SendMouseClick(normalizedX, normalizedY);
                }
                finally { Native.SetThreadDpiAwarenessContext(clickDpi); }
            }, shutdown.Token);
            Stopwatch clickWait = Stopwatch.StartNew();
            while ((!initialClick.IsCompleted || !initialClicked) && clickWait.Elapsed < TimeSpan.FromSeconds(2))
            {
                PumpOwnWindowMessages();
                Thread.Sleep(1);
            }
            Assert.True(initialClick.IsCompleted, "自有窗口初始激活点击未在限定时间内完成。");
            Assert.Equal(3u, initialClick.GetAwaiter().GetResult());
            Assert.True(initialClicked, "只有自有前台准备窗口可接收初始激活点击。");
            Assert.True(Native.SetWindowPos(previousForeground, -2, 0, 0, 0, 0, 0x0053));
            stage = "初始 ActivateWindow 与前台复验";
            bool initialActivated = api.ActivateWindow(previousForeground);
            int initialError = Marshal.GetLastWin32Error();
            nint actualForeground = api.GetForegroundWindow();
            string initialState = $"自有 fixture 初始激活：Activation={initialActivated}，目标HWND={previousForeground}，"
                + $"后续目标HWND={target}，实际前台HWND={actualForeground}，目标Visible={api.IsWindowVisible(previousForeground)}，"
                + $"目标Iconic={api.IsWindowMinimized(previousForeground)}，目标PID={api.GetWindowProcessId(previousForeground)}，"
                + $"实际前台PID={api.GetWindowProcessId(actualForeground)}，调用线程={Native.GetCurrentThreadId()}，Win32Error={initialError}。";
            Assert.True(initialActivated, initialState);
            Assert.True(previousForeground == actualForeground, initialState);
            uint ownerThread = Native.GetCurrentThreadId();
            stage = "后台跨输入队列激活与桌面截图";
            var platform = new WindowsGameAutomationPlatform(Path.GetTempPath(), new FixedWindowNativeApi(api, target, shutdown.Token));
            activation = Task.Run(() =>
            {
                nint workerDpi = Native.SetThreadDpiAwarenessContext(-4);
                try
                {
                    Assert.NotEqual(ownerThread, Native.GetCurrentThreadId());
                    platform.ActivateGame();
                    Assert.Equal(target, api.GetForegroundWindow());
                    GameFrame frame = platform.Capture();
                    Assert.Equal(target, frame.WindowHandle);
                    Assert.Equal(frame.Width * frame.Height * 4, frame.Pixels.Length);
                }
                finally
                {
                    try { platform.EndAutomation(); }
                    finally { Native.SetThreadDpiAwarenessContext(workerDpi); }
                }
            }, shutdown.Token);
            PumpUntilWorkerCompletes(activation, TimeSpan.FromSeconds(7));
            Assert.True(activation.IsCompleted, "自有窗口后台激活未在限定时间内返回。");
            activation.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            mainFailure = exception;
            nint foreground = runningApi?.GetForegroundWindow() ?? 0;
            Console.WriteLine($"OwnedFixturePrimary Stage={stage} InitialClicked={initialClicked} InitialClickStatus={initialClick?.Status} "
                + $"ActivationStatus={activation?.Status} PreviousHWND={previousForeground} TargetHWND={target} Foreground={foreground} "
                + $"ForegroundPID={runningApi?.GetWindowProcessId(foreground)} Error={exception}");
            throw;
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                if (initialClick is not null && !initialClick.IsCompleted)
                {
                    PumpUntilWorkerCompletes(initialClick, TimeSpan.FromSeconds(6));
                    Assert.True(initialClick.IsCompleted, "已请求停止的自有点击任务未在限定时间内收尾。");
                }
                if (activation is not null && !activation.IsCompleted)
                {
                    PumpUntilWorkerCompletes(activation, TimeSpan.FromSeconds(6));
                    Assert.True(activation.IsCompleted, "已请求停止的后台测试任务未在限定时间内收尾。");
                }
            }
            finally
            {
                try { runningApi?.EndEmergencyStop(); }
                finally
                {
                    try
                    {
                        if ((activation is null || activation.IsCompleted) && (initialClick is null || initialClick.IsCompleted))
                        {
                            if (previousForeground != 0)
                            {
                                Native.SetWindowPos(previousForeground, -2, 0, 0, 0, 0, 0x0053);
                                Native.DestroyWindow(previousForeground);
                            }
                            if (target != 0) Native.DestroyWindow(target);
                            Native.UnregisterClass(className, Native.GetModuleHandle(null));
                            if (originalCursor is { } savedCursor)
                            {
                                try { RestoreOwnedFixtureCursor(savedCursor, runningApi!); }
                                catch (Exception cleanupFailure)
                                {
                                    Console.WriteLine($"OwnedFixtureCursorCleanup Error={cleanupFailure}");
                                    if (mainFailure is null) throw;
                                    mainFailure.Data["CursorCleanupFailure"] = cleanupFailure.ToString();
                                }
                            }
                        }
                    }
                    finally
                    {
                        Native.SetThreadDpiAwarenessContext(previousDpi);
                        GC.KeepAlive(procedure);
                    }
                }
            }
        }
    }

    /// <summary>记录原生光标恢复结果；必要时仅发送绝对移动，并验证恢复位置，不发送按下或松开。</summary>
    private static void RestoreOwnedFixtureCursor(AutomationNativePoint cursor, SystemWindowsGameAutomationNativeApi api)
    {
        bool direct = Native.SetCursorPos(cursor.X, cursor.Y);
        int directError = Marshal.GetLastWin32Error();
        Console.WriteLine($"OwnedFixtureCursorRestore SetCursorPos={direct} Win32Error={directError} X={cursor.X} Y={cursor.Y}");
        if (!direct)
        {
            AutomationNativeRectangle desktop = api.GetVirtualDesktop();
            int x = (int)Math.Round((cursor.X - (double)desktop.Left) * 65535 / (desktop.Right - (double)desktop.Left - 1));
            int y = (int)Math.Round((cursor.Y - (double)desktop.Top) * 65535 / (desktop.Bottom - (double)desktop.Top - 1));
            Assert.InRange(x, 0, 65535);
            Assert.InRange(y, 0, 65535);
            SystemWindowsGameAutomationNativeApi.NativeInput[] move =
                [new() { Mouse = new() { X = x, Y = y, Flags = 0xC001 } }];
            Assert.Equal(1u, Native.SendInput(1, move, Marshal.SizeOf<SystemWindowsGameAutomationNativeApi.NativeInput>()));
        }
        Assert.True(SpinWait.SpinUntil(() => Native.GetCursorPos(out AutomationNativePoint current)
            && Math.Abs(current.X - cursor.X) <= 1 && Math.Abs(current.Y - cursor.Y) <= 1, TimeSpan.FromMilliseconds(250)),
            "自有窗口测试收尾后的鼠标坐标没有恢复。");
    }

    /// <summary>分派自有窗口线程当前已排队的消息，不延时、不重试前台请求。</summary>
    private static void PumpOwnWindowMessages()
    {
        while (Native.PeekMessage(out Native.WindowMessage message, 0, 0, 0, 1))
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessage(ref message);
        }
    }

    /// <summary>在有界等待后台测试收尾期间持续处理自有窗口消息。</summary>
    private static void PumpUntilWorkerCompletes(Task worker, TimeSpan timeout)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!worker.IsCompleted && wait.Elapsed < timeout)
        {
            PumpOwnWindowMessages();
            Thread.Sleep(1);
        }
    }

    /// <summary>选择自有客户区与虚拟桌面边界交集的中心像素，避免窗口出屏部分产生黑色像素。</summary>
    private static int GetVisibleOwnWindowPixel(GameFrame frame, AutomationNativeRectangle desktop)
    {
        long left = Math.Max((long)frame.ScreenX, desktop.Left);
        long top = Math.Max((long)frame.ScreenY, desktop.Top);
        long right = Math.Min((long)frame.ScreenX + frame.Width, desktop.Right);
        long bottom = Math.Min((long)frame.ScreenY + frame.Height, desktop.Bottom);
        Assert.True(left < right && top < bottom, "自有测试窗口客户区没有落在虚拟桌面边界内。");
        int x = (int)(left + (right - left) / 2 - frame.ScreenX);
        int y = (int)(top + (bottom - top) / 2 - frame.ScreenY);
        return (frame.Width * y + x) * 4;
    }

    /// <summary>在单独线程创建、操作并销毁本测试专属窗口。</summary>
    private static void VerifyOwnWindow(TaskCompletionSource completion)
    {
        nint previousDpi = Native.SetThreadDpiAwarenessContext(-4);
        nint window = 0;
        string className = "FreePackFixture-" + Guid.NewGuid().ToString("N");
        bool clicked = false;
        Native.WindowProcedure procedure = (handle, message, wParam, lParam) =>
        {
            if (message == 0x0202) clicked = true;
            return Native.DefWindowProc(handle, message, wParam, lParam);
        };
        string directory = Path.Combine(Path.GetTempPath(), className);
        SystemWindowsGameAutomationNativeApi? runningApi = null;
        try
        {
            var registration = new Native.WindowClass { Procedure = procedure, Instance = Native.GetModuleHandle(null), ClassName = className };
            Assert.NotEqual((ushort)0, Native.RegisterClass(ref registration));
            var raw = new SystemWindowsGameAutomationNativeApi();
            _ = raw.FindGameWindow();
            Assert.False(raw.IsGameWindow(0));
            Assert.Equal(0u, raw.GetWindowProcessId(0));
            var empty = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture");
            Assert.Equal((nint)0, empty.FindGameWindow());
            var desktop = raw.GetVirtualDesktop();
            Assert.True(Native.SystemParametersInfo(0x0030, 0, out AutomationNativeRectangle workArea, 0));
            Assert.True(Native.GetCursorPos(out AutomationNativePoint originalCursor));
            window = Native.CreateWindowEx(0, className, "FreePack native fixture", 0x10CF0000,
                originalCursor.X - 100, originalCursor.Y - 100, 420, 340, 0, 0, registration.Instance, 0);
            Assert.NotEqual((nint)0, window);
            using var currentProcess = Process.GetCurrentProcess();
            var api = new SystemWindowsGameAutomationNativeApi(
                () => [Process.GetCurrentProcess(), Process.GetCurrentProcess()], Process.GetProcessById, currentProcess.ProcessName);
            runningApi = api;
            Assert.Equal(window, api.FindGameWindow());
            Assert.True(api.IsGameWindow(window));
            Assert.False(raw.IsGameWindow(window));
            Assert.Equal((uint)Environment.ProcessId, api.GetWindowProcessId(window));
            Assert.True(api.IsWindowVisible(window));
            Assert.False(api.IsWindowMinimized(window));
            Native.ShowWindow(window, 6);
            Assert.True(api.IsWindowMinimized(window));
            Assert.True(api.ActivateWindow(window));
            Assert.False(api.IsWindowMinimized(window));
            Assert.Equal(window, api.GetForegroundWindow());
            Assert.True(api.GetClientRectangle(window, out AutomationNativeRectangle client));
            var origin = new AutomationNativePoint(0, 0);
            Assert.True(api.ClientToScreen(window, ref origin));
            Assert.InRange(api.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow.AddSeconds(2));
            _ = api.IsF8Pressed;
            using (var listener = new F8EmergencyStopListener())
            {
                Assert.False(listener.IsStopRequested);
                Assert.True(Native.PostThreadMessage(listener.ThreadId, 0x0312, F8EmergencyStopListener.HotKeyId, 0));
                Assert.True(SpinWait.SpinUntil(() => listener.IsStopRequested, TimeSpan.FromSeconds(1)));
            }
            var ownedApi = new FixedWindowNativeApi(api, window, CancellationToken.None);
            var platform = new WindowsGameAutomationPlatform(directory, ownedApi);
            platform.ActivateGame();
            nint dc = Native.GetDC(window);
            nint brush = Native.CreateSolidBrush(0x00332211);
            Assert.NotEqual((nint)0, dc);
            Assert.NotEqual((nint)0, brush);
            Assert.NotEqual(0, Native.FillRect(dc, ref client, brush));
            Native.DeleteObject(brush);
            Assert.Equal(1, Native.ReleaseDC(window, dc));
            Assert.Equal(0, Native.DwmFlush());
            var frame = platform.Capture();
            Assert.Equal(client.Right - client.Left, frame.Width);
            Assert.Equal(client.Bottom - client.Top, frame.Height);
            Assert.Equal(origin.X, frame.ScreenX);
            Assert.Equal(origin.Y, frame.ScreenY);
            Assert.Equal(frame.Width * frame.Height * 4, frame.Pixels.Length);
            int center = GetVisibleOwnWindowPixel(frame, desktop);
            var centerScreen = new AutomationNativePoint(frame.ScreenX + center / 4 % frame.Width, frame.ScreenY + center / 4 / frame.Width);
            Console.WriteLine($"OwnedFirstPixel OriginalCursor=({originalCursor.X},{originalCursor.Y}) "
                + $"Frame=({frame.ScreenX},{frame.ScreenY},{frame.Width},{frame.Height}) CenterScreen=({centerScreen.X},{centerScreen.Y}) "
                + $"CenterRoot={Native.GetAncestor(Native.WindowFromPoint(centerScreen), 2)} Own={window} Foreground={api.GetForegroundWindow()} "
                + $"VirtualDesktop=({desktop.Left},{desktop.Top},{desktop.Right},{desktop.Bottom}).");
            Assert.Equal([0x33, 0x22, 0x11], frame.Pixels.Skip(center).Take(3).Select(value => (int)value).ToArray());
            nint refreshedDc = Native.GetDC(window);
            nint refreshedBrush = Native.CreateSolidBrush(0x00665544);
            Assert.NotEqual((nint)0, refreshedDc);
            Assert.NotEqual((nint)0, refreshedBrush);
            Assert.NotEqual(0, Native.FillRect(refreshedDc, ref client, refreshedBrush));
            Assert.True(Native.DeleteObject(refreshedBrush));
            Assert.Equal(1, Native.ReleaseDC(window, refreshedDc));
            Assert.Equal(0, Native.DwmFlush());
            GameFrame refreshed = platform.Capture();
            Assert.Equal([0x66, 0x55, 0x44], refreshed.Pixels.Skip(center).Take(3).Select(value => (int)value).ToArray());
            Assert.False(frame.Pixels.AsSpan().SequenceEqual(refreshed.Pixels), "自有窗口重绘后截图仍为旧帧。");
            GameFrame inputFrame = refreshed;
            AutomationNativePoint cursorBeforeInput = default;
            PixelPoint target = default;
            nint rootBeforeInput = 0;
            bool inputReady = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Assert.True(Native.GetCursorPos(out cursorBeforeInput));
                target = new PixelPoint(cursorBeforeInput.X - inputFrame.ScreenX, cursorBeforeInput.Y - inputFrame.ScreenY);
                rootBeforeInput = Native.GetAncestor(Native.WindowFromPoint(cursorBeforeInput), 2);
                Console.WriteLine($"OwnedClickPoint Attempt={attempt} Original=({originalCursor.X},{originalCursor.Y}) "
                    + $"Current=({cursorBeforeInput.X},{cursorBeforeInput.Y}) Client=({target.X},{target.Y}) Root={rootBeforeInput} Own={window}.");
                inputReady = target.X >= 0 && target.X < inputFrame.Width && target.Y >= 0 && target.Y < inputFrame.Height
                    && rootBeforeInput == window;
                if (inputReady || attempt == 2) break;
                platform.EndAutomation();
                Assert.True(Native.SetWindowPos(window, 0, cursorBeforeInput.X - 100, cursorBeforeInput.Y - 100, 0, 0, 0x0015));
                PumpOwnWindowMessages();
                platform.ActivateGame();
                Assert.True(api.GetClientRectangle(window, out client));
                nint movedDc = Native.GetDC(window);
                nint movedBrush = Native.CreateSolidBrush(0x00665544);
                Assert.NotEqual((nint)0, movedDc);
                Assert.NotEqual((nint)0, movedBrush);
                try { Assert.NotEqual(0, Native.FillRect(movedDc, ref client, movedBrush)); }
                finally
                {
                    Assert.True(Native.DeleteObject(movedBrush));
                    Assert.Equal(1, Native.ReleaseDC(window, movedDc));
                }
                Assert.Equal(0, Native.DwmFlush());
                inputFrame = platform.Capture();
                int movedCenter = GetVisibleOwnWindowPixel(inputFrame, desktop);
                Assert.Equal([0x66, 0x55, 0x44], inputFrame.Pixels.Skip(movedCenter).Take(3).Select(value => (int)value).ToArray());
            }
            Assert.True(inputReady, $"两次重新定位后鼠标仍未命中自有客户区。窗口={window}，"
                + $"初始鼠标=({originalCursor.X},{originalCursor.Y})，当前鼠标=({cursorBeforeInput.X},{cursorBeforeInput.Y})，命中根窗口={rootBeforeInput}。");
            Assert.InRange(target.X, 0, inputFrame.Width - 1);
            Assert.InRange(target.Y, 0, inputFrame.Height - 1);
            Assert.Equal(window, rootBeforeInput);
            clicked = false;
            nint foregroundBeforeInput = api.GetForegroundWindow();
            platform.Click(inputFrame, target);
            Stopwatch wait = Stopwatch.StartNew();
            while (!clicked && wait.Elapsed < TimeSpan.FromSeconds(2))
            {
                while (Native.PeekMessage(out Native.WindowMessage message, window, 0, 0, 1))
                {
                    Native.TranslateMessage(ref message);
                    Native.DispatchMessage(ref message);
                }
                Thread.Sleep(5);
            }
            Assert.True(Native.GetCursorPos(out AutomationNativePoint cursor));
            nint foregroundAfterInput = api.GetForegroundWindow();
            nint pointRoot = Native.GetAncestor(Native.WindowFromPoint(cursor), 2);
            Assert.True(clicked, $"自有测试窗口未收到鼠标松开。窗口={window}，输入前前台={foregroundBeforeInput}，输入后前台={foregroundAfterInput}，"
                + $"预期屏幕点=({inputFrame.ScreenX + target.X},{inputFrame.ScreenY + target.Y})，当前鼠标=({cursor.X},{cursor.Y})，鼠标命中根窗口={pointRoot}。");
            uint originalPid = api.GetWindowProcessId(window);
            int beginsBeforeRecovery = ownedApi.BeginCount;
            int endsBeforeRecovery = ownedApi.EndCount;
            Assert.True(beginsBeforeRecovery > 0);
            Native.ShowWindow(window, 6);
            Assert.True(api.IsWindowMinimized(window));
            Assert.Throws<GameWindowTemporarilyUnavailableException>(() => platform.Capture());
            Assert.True(SpinWait.SpinUntil(platform.TryRecoverGame, TimeSpan.FromSeconds(2)), "自有最小化窗口没有恢复前台。");
            GameFrame recovered = platform.Capture();
            Assert.Equal(originalPid, api.GetWindowProcessId(window));
            Assert.Equal(inputFrame.WindowHandle, recovered.WindowHandle);
            Assert.Equal((inputFrame.Width, inputFrame.Height, inputFrame.ScreenX, inputFrame.ScreenY),
                (recovered.Width, recovered.Height, recovered.ScreenX, recovered.ScreenY));
            Assert.Equal(beginsBeforeRecovery, ownedApi.BeginCount);
            Assert.Equal(endsBeforeRecovery, ownedApi.EndCount);
            Assert.False(new SystemWindowsGameAutomationNativeApi(() => [], _ => throw new ArgumentException(), "fixture").IsGameWindow(window));
            Assert.False(new SystemWindowsGameAutomationNativeApi(() => [], _ => throw new InvalidOperationException(), "fixture").IsGameWindow(window));
            Assert.False(new SystemWindowsGameAutomationNativeApi(() => [], _ => throw new Win32Exception(), "fixture").IsGameWindow(window));
            platform.SaveDiagnostic(frame, "隔离窗口验证");
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "free-pack-diagnostics"), "*.png"));
            platform.EndAutomation();
            api.EndEmergencyStop();
            completion.SetResult();
        }
        catch (Exception exception) { completion.SetException(exception); }
        finally
        {
            runningApi?.EndEmergencyStop();
            if (window != 0) Native.DestroyWindow(window);
            Native.UnregisterClass(className, Native.GetModuleHandle(null));
            Native.SetThreadDpiAwarenessContext(previousDpi);
            GC.KeepAlive(procedure);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>固定寻找本测试拥有的目标窗口，其余操作交给真实原生边界。</summary>
    private sealed class FixedWindowNativeApi(IWindowsGameAutomationNativeApi inner, nint window, CancellationToken shutdown) : IWindowsGameAutomationNativeApi
    {
        /// <summary>本测试实际注册紧急停止监听的次数。</summary>
        public int BeginCount { get; private set; }
        /// <summary>本测试实际释放紧急停止监听的次数。</summary>
        public int EndCount { get; private set; }
        /// <summary>返回本测试指定的窗口。</summary>
        public nint FindGameWindow() => window;
        /// <summary>复验目标窗口进程。</summary>
        public bool IsGameWindow(nint handle) => inner.IsGameWindow(handle);
        /// <summary>读取目标窗口进程标识。</summary>
        public uint GetWindowProcessId(nint handle) => inner.GetWindowProcessId(handle);
        /// <summary>读取真实前台窗口。</summary>
        public nint GetForegroundWindow() => inner.GetForegroundWindow();
        /// <summary>读取真实可见状态。</summary>
        public bool IsWindowVisible(nint handle) => inner.IsWindowVisible(handle);
        /// <summary>读取真实最小化状态。</summary>
        public bool IsWindowMinimized(nint handle) => inner.IsWindowMinimized(handle);
        /// <summary>通过真实 Windows API 激活指定窗口。</summary>
        public bool ActivateWindow(nint handle) => inner.ActivateWindow(handle);
        /// <summary>读取真实客户区矩形。</summary>
        public bool GetClientRectangle(nint handle, out AutomationNativeRectangle rectangle) => inner.GetClientRectangle(handle, out rectangle);
        /// <summary>转换真实客户区物理坐标。</summary>
        public bool ClientToScreen(nint handle, ref AutomationNativePoint point) => inner.ClientToScreen(handle, ref point);
        /// <summary>取得真实客户区绘图设备上下文。</summary>
        public nint GetClientDc(nint handle) => inner.GetClientDc(handle);
        /// <summary>创建真实内存设备上下文。</summary>
        public nint CreateMemoryDc(nint source) => inner.CreateMemoryDc(source);
        /// <summary>创建真实兼容位图。</summary>
        public nint CreateBitmap(nint source, int width, int height) => inner.CreateBitmap(source, width, height);
        /// <summary>将位图选入真实设备上下文。</summary>
        public nint SelectBitmap(nint dc, nint bitmap) => inner.SelectBitmap(dc, bitmap);
        /// <summary>复制真实客户区像素。</summary>
        public bool CopyPixels(nint target, nint source, int width, int height, int sourceX, int sourceY) => inner.CopyPixels(target, source, width, height, sourceX, sourceY);
        /// <summary>读取真实位图像素。</summary>
        public int ReadPixels(nint dc, nint bitmap, int width, int height, byte[] pixels) => inner.ReadPixels(dc, bitmap, width, height, pixels);
        /// <summary>释放真实位图。</summary>
        public void DeleteBitmap(nint bitmap) => inner.DeleteBitmap(bitmap);
        /// <summary>释放真实内存设备上下文。</summary>
        public void DeleteMemoryDc(nint dc) => inner.DeleteMemoryDc(dc);
        /// <summary>释放真实客户区设备上下文。</summary>
        public void ReleaseClientDc(nint handle, nint dc) => inner.ReleaseClientDc(handle, dc);
        /// <summary>读取真实虚拟桌面。</summary>
        public AutomationNativeRectangle GetVirtualDesktop() => inner.GetVirtualDesktop();
        /// <summary>将自有窗口的普通鼠标输入交给真实原生边界。</summary>
        public uint SendMouseClick(int normalizedX, int normalizedY) => inner.SendMouseClick(normalizedX, normalizedY);
        /// <summary>读取真实紧急停止状态。</summary>
        public bool IsF8Pressed => shutdown.IsCancellationRequested || inner.IsF8Pressed;
        /// <summary>注册本测试运行期间的 F8 停止热键。</summary>
        public void BeginEmergencyStop() { BeginCount++; inner.BeginEmergencyStop(); }
        /// <summary>释放本测试运行期间的 F8 停止热键。</summary>
        public void EndEmergencyStop() { EndCount++; inner.EndEmergencyStop(); }
        /// <summary>读取真实 UTC 时间。</summary>
        public DateTimeOffset UtcNow => inner.UtcNow;
    }

    /// <summary>仅供测试创建隔离窗口和验证绘制消息的原生入口。</summary>
    private static class Native
    {
        /// <summary>测试窗口消息处理入口。</summary>
        internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
        /// <summary>注册测试窗口类的原生布局。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            /// <summary>窗口类样式。</summary>
            internal uint Style;
            /// <summary>消息处理入口。</summary>
            internal WindowProcedure Procedure;
            /// <summary>额外类数据大小。</summary>
            internal int ClassExtra;
            /// <summary>额外窗口数据大小。</summary>
            internal int WindowExtra;
            /// <summary>模块实例。</summary>
            internal nint Instance;
            /// <summary>图标句柄。</summary>
            internal nint Icon;
            /// <summary>光标句柄。</summary>
            internal nint Cursor;
            /// <summary>背景画刷。</summary>
            internal nint Background;
            /// <summary>菜单资源名称。</summary>
            internal string? MenuName;
            /// <summary>类名称。</summary>
            internal string ClassName;
        }
        /// <summary>Win32 消息缓冲。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowMessage
        {
            /// <summary>接收窗口。</summary>
            internal nint Window;
            /// <summary>消息编号。</summary>
            internal uint Message;
            /// <summary>第一个消息参数。</summary>
            internal nuint WParam;
            /// <summary>第二个消息参数。</summary>
            internal nint LParam;
            /// <summary>消息时间。</summary>
            internal uint Time;
            /// <summary>发送时的光标位置。</summary>
            internal AutomationNativePoint Point;
            /// <summary>系统私有字段。</summary>
            internal uint Private;
        }
        /// <summary>设置测试线程 DPI 上下文。</summary>
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        /// <summary>确认激活调用线程与自有窗口线程拥有不同输入队列。</summary>
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        /// <summary>查询模块实例。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        /// <summary>注册测试窗口类。</summary>
        [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClass(ref WindowClass windowClass);
        /// <summary>创建测试窗口。</summary>
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        /// <summary>使用系统默认消息处理。</summary>
        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
        /// <summary>最小化测试窗口。</summary>
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
        /// <summary>只调整本测试自有窗口的置顶状态，不请求前台激活。</summary>
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        /// <summary>在自有窗口测试收尾后恢复原鼠标位置，不发送任何点击。</summary>
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        /// <summary>只为恢复原鼠标坐标发送绝对移动；调用点没有鼠标按下或松开标志。</summary>
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, SystemWindowsGameAutomationNativeApi.NativeInput[] inputs, int size);
        /// <summary>只在自有窗口收到按下时锁定本测试的鼠标事件接收目标。</summary>
        [DllImport("user32.dll")] internal static extern nint SetCapture(nint window);
        /// <summary>在自有窗口收到松开时释放本测试的鼠标事件捕获。</summary>
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseCapture();
        /// <summary>读取实际主显示器工作区，避免虚拟桌面边界内的多屏空隙。</summary>
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SystemParametersInfo(uint action, uint parameter, out AutomationNativeRectangle rectangle, uint flags);
        /// <summary>仅向本测试的自有热键线程投递消息，不模拟真实键盘输入。</summary>
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
        /// <summary>只读获取测试结束时的真实鼠标坐标。</summary>
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out AutomationNativePoint point);
        /// <summary>只读获取鼠标实际命中的窗口。</summary>
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(AutomationNativePoint point);
        /// <summary>只读获取鼠标命中窗口的顶级父窗口。</summary>
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        /// <summary>绘制测试客户区。</summary>
        [DllImport("user32.dll")] internal static extern int FillRect(nint dc, ref AutomationNativeRectangle rectangle, nint brush);
        /// <summary>仅为自有测试窗口取得绘图上下文，生产截图另取桌面上下文。</summary>
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        /// <summary>释放自有测试窗口的绘图上下文。</summary>
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        /// <summary>等待自有窗口重绘进入桌面合成帧后再验证截图。</summary>
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        /// <summary>创建测试颜色画刷。</summary>
        [DllImport("gdi32.dll")] internal static extern nint CreateSolidBrush(uint color);
        /// <summary>删除测试画刷。</summary>
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        /// <summary>读取测试窗口的排队消息。</summary>
        [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out WindowMessage message, nint window, uint minimum, uint maximum, uint remove);
        /// <summary>转换测试窗口消息。</summary>
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref WindowMessage message);
        /// <summary>分派测试窗口消息。</summary>
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern nint DispatchMessage(ref WindowMessage message);
        /// <summary>销毁测试窗口。</summary>
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
        /// <summary>注销测试窗口类。</summary>
        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string className, nint instance);
    }
}
