using System.Diagnostics;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>在独立临时目录中验证资源路径的规范化、账号边界和真实文件属性。</summary>
public sealed class ResourcePathTests : IDisposable
{
    /// <summary>本测试独占的临时目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.PathTests", Guid.NewGuid().ToString("N"));
    /// <summary>实际存在的临时游戏安装目录。</summary>
    private readonly string game;
    /// <summary>实际存在的八位十六进制账号目录。</summary>
    private readonly string account;

    /// <summary>创建独立安装和账号目录，避免测试依赖用户真实游戏数据。</summary>
    public ResourcePathTests()
    {
        game = Path.Combine(root, "game");
        account = Path.Combine(game, "LocalData", "1234ABCD");
        Directory.CreateDirectory(account);
    }

    /// <summary>本地目录中的点段和尾部分隔符应被规范化，磁盘根目录应保留分隔符。</summary>
    [Fact]
    public void NormalizeResolvesSegmentsAndPreservesDriveRoot()
    {
        Assert.Equal(account, ResourcePathValidation.Normalize(Path.Combine(game, ".", "LocalData", "1234ABCD") + "\\"));
        Assert.Equal(Path.GetPathRoot(root), ResourcePathValidation.Normalize(Path.GetPathRoot(root)!));
        Assert.Equal(game, ResourcePathValidation.Normalize(Path.GetRelativePath(Environment.CurrentDirectory, game)));
    }

    /// <summary>缺少路径或无效路径字符应在文件系统访问前被拒绝。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C:\\bad\0path")]
    public void NormalizeRejectsMissingOrInvalidPath(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => ResourcePathValidation.Normalize(path!));
    }

    /// <summary>网络、设备及备用数据流路径均不属于允许管理的本地目录。</summary>
    [Theory]
    [InlineData("\\\\fixture-host\\share\\game")]
    [InlineData("\\\\?\\C:\\fixture-game")]
    [InlineData("\\\\.\\C:\\fixture-game")]
    [InlineData("C:\\fixture-game:stream")]
    public void NormalizeRejectsNonLocalOrAlternateStreamPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => ResourcePathValidation.Normalize(path));
    }

    /// <summary>目录比较应忽略大小写和路径别名，同时区分相邻目录。</summary>
    [Fact]
    public void EqualComparesNormalizedWindowsDirectories()
    {
        Assert.True(ResourcePathValidation.Equal(game, game.ToUpperInvariant() + "\\."));
        Assert.False(ResourcePathValidation.Equal(game, Path.Combine(root, "other-game")));
    }

    /// <summary>账号名的长度、字符和空值决定是否允许构造账号资源路径。</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234ABCD", true)]
    [InlineData("abcdef12", true)]
    [InlineData("1234ABCG", false)]
    [InlineData("1234ABC", false)]
    [InlineData("1234ABCDE", false)]
    [InlineData("1234ABCD\n", false)]
    public void IsAccountRequiresExactlyEightHexadecimalCharacters(string? name, bool expected)
    {
        Assert.Equal(expected, ResourcePathValidation.IsAccount(name));
    }

    /// <summary>事务标识只能使用无分隔符的小写十六进制 GUID。</summary>
    [Fact]
    public void ValidateIdAcceptsLowercaseHexadecimalIdentifier()
    {
        ResourcePathValidation.ValidateId("0123456789abcdef0123456789abcdef");
    }

    /// <summary>非法标识应在读取清单前被拒绝。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    public void ValidateIdRejectsInvalidIdentifier(string? id)
    {
        Assert.Throws<ArgumentException>(() => ResourcePathValidation.ValidateId(id!));
    }

    /// <summary>属性读取应返回真实目录和文件类型，并区分缺失叶子及缺失父目录。</summary>
    [Fact]
    public void AttributesReportsRealEntriesAndMissingPaths()
    {
        var file = Path.Combine(root, "occupied.bin");
        File.WriteAllText(file, "keep");
        Assert.True((ResourcePathValidation.Attributes(account)!.Value & FileAttributes.Directory) != 0);
        Assert.True((ResourcePathValidation.Attributes(file)!.Value & FileAttributes.Directory) == 0);
        Assert.Null(ResourcePathValidation.Attributes(Path.Combine(account, "missing")));
        Assert.Null(ResourcePathValidation.Attributes(Path.Combine(root, "missing-parent", "missing")));
        Assert.Throws<ArgumentException>(() => ResourcePathValidation.Attributes("bad\0path"));
    }

    /// <summary>缺失的后代路径允许通过祖先检查，磁盘根目录可跳过自身检查。</summary>
    [Fact]
    public void EnsureNoReparseAncestorsAllowsMissingDescendantsAndDriveParentBoundary()
    {
        ResourcePathValidation.EnsureNoReparseAncestors(Path.Combine(account, "not-created", "0000"));
        ResourcePathValidation.EnsureNoReparseAncestors(Path.GetPathRoot(root)!, false);
    }

    /// <summary>目录被文件占用时应拒绝，显式跳过叶子时仅检查其目录祖先。</summary>
    [Fact]
    public void EnsureNoReparseAncestorsRejectsFileOccupancy()
    {
        var file = Path.Combine(account, "occupied");
        File.WriteAllText(file, "keep");
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.EnsureNoReparseAncestors(file));
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.EnsureNoReparseAncestors(Path.Combine(file, "child")));
        ResourcePathValidation.EnsureNoReparseAncestors(file, false);
        Assert.Equal("keep", File.ReadAllText(file));
    }

    /// <summary>真实 junction 不得成为资源路径的叶子或祖先。</summary>
    [Fact]
    public void EnsureNoReparseAncestorsRejectsRealJunction()
    {
        var link = Path.Combine(root, "linked-game");
        CreateJunction(link, game);
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.EnsureNoReparseAncestors(link));
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.EnsureNoReparseAncestors(Path.Combine(link, "LocalData")));
        Assert.True(Directory.Exists(account));
    }

    /// <summary>有效安装应返回规范化目录，缺失安装应明确报告目录不存在。</summary>
    [Fact]
    public void GameRequiresAnExistingInstallation()
    {
        Assert.Equal(game, ResourcePathValidation.Game(game + "\\."));
        Assert.Throws<DirectoryNotFoundException>(() => ResourcePathValidation.Game(Path.Combine(root, "missing-game")));
        var emptyGame = Path.Combine(root, "game-without-local-data");
        Directory.CreateDirectory(emptyGame);
        Assert.Equal(emptyGame, ResourcePathValidation.Game(emptyGame));
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyGame));
    }

    /// <summary>已有账号只构造 0000 路径，不擅自创建资源目录。</summary>
    [Fact]
    public void ResourceReturnsOnlyAccount0000WithoutCreatingIt()
    {
        var resource = ResourcePathValidation.Resource(game, "1234ABCD");
        Assert.Equal(Path.Combine(account, "0000"), resource);
        Assert.False(Directory.Exists(resource));
        Assert.Throws<DirectoryNotFoundException>(() => ResourcePathValidation.Resource(game, "FFFFFFFF"));
    }

    /// <summary>路径穿越或空账号名应在资源目录构造前被拒绝。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("../1234ABCD")]
    [InlineData("1234ABCD\\0000")]
    public void ResourceRejectsInvalidAccountName(string? name)
    {
        Assert.Throws<ArgumentException>(() => ResourcePathValidation.Resource(game, name!));
    }

    /// <summary>清单允许规范账号的 0000 路径，资源本身尚未创建也可核验。</summary>
    [Fact]
    public void ManifestResourceAcceptsCanonicalAccountResource()
    {
        ResourcePathValidation.ManifestResource(game, Path.Combine(account, "0000"));
    }

    /// <summary>清单必须拒绝缺失账号层级、无效账号、错误资源叶子和游戏外路径。</summary>
    [Fact]
    public void ManifestResourceRejectsInvalidHierarchyAndOutsideGame()
    {
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, Path.GetPathRoot(root)!));
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, Path.Combine(Path.GetPathRoot(root)!, "0000")));
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, Path.Combine(game, "LocalData", "not-account", "0000")));
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, Path.Combine(account, "LocalSave")));
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, Path.Combine(root, "other-game", "LocalData", "1234ABCD", "0000")));
    }

    /// <summary>清单中的点段、重复分隔符、斜杠或尾部分隔符别名必须在恢复前被拒绝。</summary>
    [Theory]
    [InlineData("dot")]
    [InlineData("parent")]
    [InlineData("slash")]
    [InlineData("trailing")]
    [InlineData("duplicate")]
    [InlineData("relative")]
    public void ManifestResourceRejectsNonCanonicalAliases(string alias)
    {
        var canonical = Path.Combine(account, "0000");
        var resource = alias switch
        {
            "dot" => Path.Combine(account, ".", "0000"),
            "parent" => Path.Combine(account, "ignored", "..", "0000"),
            "slash" => canonical.Replace('\\', '/'),
            "trailing" => canonical + "\\",
            "relative" => canonical[..2] + Path.GetRelativePath(Path.GetFullPath(canonical[..2] + "."), canonical),
            _ => account + "\\\\0000"
        };
        Assert.Throws<InvalidDataException>(() => ResourcePathValidation.ManifestResource(game, resource));
        Assert.False(Directory.Exists(canonical));
    }

    /// <summary>状态目录可以位于 LocalData 的相邻目录，应拒绝占用 LocalData 本身或后代。</summary>
    [Fact]
    public void StateEnforcesLocalDataBoundaryWithoutRejectingSibling()
    {
        var localData = Path.Combine(game, "LocalData");
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.State(localData, game));
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.State(Path.Combine(localData, "transactions"), game));
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.State(Path.Combine(localData, "transactions"), game + "\\."));
        ResourcePathValidation.State(Path.Combine(game, "LocalData-backups", "transactions"), game);
        ResourcePathValidation.State(Path.Combine(root, "state"), game);
    }

    /// <summary>状态目录的点段或斜杠别名仍必须受到规范化后的 LocalData 隔离边界约束。</summary>
    [Theory]
    [InlineData("parent")]
    [InlineData("slash")]
    public void StateRejectsAliasesInsideLocalData(string alias)
    {
        var localData = Path.Combine(game, "LocalData");
        var state = alias == "parent"
            ? Path.Combine(game, "outside", "..", "LocalData", "transactions")
            : Path.Combine(localData, "transactions").Replace('\\', '/');
        Assert.Throws<InvalidOperationException>(() => ResourcePathValidation.State(state, game));
        Assert.False(Directory.Exists(Path.Combine(localData, "transactions")));
    }

    /// <summary>实际资源目录必须存在，缺失目录应拒绝作为共享来源。</summary>
    [Fact]
    public void RealDirectoryRequiresExistingDirectory()
    {
        ResourcePathValidation.RealDirectory(account);
        Assert.Throws<DirectoryNotFoundException>(() => ResourcePathValidation.RealDirectory(Path.Combine(account, "0000")));
    }

    /// <summary>创建测试专有的真实 NTFS junction，并确认操作成功。</summary>
    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{path}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>删除本测试所有的临时数据，遇到链接时只删除链接自身。</summary>
    public void Dispose() => DeleteDirectory(root);

    /// <summary>逐级清理实际目录，防止递归进入 junction 指向的目标。</summary>
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
