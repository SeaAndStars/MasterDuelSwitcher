using System.Diagnostics;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>串行运行真实进程检测测试，避免测试进程影响其他默认检测调用。</summary>
[CollectionDefinition("ResourceProcessDetection", DisableParallelization = true)]
public sealed class ResourceProcessCollection { }

/// <summary>使用临时目录和测试自有进程验证默认 Master Duel 运行检测。</summary>
[Collection("ResourceProcessDetection")]
public sealed class ResourceProcessTests : IDisposable
{
    /// <summary>本测试独占的临时数据和进程夹具目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ProcessTests", Guid.NewGuid().ToString("N"));
    /// <summary>包含两个账号资源目录的临时安装。</summary>
    private readonly string game;
    /// <summary>本测试独立使用的事务状态根目录。</summary>
    private readonly string state;
    /// <summary>使用真实文件系统和默认进程检测的被测服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>创建实际账号资源，默认构造服务且不注入进程检测函数。</summary>
    public ResourceProcessTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        Directory.CreateDirectory(Resource("1234ABCD"));
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin"), "shared-source");
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "bundle.bin"), "original-target");
        service = new ResourceSharingService(state);
    }

    /// <summary>没有游戏进程时，默认检测应允许真实资源共享并完整还原原目录。</summary>
    [Fact]
    public void DefaultDetectorAllowsSharingAndRestoreWhenGameIsStopped()
    {
        AssertNoExistingGameProcess();
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("shared-source", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        service.Restore(backup.Id);
        Assert.Equal("original-target", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>测试自有的 masterduel 进程运行时，默认检测应拦截共享和还原且保留事务。</summary>
    [Fact]
    public void DefaultDetectorBlocksSharingAndRestoreWhileOwnedGameProcessRuns()
    {
        AssertNoExistingGameProcess();
        var backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var manifest = Path.Combine(state, "resource-backups", backup.Id + ".json");
        var originalManifest = File.ReadAllText(manifest);
        var fixturePath = Path.Combine(root, "masterduel.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fixturePath);
        var start = new ProcessStartInfo(fixturePath, "-t 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var fixture = Process.Start(start)!;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => IsOwnedGameProcessVisible(fixture.Id), TimeSpan.FromSeconds(5)));
            Assert.False(fixture.HasExited);
            Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
            Assert.Throws<InvalidOperationException>(() => service.Restore(backup.Id));
            Assert.Equal(originalManifest, File.ReadAllText(manifest));
            Assert.Equal("original-target", File.ReadAllText(Path.Combine(Assert.Single(backup.Entries).BackupPath, "bundle.bin")));
        }
        finally
        {
            try
            {
                if (!fixture.HasExited) fixture.Kill();
                Assert.True(fixture.WaitForExit(5000));
            }
            finally { fixture.Dispose(); }
        }
        service.Restore(backup.Id);
        Assert.Equal("original-target", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.True(Assert.Single(service.GetBackups(game)).Restored);
    }

    /// <summary>确认环境中没有已有游戏进程；仅读取并释放句柄，不结束任何外部进程。</summary>
    private static void AssertNoExistingGameProcess()
    {
        var processes = Process.GetProcessesByName("masterduel");
        try { Assert.Empty(processes); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>确认启动的自有进程可被默认检测所用的进程名称查询发现。</summary>
    private static bool IsOwnedGameProcessVisible(int processId)
    {
        var processes = Process.GetProcessesByName("masterduel");
        try { return processes.Any(process => process.Id == processId); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>返回临时账号中唯一允许共享的 0000 目录。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");

    /// <summary>清理测试自有数据，对 junction 仅移除链接本身。</summary>
    public void Dispose() => DeleteDirectory(root);

    /// <summary>逐级移除实际目录，避免进入共享链接所指向的资源来源。</summary>
    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, false);
            return;
        }
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) DeleteDirectory(child);
            else File.Delete(child);
        }
        Directory.Delete(path, false);
    }
}
