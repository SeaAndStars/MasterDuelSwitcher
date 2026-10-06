using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>通过真实目录竞态验证启用共享的预检、意图和暂存安装保护。</summary>
public sealed class ResourceRaceTests : IDisposable
{
    /// <summary>本测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.RaceTests", Guid.NewGuid().ToString("N"));
    /// <summary>真实临时游戏目录。</summary>
    private readonly string game;
    /// <summary>独立于游戏目录的事务状态根目录。</summary>
    private readonly string state;
    /// <summary>共享来源账号的真实资源路径。</summary>
    private readonly string source;
    /// <summary>共享目标账号的真实资源路径。</summary>
    private readonly string target;
    /// <summary>允许在真实磁盘操作前注入外部变化的文件系统。</summary>
    private readonly ResourceFailureTests.FaultFiles files = new();
    /// <summary>依赖真实故障注入文件系统的被测服务。</summary>
    private readonly ResourceSharingService service;
    /// <summary>初始来源资源的内容哈希。</summary>
    private readonly string sourceHash;
    /// <summary>初始目标资源的内容哈希。</summary>
    private readonly string originalHash;
    /// <summary>初始目标目录的稳定身份。</summary>
    private readonly string originalIdentity;

    /// <summary>创建两个真实账号资源，服务构造不产生事务清单。</summary>
    public ResourceRaceTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        source = Resource("1234ABCD");
        target = Resource("5678EF90");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "bundle.bin"), "race-source-resource");
        File.WriteAllText(Path.Combine(target, "bundle.bin"), "race-original-resource");
        sourceHash = Hash(Path.Combine(source, "bundle.bin"));
        originalHash = Hash(Path.Combine(target, "bundle.bin"));
        originalIdentity = JunctionOperations.GetDirectoryIdentity(target);
        service = new ResourceSharingService(state, files, () => false);
    }

    /// <summary>空目标列表应在创建事务前被拒绝，来源和目标均保持原样。</summary>
    [Fact]
    public void EmptyTargetsAreRejectedBeforeIntent()
    {
        Assert.Throws<ArgumentException>(() => service.EnableSharing(game, "1234ABCD", []));
        AssertNoTransactions();
        AssertSourcePreserved();
        AssertOriginalPreserved(target);
    }

    /// <summary>预先存在的目标文件不得被移动、覆盖或作为资源目录共享。</summary>
    [Fact]
    public void ExistingResourceFileIsPreservedBeforeIntent()
    {
        var displaced = Path.Combine(root, "displaced-original");
        Directory.Move(target, displaced);
        File.WriteAllText(target, "third-party-resource-file");
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.Equal("third-party-resource-file", File.ReadAllText(target));
        AssertNoTransactions();
        AssertOriginalPreserved(displaced);
        AssertSourcePreserved();
    }

    /// <summary>目标虽已指向当前来源，旧活动事务仍指向不同来源时必须拒绝接管。</summary>
    [Fact]
    public void MatchingCurrentJunctionDoesNotOverrideDifferentActiveTransactionSource()
    {
        var prior = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var currentSource = Resource("AAAABBBB");
        Directory.CreateDirectory(currentSource);
        File.WriteAllText(Path.Combine(currentSource, "bundle.bin"), "current-other-source");
        Directory.Delete(target, false);
        JunctionOperations.Create(target, currentSource);
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "AAAABBBB", ["5678EF90"]));
        Assert.Equal(currentSource, JunctionOperations.GetTarget(target));
        Assert.Equal("current-other-source", File.ReadAllText(Path.Combine(target, "bundle.bin")));
        AssertOriginalPreserved(Assert.Single(prior.Entries).BackupPath);
        Assert.Equal(prior.Id, ReadIntent().Id);
        AssertSourcePreserved();
    }

    /// <summary>两目标事务中已独立还原的第一项可重新共享，第二项旧链接及备份保持完整。</summary>
    [Fact]
    public void RestoredEntryCanBeSharedAgainWithoutChangingOtherPendingEntry()
    {
        var secondTarget = Resource("AAAABBBB");
        Directory.CreateDirectory(secondTarget);
        File.WriteAllText(Path.Combine(secondTarget, "bundle.bin"), "secondary-original-resource");
        var secondHash = Hash(Path.Combine(secondTarget, "bundle.bin"));
        var secondIdentity = JunctionOperations.GetDirectoryIdentity(secondTarget);
        var prior = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90", "AAAABBBB"]));
        var restoredEntry = Assert.Single(prior.Entries, entry => entry.ResourcePath == target);
        var pendingEntry = Assert.Single(prior.Entries, entry => entry.ResourcePath == secondTarget);
        Directory.Delete(target, false);
        Directory.Move(restoredEntry.BackupPath, target);
        var manifestPath = Path.Combine(state, "resource-backups", prior.Id + ".json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var savedEntry = manifest["Entries"]!.AsArray().Single(entry => entry!["ResourcePath"]!.GetValue<string>() == target)!.AsObject();
        savedEntry["Restored"] = true;
        savedEntry["Moved"] = false;
        savedEntry["Linked"] = false;
        manifest["Restored"] = false;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var expectedPriorManifest = File.ReadAllText(manifestPath);

        var current = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.NotEqual(prior.Id, current.Id);
        AssertOriginalPreserved(Assert.Single(current.Entries).BackupPath);
        Assert.Equal(source, JunctionOperations.GetTarget(target));
        Assert.Equal(source, JunctionOperations.GetTarget(secondTarget));
        Assert.Equal(secondHash, Hash(Path.Combine(pendingEntry.BackupPath, "bundle.bin")));
        Assert.Equal(secondIdentity, JunctionOperations.GetDirectoryIdentity(pendingEntry.BackupPath));
        Assert.Equal(expectedPriorManifest, File.ReadAllText(manifestPath));
        Assert.Equal(2, service.GetBackups(game).Count);
        AssertSourcePreserved();
    }

    /// <summary>预检时真实创建本次 GUID 的备份或暂存碰撞路径，事务不得覆盖该目录。</summary>
    [Theory]
    [InlineData(".mdbackup-")]
    [InlineData(".mdjunction-")]
    public void GuidPathCollisionIsPreservedBeforeIntent(string suffix)
    {
        var collided = "";
        files.Before = (operation, path) =>
        {
            if (operation != "Attributes" || !path.StartsWith(target + suffix, StringComparison.Ordinal) || collided.Length != 0) return;
            collided = path;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "keep.bin"), "external-guid-collision");
        };
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.NotEmpty(collided);
        Assert.Equal("external-guid-collision", File.ReadAllText(Path.Combine(collided, "keep.bin")));
        AssertNoTransactions();
        AssertOriginalPreserved(target);
        AssertSourcePreserved();
    }

    /// <summary>意图已保存后，原目标消失或变为目录、文件、链接时保留清单和外部变化。</summary>
    [Theory]
    [InlineData(IntentMutation.Disappeared)]
    [InlineData(IntentMutation.ReplacedByJunction)]
    [InlineData(IntentMutation.ReplacedByFile)]
    [InlineData(IntentMutation.ReplacedByDirectory)]
    public void TargetChangedAfterIntentIsPreservedBeforeOriginalMove(IntentMutation mutation)
    {
        var displaced = Path.Combine(root, "displaced-original");
        var outside = CreateUnrelatedDirectory();
        BeforeFirstMove(() =>
        {
            Directory.Move(target, displaced);
            switch (mutation)
            {
                case IntentMutation.ReplacedByJunction: JunctionOperations.Create(target, outside); break;
                case IntentMutation.ReplacedByFile: File.WriteAllText(target, "external-new-file"); break;
                case IntentMutation.ReplacedByDirectory:
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "keep.bin"), "external-new-directory");
                    break;
            }
        });
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var intent = ReadIntent();
        var entry = Assert.Single(intent.Entries);
        Assert.False(entry.Moved);
        Assert.False(entry.Linked);
        Assert.False(intent.Restored);
        Assert.False(Directory.Exists(entry.BackupPath));
        AssertOriginalPreserved(displaced);
        AssertSourcePreserved();
        switch (mutation)
        {
            case IntentMutation.Disappeared: Assert.False(Directory.Exists(target)); break;
            case IntentMutation.ReplacedByJunction:
                Assert.Equal(outside, JunctionOperations.GetTarget(target));
                Assert.Equal("external-unrelated-data", File.ReadAllText(Path.Combine(target, "keep.bin")));
                break;
            case IntentMutation.ReplacedByFile: Assert.Equal("external-new-file", File.ReadAllText(target)); break;
            case IntentMutation.ReplacedByDirectory:
                Assert.Null(JunctionOperations.GetTarget(target));
                Assert.Equal("external-new-directory", File.ReadAllText(Path.Combine(target, "keep.bin")));
                break;
        }
    }

    /// <summary>原本没有 0000 的账号在意图后出现外部新目录时，该新目录必须完整保留。</summary>
    [Fact]
    public void OriginallyMissingTargetCreatedAfterIntentIsPreserved()
    {
        var displaced = Path.Combine(root, "displaced-original");
        Directory.Move(target, displaced);
        BeforeFirstMove(() =>
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.bin"), "external-new-directory");
        });
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        var entry = Assert.Single(ReadIntent().Entries);
        Assert.False(entry.OriginalExisted);
        Assert.False(entry.Moved);
        Assert.False(entry.Linked);
        Assert.False(Directory.Exists(entry.BackupPath));
        Assert.Null(JunctionOperations.GetTarget(target));
        Assert.Equal("external-new-directory", File.ReadAllText(Path.Combine(target, "keep.bin")));
        AssertOriginalPreserved(displaced);
        AssertSourcePreserved();
    }

    /// <summary>暂存 junction 安装前目标或暂存被外部改变时，保留原始备份及外部内容。</summary>
    [Theory]
    [InlineData(StagingMutation.TargetCreated)]
    [InlineData(StagingMutation.ReplacedByUnknownJunction)]
    [InlineData(StagingMutation.Removed)]
    public void StagingChangedBeforeInstallationPreservesOriginalBackup(StagingMutation mutation)
    {
        var observedStaging = "";
        var outside = CreateUnrelatedDirectory();
        BeforeStagingInstall(staging =>
        {
            observedStaging = staging;
            switch (mutation)
            {
                case StagingMutation.TargetCreated:
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "keep.bin"), "external-new-directory");
                    break;
                case StagingMutation.ReplacedByUnknownJunction:
                    Directory.Delete(staging, false);
                    JunctionOperations.Create(staging, outside);
                    break;
                case StagingMutation.Removed: Directory.Delete(staging, false); break;
            }
        });
        Assert.Throws<InvalidOperationException>(() => service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        Assert.NotEmpty(observedStaging);
        var intent = ReadIntent();
        var entry = Assert.Single(intent.Entries);
        Assert.True(entry.Moved);
        Assert.False(entry.Linked);
        Assert.False(intent.Restored);
        AssertOriginalPreserved(entry.BackupPath);
        AssertSourcePreserved();
        switch (mutation)
        {
            case StagingMutation.TargetCreated:
                Assert.Null(JunctionOperations.GetTarget(target));
                Assert.Equal("external-new-directory", File.ReadAllText(Path.Combine(target, "keep.bin")));
                Assert.Equal(source, JunctionOperations.GetTarget(observedStaging));
                break;
            case StagingMutation.ReplacedByUnknownJunction:
                Assert.False(Directory.Exists(target));
                Assert.Equal(outside, JunctionOperations.GetTarget(observedStaging));
                Assert.Equal("external-unrelated-data", File.ReadAllText(Path.Combine(observedStaging, "keep.bin")));
                break;
            case StagingMutation.Removed:
                Assert.False(Directory.Exists(target));
                Assert.False(Directory.Exists(observedStaging));
                break;
        }
    }

    /// <summary>在目标第二次真实属性读取前变化目录，此时完整事务意图已经原子保存。</summary>
    private void BeforeFirstMove(Action mutation)
    {
        var targetReads = 0;
        files.Before = (operation, path) =>
        {
            if (operation == "Attributes" && path == target && ++targetReads == 2)
            {
                Assert.Single(Directory.GetFiles(Path.Combine(state, "resource-backups"), "*.json"));
                mutation();
            }
        };
    }

    /// <summary>第二次清单替换前暂存 junction 已创建，可真实改变安装前的目录状态。</summary>
    private void BeforeStagingInstall(Action<string> mutation)
    {
        var replacements = 0;
        files.Before = (operation, path) =>
        {
            if (operation != "Replace" || ++replacements != 2) return;
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var staging = manifest["StagingPaths"]![target]!.GetValue<string>();
            Assert.Equal(source, JunctionOperations.GetTarget(staging));
            mutation(staging);
        };
    }

    /// <summary>停止故障注入后读取唯一真实事务，避免断言引入额外目录变化。</summary>
    private ShareBackup ReadIntent()
    {
        files.Before = null;
        return Assert.Single(new ResourceSharingService(state, () => false).GetBackups(game));
    }

    /// <summary>预检失败不应创建事务目录或清单。</summary>
    private void AssertNoTransactions()
    {
        files.Before = null;
        Assert.Empty(new ResourceSharingService(state, () => false).GetBackups(game));
        Assert.False(Directory.Exists(Path.Combine(state, "resource-backups")));
    }

    /// <summary>核验原目录内容和身份均未被服务替换。</summary>
    private void AssertOriginalPreserved(string path)
    {
        Assert.Equal(originalHash, Hash(Path.Combine(path, "bundle.bin")));
        Assert.Equal(originalIdentity, JunctionOperations.GetDirectoryIdentity(path));
    }

    /// <summary>核验共享来源保持独立真实目录和原始内容。</summary>
    private void AssertSourcePreserved()
    {
        Assert.Null(JunctionOperations.GetTarget(source));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>创建测试自有的第三方目录，作为未知链接的目标。</summary>
    private string CreateUnrelatedDirectory()
    {
        var outside = Path.Combine(root, "unrelated");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.bin"), "external-unrelated-data");
        return outside;
    }

    /// <summary>返回临时账号中唯一允许共享的资源路径。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");

    /// <summary>计算真实文件的内容哈希。</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>复用不跟随目录链接的隔离夹具清理。</summary>
    public void Dispose() => ResourceFailureTests.DeleteTree(root);

    /// <summary>意图保存后对原目标施加的真实外部变更。</summary>
    public enum IntentMutation
    {
        /// <summary>原目标被外部移动到其他目录。</summary>
        Disappeared,
        /// <summary>原目标被未知 junction 替换。</summary>
        ReplacedByJunction,
        /// <summary>原目标被普通文件替换。</summary>
        ReplacedByFile,
        /// <summary>原目标被另一个普通目录替换。</summary>
        ReplacedByDirectory
    }

    /// <summary>暂存 junction 安装前施加的真实外部变更。</summary>
    public enum StagingMutation
    {
        /// <summary>目标位置出现外部新建目录。</summary>
        TargetCreated,
        /// <summary>暂存位置被未知来源 junction 替换。</summary>
        ReplacedByUnknownJunction,
        /// <summary>暂存 junction 被外部移除。</summary>
        Removed
    }
}
