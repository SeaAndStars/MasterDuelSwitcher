using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证事务互斥超时、异常释放和退出进程留下的锁接管。</summary>
public sealed class ResourceLockTests
{
    /// <summary>平台报告等待超时时，禁止进入事务。</summary>
    [Fact]
    public void TimeoutRejectsTransaction()
    {
        var mutex = new FixtureMutex { WaitResult = false };
        Assert.Throws<InvalidOperationException>(() => { using var guard = new ResourceSharingService.GameTransactionLock(Guid.NewGuid().ToString("N"), mutex); });
        Assert.True(mutex.Disposed);
    }

    /// <summary>等待遇到平台错误时，释放已经创建的句柄并保留原异常。</summary>
    [Fact]
    public void WaitErrorDisposesMutexAndPreservesError()
    {
        var mutex = new FixtureMutex { WaitError = new IOException("platform-fixture") };
        Assert.Throws<IOException>(() => { using var guard = new ResourceSharingService.GameTransactionLock(Guid.NewGuid().ToString("N"), mutex); });
        Assert.True(mutex.Disposed);
    }

    /// <summary>遗留锁的异常代表当前线程已经获得所有权，事务应可继续。</summary>
    [Fact]
    public void AbandonedMutexIsAcquiredAndReleasedNormally()
    {
        var mutex = new FixtureMutex { WaitError = new AbandonedMutexException() };
        using (new ResourceSharingService.GameTransactionLock(Guid.NewGuid().ToString("N"), mutex)) { }
        Assert.True(mutex.Released);
        Assert.True(mutex.Disposed);
    }

    /// <summary>普通平台锁获得成功后按生命周期释放。</summary>
    [Fact]
    public void SuccessfulMutexIsReleasedNormally()
    {
        var mutex = new FixtureMutex();
        using (new ResourceSharingService.GameTransactionLock(Guid.NewGuid().ToString("N"), mutex)) { }
        Assert.True(mutex.Released);
        Assert.True(mutex.Disposed);
    }

    /// <summary>模拟互斥平台结果并记录资源生命周期。</summary>
    private sealed class FixtureMutex : IResourceMutex
    {
        /// <summary>等待操作的模拟返回结果。</summary>
        internal bool WaitResult { get; init; } = true;
        /// <summary>等待操作的模拟平台异常。</summary>
        internal Exception? WaitError { get; init; }
        /// <summary>平台锁是否已被释放。</summary>
        internal bool Released { get; private set; }
        /// <summary>平台句柄是否已被释放。</summary>
        internal bool Disposed { get; private set; }
        /// <summary>报告当前等待结果或平台异常。</summary>
        public bool Wait(TimeSpan timeout) { if (WaitError is not null) throw WaitError; return WaitResult; }
        /// <summary>释放模拟锁所有权。</summary>
        public void Release() => Released = true;
        /// <summary>释放模拟句柄。</summary>
        public void Dispose() => Disposed = true;
    }
}
