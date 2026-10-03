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
    [InlineData(2u)]
    [InlineData(3u)]
    public void PartialMouseBatchAlwaysReleasesTheButtonAndPreservesOriginalError(uint accepted)
    {
        var batches = new List<uint[]>();
        var api = new SystemWindowsGameAutomationNativeApi(() => [], Process.GetProcessById, "fixture", inputs =>
        {
            batches.Add(inputs.Select(input => input.Mouse.Flags).ToArray());
            Marshal.SetLastPInvokeError(batches.Count == 1 ? 5 : 0);
            return batches.Count == 1 ? accepted : 1;
        });
        uint result = api.SendMouseClick(100, 200);
        int originalError = Marshal.GetLastWin32Error();
        Assert.Equal(accepted, result);
        Assert.Equal([0xC001u, 2u, 4u], batches[0]);
        Assert.Equal(accepted == 3 ? 1 : 2, batches.Count);
        if (accepted != 3)
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
            window = Native.CreateWindowEx(0, className, "FreePack native fixture", 0x10CF0000,
                workArea.Left + 50, workArea.Top + 50, 420, 340, 0, 0, registration.Instance, 0);
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
            var platform = new WindowsGameAutomationPlatform(directory, api);
            platform.ActivateGame();
            nint dc = api.GetClientDc(window);
            nint brush = Native.CreateSolidBrush(0x00332211);
            Assert.NotEqual((nint)0, dc);
            Assert.NotEqual((nint)0, brush);
            Assert.NotEqual(0, Native.FillRect(dc, ref client, brush));
            Native.DeleteObject(brush);
            api.ReleaseClientDc(window, dc);
            var frame = platform.Capture();
            Assert.Equal(client.Right - client.Left, frame.Width);
            Assert.Equal(client.Bottom - client.Top, frame.Height);
            Assert.Equal(origin.X, frame.ScreenX);
            Assert.Equal(origin.Y, frame.ScreenY);
            Assert.Equal(frame.Width * frame.Height * 4, frame.Pixels.Length);
            int center = (frame.Width * (frame.Height / 2) + frame.Width / 2) * 4;
            Assert.Equal([0x33, 0x22, 0x11], frame.Pixels.Skip(center).Take(3).Select(value => (int)value).ToArray());
            var target = new PixelPoint(frame.Width / 2, frame.Height / 2);
            nint foregroundBeforeInput = api.GetForegroundWindow();
            platform.Click(frame, target);
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
                + $"预期屏幕点=({frame.ScreenX + target.X},{frame.ScreenY + target.Y})，当前鼠标=({cursor.X},{cursor.Y})，鼠标命中根窗口={pointRoot}。");
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
