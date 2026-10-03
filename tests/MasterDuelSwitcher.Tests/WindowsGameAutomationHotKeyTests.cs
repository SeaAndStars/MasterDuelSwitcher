using MasterDuelSwitcher.Core.Services;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证 F8 自有消息线程的事件锁存、初始化和有界资源释放。</summary>
[Collection("WindowsGameAutomationNative")]
public sealed class WindowsGameAutomationHotKeyTests
{
    /// <summary>验证真实 F8 队列只接收自有线程消息，不激活窗口或模拟游戏输入。</summary>
    [Fact]
    public void RealF8HotKeyQueueLatchesItsOwnMessageWithoutActivatingAnyWindow()
    {
        using var listener = new F8EmergencyStopListener();
        Assert.NotEqual(0u, listener.ThreadId);
        Assert.False(listener.IsStopRequested);
        Assert.True(NativeHotKey.PostThreadMessage(listener.ThreadId, 0x0312, F8EmergencyStopListener.HotKeyId, 0));
        Assert.True(SpinWait.SpinUntil(() => listener.IsStopRequested, TimeSpan.FromSeconds(1)));
        Assert.True(listener.IsStopRequested);
    }

    /// <summary>验证热键只接受正确消息和标识，并在一次短按后保持停止状态。</summary>
    [Fact]
    public void CorrectHotKeyIsLatchedAndListenerReleasesOnce()
    {
        using var native = new FixtureHotKeyApi();
        using var listener = new F8EmergencyStopListener(native);
        Assert.Equal(7u, listener.ThreadId);
        Assert.False(listener.IsStopRequested);
        native.Messages.Add((1, 0x0400, F8EmergencyStopListener.HotKeyId));
        native.Messages.Add((1, 0x0312, 1));
        Assert.True(SpinWait.SpinUntil(() => native.ReadCount >= 3, TimeSpan.FromSeconds(1)));
        Assert.False(listener.IsStopRequested);
        native.Messages.Add((1, 0x0312, F8EmergencyStopListener.HotKeyId));
        Assert.True(SpinWait.SpinUntil(() => listener.IsStopRequested, TimeSpan.FromSeconds(1)));
        listener.Dispose();
        listener.Dispose();
        Assert.Equal(1, native.UnregisterCount);
        Assert.Equal(1, native.PostCount);
    }

    /// <summary>验证系统队列退出或读取错误均锁存停止，已经结束的线程可再次清理。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QueueTerminationStopsAndReleasesRegistration(int result)
    {
        using var native = new FixtureHotKeyApi();
        using var listener = new F8EmergencyStopListener(native);
        native.Messages.Add((result, 0, 0));
        Assert.True(native.Unregistered.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(listener.IsStopRequested);
        Thread.Sleep(40);
        listener.Dispose();
        Assert.Equal(0, native.PostCount);
    }

    /// <summary>验证热键被占用时初始化失败，未注册的资源不尝试解除。</summary>
    [Fact]
    public void RegistrationFailureKeepsTheSystemErrorAndDoesNotUnregister()
    {
        using var native = new FixtureHotKeyApi { RegisterSuccess = false };
        var error = Assert.Throws<Win32Exception>(() => new F8EmergencyStopListener(native));
        Assert.Equal(1409, error.NativeErrorCode);
        Assert.Equal(0, native.UnregisterCount);
    }

    /// <summary>验证初始化超时之后晚到的注册也会自行解除。</summary>
    [Fact]
    public void TimedOutStartupReleasesLateRegistration()
    {
        using var native = new FixtureHotKeyApi { BlockRegistration = true };
        Assert.Throws<TimeoutException>(() => new F8EmergencyStopListener(native, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40)));
        native.RegistrationGate.Set();
        Assert.True(native.Unregistered.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, native.UnregisterCount);
        Assert.Equal(0, native.ReadCount);
    }

    /// <summary>验证注册完成与初始化超时交界时，已经开始读消息的线程也会被唤醒释放。</summary>
    [Fact]
    public void StartupTimeoutAfterQueueReadStartedWakesAndReleasesTheThread()
    {
        using var native = new FixtureHotKeyApi();
        try
        {
            Assert.Throws<TimeoutException>(() => new F8EmergencyStopListener(native, waitForStartup: (task, timeout) =>
            {
                Assert.True(task.Wait(timeout));
                Assert.True(SpinWait.SpinUntil(() => native.ReadCount == 1, TimeSpan.FromSeconds(1)));
                return false;
            }));
            Assert.True(native.Unregistered.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.Equal(1, native.PostCount);
        }
        finally
        {
            if (!native.Unregistered.IsSet) native.Messages.Add((0, 0, 0));
            Assert.True(native.Unregistered.Wait(TimeSpan.FromSeconds(1)));
        }
    }

    /// <summary>验证超时边界观察到注册失败时不向未持有热键的线程发送退出。</summary>
    [Fact]
    public void StartupTimeoutAfterRegistrationFailureDoesNotPostQuit()
    {
        using var native = new FixtureHotKeyApi { RegisterSuccess = false };
        Assert.Throws<TimeoutException>(() => new F8EmergencyStopListener(native, waitForStartup: (task, timeout) =>
        {
            Assert.True(task.Wait(timeout));
            return false;
        }));
        Assert.Equal(0, native.PostCount);
        Assert.Equal(0, native.UnregisterCount);
    }

    /// <summary>验证退出消息失败和线程退出超时均报告明确错误，随后测试主动解除资源。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShutdownFailuresRemainBoundedAndTheRegistrationIsEventuallyReleased(bool timeout)
    {
        using var native = new FixtureHotKeyApi { PostSuccess = timeout, IgnorePost = timeout };
        var listener = new F8EmergencyStopListener(native, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40));
        Assert.True(SpinWait.SpinUntil(() => native.ReadCount == 1, TimeSpan.FromSeconds(1)));
        try
        {
            if (timeout) Assert.Throws<TimeoutException>(() => listener.Dispose());
            else Assert.Throws<Win32Exception>(() => listener.Dispose());
        }
        finally
        {
            native.Messages.Add((0, 0, 0));
            Assert.True(native.Unregistered.Wait(TimeSpan.FromSeconds(1)));
            listener.Dispose();
        }
    }

    /// <summary>记录专属消息队列和热键生命周期的替身。</summary>
    private sealed class FixtureHotKeyApi : IF8HotKeyNativeApi, IDisposable
    {
        /// <summary>本测试拥有的排队消息。</summary>
        internal BlockingCollection<(int Result, uint Message, nuint KeyId)> Messages { get; } = [];
        /// <summary>阻塞热键注册的测试门。</summary>
        internal ManualResetEventSlim RegistrationGate { get; } = new(false);
        /// <summary>热键解除完成通知。</summary>
        internal ManualResetEventSlim Unregistered { get; } = new(false);
        /// <summary>控制注册是否阻塞。</summary>
        internal bool BlockRegistration { get; set; }
        /// <summary>控制注册是否成功。</summary>
        internal bool RegisterSuccess { get; set; } = true;
        /// <summary>控制退出消息发送结果。</summary>
        internal bool PostSuccess { get; set; } = true;
        /// <summary>控制消息发送成功但暂不退出的超时场景。</summary>
        internal bool IgnorePost { get; set; }
        /// <summary>读取消息的次数。</summary>
        private int readCount;
        /// <summary>返回稳定可读的读取次数。</summary>
        internal int ReadCount => Volatile.Read(ref readCount);
        /// <summary>解除注册次数。</summary>
        internal int UnregisterCount { get; private set; }
        /// <summary>发送退出消息次数。</summary>
        internal int PostCount { get; private set; }
        /// <summary>返回专属于测试的线程标识。</summary>
        public uint CurrentThreadId => 7;
        /// <summary>执行可阻塞的注册并保留系统错误码。</summary>
        public bool Register()
        {
            if (BlockRegistration) RegistrationGate.Wait();
            Marshal.SetLastPInvokeError(RegisterSuccess ? 0 : 1409);
            return RegisterSuccess;
        }
        /// <summary>记录解除注册并唤醒测试。</summary>
        public void Unregister()
        {
            UnregisterCount++;
            Unregistered.Set();
        }
        /// <summary>从测试消息队列获取一条消息。</summary>
        public int ReadMessage(out uint message, out nuint keyId)
        {
            Interlocked.Increment(ref readCount);
            var value = Messages.Take();
            message = value.Message;
            keyId = value.KeyId;
            return value.Result;
        }
        /// <summary>记录退出请求并按故障设置模拟发送结果。</summary>
        public bool PostQuit(uint threadId)
        {
            Assert.Equal(7u, threadId);
            PostCount++;
            Marshal.SetLastPInvokeError(PostSuccess ? 0 : 5);
            if (PostSuccess && !IgnorePost) Messages.Add((0, 0, 0));
            return PostSuccess;
        }
        /// <summary>释放本次替身持有的测试队列与等待对象。</summary>
        public void Dispose()
        {
            Messages.Dispose();
            RegistrationGate.Dispose();
            Unregistered.Dispose();
        }
    }

    /// <summary>只向本测试自有线程投递验证消息的系统入口。</summary>
    private static class NativeHotKey
    {
        /// <summary>投递自有热键线程消息，不创建真实键盘事件。</summary>
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    }
}
