using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>通过文件系统注入验证权限错误、磁盘失败和持久化中断的事务边界。</summary>
public sealed class ResourceFailureTests : IDisposable
{
    /// <summary>仅由本测试拥有的临时目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ResourceFailures", Guid.NewGuid().ToString("N"));
    /// <summary>临时游戏目录。</summary>
    private readonly string game;
    /// <summary>临时应用状态目录。</summary>
    private readonly string state;
    /// <summary>可按操作注入异常的文件系统。</summary>
    private readonly FaultFiles files = new();
    /// <summary>通过接口依赖创建的资源服务。</summary>
    private readonly ResourceSharingService service;

    /// <summary>创建两个独立资源账号。</summary>
    public ResourceFailureTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        Directory.CreateDirectory(Resource("1234ABCD"));
        Directory.CreateDirectory(Resource("5678EF90"));
        File.WriteAllText(Path.Combine(Resource("1234ABCD"), "bundle.bin"), "source");
        File.WriteAllText(Path.Combine(Resource("5678EF90"), "bundle.bin"), "original");
        service = new ResourceSharingService(state, files, () => false);
    }

    /// <summary>单账号属性访问失败时，其余账号应照常返回。</summary>
    [Fact]
    public void InaccessibleAccountDoesNotPreventScanningOtherAccounts()
    {
        files.Before = (operation, path) => { if (operation == "Attributes" && path.EndsWith("1234ABCD", StringComparison.Ordinal)) throw new UnauthorizedAccessException("fixture"); };
        var profile = Assert.Single(service.ScanProfiles(game));
        Assert.Equal("5678EF90", profile.FolderName);
    }

    /// <summary>目录枚举遭遇磁盘错误时，只跳过该目录的容量。</summary>
    [Fact]
    public void InaccessibleResourceDirectoryDoesNotFollowOrCountItsFiles()
    {
        files.Before = (operation, path) => { if (operation == "Entries" && path == Resource("1234ABCD")) throw new IOException("fixture"); };
        Assert.Equal(0, Assert.Single(service.ScanProfiles(game), profile => profile.FolderName == "1234ABCD").Bytes);
    }

    /// <summary>单文件长度读取失败时，不阻断其他账号扫描。</summary>
    [Fact]
    public void InaccessibleResourceFileIsSkipped()
    {
        files.Before = (operation, path) => { if (operation == "Length" && path.StartsWith(Resource("1234ABCD"), StringComparison.Ordinal)) throw new UnauthorizedAccessException("fixture"); };
        Assert.Equal(0, Assert.Single(service.ScanProfiles(game), profile => profile.FolderName == "1234ABCD").Bytes);
    }

    /// <summary>初始意图写入失败时必须保持原目录完整。</summary>
    [Fact]
    public void IntentWriteFailureLeavesOriginalResourceUntouched()
    {
        files.Before = (operation, _) => { if (operation == "Write") throw new IOException("disk-full-fixture"); };
        Assert.Throws<IOException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("original", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(state, "resource-backups"), "*.tmp"));
    }

    /// <summary>移动后原子替换失败仍保留原始意图及备份，并清理临时清单。</summary>
    [Fact]
    public void ReplaceFailureLeavesRecoverableIntentAndNoTemporaryFile()
    {
        files.Before = (operation, _) => { if (operation == "Replace") throw new IOException("replace-fixture"); };
        Assert.Throws<IOException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var clean = new ResourceSharingService(state, () => false);
        var backup = Assert.Single(clean.GetBackups(game));
        Assert.False(backup.Entries[0].Moved);
        Assert.Equal("original", File.ReadAllText(Path.Combine(backup.Entries[0].BackupPath, "bundle.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(state, "resource-backups"), "*.tmp"));
        clean.Restore(backup.Id);
        Assert.Equal("original", File.ReadAllText(Path.Combine(Resource("5678EF90"), "bundle.bin")));
    }

    /// <summary>返回临时账号资源路径。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");

    /// <summary>只清理本测试拥有的临时目录，目录链接仅删除链接本身。</summary>
    public void Dispose() => DeleteTree(root);

    /// <summary>以不跟随链接的方式删除测试夹具。</summary>
    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path, false); return; }
        foreach (var entry in Directory.GetFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) DeleteTree(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path, false);
    }

    /// <summary>在真实磁盘操作前提供显式故障注入接口。</summary>
    internal sealed class FaultFiles : IResourceFileSystem
    {
        /// <summary>实际 Windows 文件系统实现。</summary>
        private readonly IResourceFileSystem inner = new WindowsResourceFileSystem();
        /// <summary>操作执行前由测试设置的故障或外部变更。</summary>
        internal Action<string, string>? Before { get; set; }
        /// <summary>通知测试当前操作。</summary>
        private void Check(string operation, string path) => Before?.Invoke(operation, path);
        /// <summary>读取路径自身属性。</summary>
        public FileAttributes? Attributes(string path) { Check("Attributes", path); return inner.Attributes(path); }
        /// <summary>判断目录存在。</summary>
        public bool DirectoryExists(string path) { Check("DirectoryExists", path); return inner.DirectoryExists(path); }
        /// <summary>创建状态目录。</summary>
        public void CreateDirectory(string path) { Check("CreateDirectory", path); inner.CreateDirectory(path); }
        /// <summary>移动目录本身。</summary>
        public void MoveDirectory(string source, string target) { Check("MoveDirectory", source); inner.MoveDirectory(source, target); }
        /// <summary>判断文件存在。</summary>
        public bool FileExists(string path) { Check("FileExists", path); return inner.FileExists(path); }
        /// <summary>读取清单文本。</summary>
        public string ReadText(string path) { Check("Read", path); return inner.ReadText(path); }
        /// <summary>读取文件长度。</summary>
        public long FileLength(string path) { Check("Length", path); return inner.FileLength(path); }
        /// <summary>物化目录条目。</summary>
        public string[] Entries(string path) { Check("Entries", path); return inner.Entries(path); }
        /// <summary>物化事务清单路径。</summary>
        public string[] ManifestFiles(string path) { Check("Manifests", path); return inner.ManifestFiles(path); }
        /// <summary>写入并强制刷新清单。</summary>
        public void WriteDurable(string path, byte[] bytes) { Check("Write", path); inner.WriteDurable(path, bytes); }
        /// <summary>原子替换已有清单。</summary>
        public void ReplaceFile(string source, string target) { Check("Replace", target); inner.ReplaceFile(source, target); }
        /// <summary>原子安装首次清单。</summary>
        public void MoveFile(string source, string target) { Check("MoveFile", target); inner.MoveFile(source, target); }
        /// <summary>删除本事务的临时文件。</summary>
        public void DeleteFile(string path) { Check("Delete", path); inner.DeleteFile(path); }
    }
}
