using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>在隔离真实 NTFS 目录中验证失效共享的显式修复、原文归档及失败保全。</summary>
public sealed class ResourceRepairTests : IDisposable
{
    /// <summary>本测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.RepairTests", Guid.NewGuid().ToString("N"));
    /// <summary>临时游戏安装目录。</summary>
    private readonly string game;
    /// <summary>临时应用状态根目录。</summary>
    private readonly string state;
    /// <summary>真实共享来源的 0000 路径。</summary>
    private readonly string source;
    /// <summary>失去旧共享链接后新建的目标 0000 路径。</summary>
    private readonly string target;
    /// <summary>提供真实磁盘操作前故障和竞态注入的文件系统。</summary>
    private readonly ResourceFailureTests.FaultFiles files = new();
    /// <summary>使用真实文件系统边界的资源服务。</summary>
    private readonly ResourceSharingService service;
    /// <summary>已成功共享但原备份随后缺失的旧事务。</summary>
    private readonly ShareBackup oldBackup;
    /// <summary>旧事务中唯一目标的原始状态。</summary>
    private readonly ShareEntry oldEntry;
    /// <summary>旧事务首次成功保存后的原始清单字节。</summary>
    private readonly byte[] oldManifestBytes;
    /// <summary>修复前来源文件的内容哈希。</summary>
    private readonly string sourceHash;
    /// <summary>新建目标目录的十九字节资源，用于核验备份和还原。</summary>
    private readonly byte[] currentTargetBytes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    /// <summary>先完成真实共享，再仅在测试数据中移除旧备份并重建十九字节目标目录。</summary>
    public ResourceRepairTests()
    {
        game = Path.Combine(root, "game");
        state = Path.Combine(root, "state");
        source = Resource("1234ABCD");
        target = Resource("5678EF90");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "bundle.bin"), "source");
        File.WriteAllText(Path.Combine(target, "bundle.bin"), "old-original");
        service = new ResourceSharingService(state, files, () => false);
        oldBackup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        oldEntry = Assert.Single(oldBackup.Entries);
        oldManifestBytes = File.ReadAllBytes(Manifest);
        sourceHash = Hash(Path.Combine(source, "bundle.bin"));
        JunctionOperations.RemoveMatching(target, source);
        ResourceFailureTests.DeleteTree(oldEntry.BackupPath);
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "current.bin"), currentTargetBytes);
    }

    /// <summary>修复应原文归档旧事务、备份当前目标并重建共享，后续还原找回十九字节当前目标。</summary>
    [Fact]
    public void RepairArchivesExactOldManifestAndCanRestoreCurrentTarget()
    {
        var currentIdentity = JunctionOperations.GetDirectoryIdentity(target);
        var repaired = Assert.IsType<ShareBackup>(service.RepairInvalidSharing(oldBackup.Id));
        var entry = Assert.Single(repaired.Entries);
        Assert.NotEqual(oldBackup.Id, repaired.Id);
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Manifest));
        Assert.False(JsonNode.Parse(File.ReadAllBytes(Archive))!["Restored"]!.GetValue<bool>());
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Equal(source, JunctionOperations.GetTarget(target));
        Assert.True(entry.OriginalExisted);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(entry.BackupPath, "current.bin")));
        Assert.Equal(currentIdentity, JunctionOperations.GetDirectoryIdentity(entry.BackupPath));
        Assert.Equal(repaired.Id, Assert.Single(service.GetBackups(game)).Id);
        service.Restore(repaired.Id);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(currentIdentity, JunctionOperations.GetDirectoryIdentity(target));
        Assert.Null(JunctionOperations.GetTarget(target));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
    }

    /// <summary>已还原、已完成条目、无原目录或多目标清单均超出单目标失效修复范围。</summary>
    [Theory]
    [InlineData("restored")]
    [InlineData("entry-restored")]
    [InlineData("no-original")]
    [InlineData("multiple")]
    public void UnsupportedTransactionShapeIsRejectedBeforeArchive(string shape)
    {
        RewriteManifest(document =>
        {
            var entry = document["Entries"]![0]!.AsObject();
            if (shape == "restored") { document["Restored"] = true; entry["Restored"] = true; }
            if (shape == "entry-restored") entry["Restored"] = true;
            if (shape == "no-original")
            {
                entry["OriginalExisted"] = false;
                document["OriginalDirectoryIdentities"]!.AsObject().Clear();
            }
            if (shape == "multiple")
            {
                var second = Resource("ABCDEF01");
                Directory.CreateDirectory(Path.GetDirectoryName(second)!);
                document["Entries"]!.AsArray().Add(new JsonObject
                {
                    ["ResourcePath"] = second,
                    ["BackupPath"] = second + ".mdbackup-" + oldBackup.Id,
                    ["OriginalExisted"] = false,
                    ["Restored"] = false
                });
                document["StagingPaths"]!.AsObject()[second] = second + ".mdjunction-" + oldBackup.Id;
            }
        });
        AssertRejectedWithCurrentTargetPreserved();
    }

    /// <summary>旧备份或暂存路径存在任何实际目录、普通文件或 junction 时，修复应保留现场。</summary>
    [Theory]
    [InlineData(false, "directory")]
    [InlineData(false, "file")]
    [InlineData(false, "junction")]
    [InlineData(true, "directory")]
    [InlineData(true, "file")]
    [InlineData(true, "junction")]
    public void ExistingBackupOrStagingIsRejected(bool staging, string kind)
    {
        var path = staging ? target + ".mdjunction-" + oldBackup.Id : oldEntry.BackupPath;
        if (kind == "directory") Directory.CreateDirectory(path);
        if (kind == "file") File.WriteAllText(path, "preserve-file");
        if (kind == "junction") JunctionOperations.Create(path, source);
        AssertRejectedWithCurrentTargetPreserved();
        if (kind == "file") Assert.Equal("preserve-file", File.ReadAllText(path));
        else Assert.True(Directory.Exists(path));
    }

    /// <summary>目标目录身份仍与旧原目录相同时，应交给普通还原而不是归档失效事务。</summary>
    [Fact]
    public void TargetWithOriginalDirectoryIdentityIsRejected()
    {
        RewriteManifest(document => document["OriginalDirectoryIdentities"]!.AsObject()[target] = JunctionOperations.GetDirectoryIdentity(target));
        AssertRejectedWithCurrentTargetPreserved();
    }

    /// <summary>无效标识应在读取事务和创建归档前被拒绝。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("../outside")]
    public void InvalidRepairIdentifierIsRejected(string? id)
    {
        Assert.Throws<ArgumentException>(() => service.RepairInvalidSharing(id!));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>指定清单不存在时应报告文件缺失，现有事务和账号资源仍保持原样。</summary>
    [Fact]
    public void MissingRepairManifestPreservesOtherTransaction()
    {
        Assert.Throws<FileNotFoundException>(() => service.RepairInvalidSharing(new string('a', 32)));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>游戏运行检查应在任何修复事务变更前拒绝操作。</summary>
    [Fact]
    public void RunningGamePreventsRepair()
    {
        var blocked = new ResourceSharingService(state, files, () => true);
        Assert.Throws<InvalidOperationException>(() => blocked.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>目标缺失、被文件占用或已经是 junction 时，修复应拒绝并保留当前路径状态。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("junction")]
    public void NonOrdinaryTargetIsRejected(string kind)
    {
        ResourceFailureTests.DeleteTree(target);
        if (kind == "file") File.WriteAllBytes(target, currentTargetBytes);
        if (kind == "junction") JunctionOperations.Create(target, source);
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
        if (kind == "missing") Assert.False(Directory.Exists(target));
        if (kind == "file") Assert.Equal(currentTargetBytes, File.ReadAllBytes(target));
        if (kind == "junction") Assert.Equal(source, JunctionOperations.GetTarget(target));
    }

    /// <summary>来源缺失、为空、被文件占用或成为链接时，应拒绝归档并保留当前目标。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("file")]
    [InlineData("junction")]
    public void UnusableSourceIsRejectedBeforeArchive(string kind)
    {
        var heldSource = Path.Combine(root, "held-source");
        if (kind == "empty") File.Delete(Path.Combine(source, "bundle.bin"));
        else if (kind == "junction")
        {
            Directory.Move(source, heldSource);
            JunctionOperations.Create(source, heldSource);
        }
        else
        {
            ResourceFailureTests.DeleteTree(source);
            if (kind == "file") File.WriteAllText(source, "preserve-source-file");
        }
        if (kind == "missing") Assert.Throws<DirectoryNotFoundException>(() => service.RepairInvalidSharing(oldBackup.Id));
        else Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.False(File.Exists(Archive));
        if (kind == "missing") Assert.False(Directory.Exists(source));
        if (kind == "empty") Assert.Empty(Directory.GetFileSystemEntries(source));
        if (kind == "file") Assert.Equal("preserve-source-file", File.ReadAllText(source));
        if (kind == "junction") Assert.Equal(sourceHash, Hash(Path.Combine(heldSource, "bundle.bin")));
    }

    /// <summary>来源在扫描前消失或被替换为链接时，修复不得仅凭先前目录检查继续归档。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceChangedDuringScanIsRejected(bool junction)
    {
        var heldSource = Path.Combine(root, "held-source-account");
        var sourceAccount = Path.GetDirectoryName(source)!;
        files.Before = (operation, path) =>
        {
            if (operation != "Attributes" || path != (junction ? source : sourceAccount)) return;
            files.Before = null;
            if (junction)
            {
                Directory.Move(source, heldSource);
                JunctionOperations.Create(source, heldSource);
            }
            else Directory.Move(sourceAccount, heldSource);
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(heldSource, junction ? "bundle.bin" : "0000\\bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>归档目录被普通文件或 junction 占用时，修复应拒绝且不写入外部目录。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonOrdinaryArchiveParentIsRejected(bool junction)
    {
        var history = Path.GetDirectoryName(Archive)!;
        var external = Path.Combine(root, "external-history");
        if (junction)
        {
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.bin"), "outside-history");
            JunctionOperations.Create(history, external);
        }
        else File.WriteAllText(history, "history-is-file");
        AssertRejectedWithCurrentTargetPreserved();
        if (junction)
        {
            Assert.Equal("outside-history", File.ReadAllText(Path.Combine(external, "keep.bin")));
            Assert.False(File.Exists(Path.Combine(external, oldBackup.Id + ".json")));
        }
        else Assert.Equal("history-is-file", File.ReadAllText(history));
    }

    /// <summary>同标识归档已被文件、目录或 junction 占用时，修复不得覆盖已有历史。</summary>
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("junction")]
    public void ExistingArchiveIsNeverOverwritten(string kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
        var external = Path.Combine(root, "existing-history-target");
        if (kind == "file") File.WriteAllText(Archive, "existing-history");
        if (kind == "directory") Directory.CreateDirectory(Archive);
        if (kind == "junction")
        {
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "keep.bin"), "outside-history");
            JunctionOperations.Create(Archive, external);
        }
        AssertRejectedWithCurrentTargetPreserved(false);
        if (kind == "file") Assert.Equal("existing-history", File.ReadAllText(Archive));
        if (kind == "directory") Assert.True(Directory.Exists(Archive));
        if (kind == "junction") Assert.Equal("outside-history", File.ReadAllText(Path.Combine(external, "keep.bin")));
    }

    /// <summary>其他活动事务将当前目标作为来源或目标时，应保留依赖而拒绝重新共享。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherActiveTransactionUsingTargetPreventsRepair(bool targetIsSource)
    {
        if (targetIsSource)
        {
            var third = Resource("ABCDEF01");
            Directory.CreateDirectory(third);
            File.WriteAllText(Path.Combine(third, "third.bin"), "third-original");
            service.EnableSharing(game, "5678EF90", ["ABCDEF01"]);
            Assert.Equal(target, JunctionOperations.GetTarget(third));
        }
        else SaveAdditionalTargetTransaction(false, false);
        AssertRejectedWithCurrentTargetPreserved();
        Assert.Equal(2, service.GetBackups(game).Count);
    }

    /// <summary>其他事务已整体还原或已完成此目标条目时，其历史记录不应阻止当前显式修复。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedOtherTransactionDoesNotBlockRepair(bool fullyRestored)
    {
        var otherId = SaveAdditionalTargetTransaction(fullyRestored, true);
        var repaired = Assert.IsType<ShareBackup>(service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(Assert.Single(repaired.Entries).BackupPath, "current.bin")));
        Assert.Contains(service.GetBackups(game), backup => backup.Id == otherId);
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>初次读取之后清单被替换为另一游戏的合法事务时，应拒绝跨游戏修复。</summary>
    [Fact]
    public void ManifestGameChangedBeforeLockedReadIsRejected()
    {
        var reads = 0;
        var changed = Array.Empty<byte>();
        files.Before = (operation, path) =>
        {
            if (operation != "Read" || path != Manifest || ++reads != 2) return;
            files.Before = null;
            var otherGame = Path.Combine(root, "other-game");
            var otherSource = Path.Combine(otherGame, "LocalData", "1234ABCD", "0000");
            var otherTarget = Path.Combine(otherGame, "LocalData", "5678EF90", "0000");
            Directory.CreateDirectory(otherGame);
            RewriteManifest(document =>
            {
                var identity = document["OriginalDirectoryIdentities"]!.AsObject()[target]!.GetValue<string>();
                document["GamePath"] = otherGame;
                document["SourcePath"] = otherSource;
                document["Entries"]![0]!["ResourcePath"] = otherTarget;
                document["Entries"]![0]!["BackupPath"] = otherTarget + ".mdbackup-" + oldBackup.Id;
                document["OriginalDirectoryIdentities"] = new JsonObject { [otherTarget] = identity };
                document["StagingPaths"] = new JsonObject { [otherTarget] = otherTarget + ".mdjunction-" + oldBackup.Id };
            });
            changed = File.ReadAllBytes(Manifest);
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.NotEmpty(changed);
        Assert.Equal(changed, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>创建归档目录期间目标被替换为另一真实目录时，应在归档前识别新身份并保留两份现场。</summary>
    [Fact]
    public void TargetIdentityChangedAfterInitialCheckIsRejected()
    {
        var heldTarget = Path.Combine(root, "held-current-target");
        files.Before = (operation, path) =>
        {
            if (operation != "CreateDirectory" || path != Path.GetDirectoryName(Archive)) return;
            files.Before = null;
            Directory.Move(target, heldTarget);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "third-party.bin"), "replacement-target");
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(heldTarget, "current.bin")));
        Assert.Equal("replacement-target", File.ReadAllText(Path.Combine(target, "third-party.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>首次校验之后旧备份、暂存或空来源状态出现变化时，应在归档前重新检查并保留旧清单。</summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("staging")]
    [InlineData("empty-source")]
    public void CandidateChangedWhileArchiveDirectoryWasCreatedIsRejected(string change)
    {
        files.Before = (operation, path) =>
        {
            if (operation != "CreateDirectory" || path != Path.GetDirectoryName(Archive)) return;
            files.Before = null;
            if (change == "backup") Directory.CreateDirectory(oldEntry.BackupPath);
            if (change == "staging") JunctionOperations.Create(target + ".mdjunction-" + oldBackup.Id, source);
            if (change == "empty-source") File.Delete(Path.Combine(source, "bundle.bin"));
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.False(File.Exists(Archive));
        if (change == "backup") Assert.True(Directory.Exists(oldEntry.BackupPath));
        if (change == "staging") Assert.Equal(source, JunctionOperations.GetTarget(target + ".mdjunction-" + oldBackup.Id));
        if (change == "empty-source") Assert.Empty(Directory.GetFileSystemEntries(source));
        else Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>归档目录创建期间清单被合法内容替换时，应保留变更后的原文并拒绝归档。</summary>
    [Fact]
    public void ManifestTextChangedBeforeArchiveIsRejected()
    {
        var changed = Array.Empty<byte>();
        files.Before = (operation, path) =>
        {
            if (operation != "CreateDirectory" || path != Path.GetDirectoryName(Archive)) return;
            files.Before = null;
            RewriteManifest(document => document["CreatedAt"] = "2030-01-01T00:00:00+00:00");
            changed = File.ReadAllBytes(Manifest);
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.NotEmpty(changed);
        Assert.Equal(changed, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>旧清单在创建历史目录期间被目录占用时，读取校验应拒绝并保留原文件字节。</summary>
    [Fact]
    public void ManifestBecameDirectoryBeforeArchiveIsRejected()
    {
        var heldManifest = Path.Combine(root, "held-manifest.json");
        files.Before = (operation, path) =>
        {
            if (operation != "CreateDirectory" || path != Path.GetDirectoryName(Archive)) return;
            files.Before = null;
            File.Move(Manifest, heldManifest);
            Directory.CreateDirectory(Manifest);
        };
        Assert.Throws<InvalidDataException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(heldManifest));
        Assert.True(Directory.Exists(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>归档路径在创建历史目录期间变成链接时，应拒绝并保留外部目录内容。</summary>
    [Fact]
    public void ArchiveParentBecameJunctionBeforeArchiveIsRejected()
    {
        var external = Path.Combine(root, "racing-history");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.bin"), "outside-history");
        files.Before = (operation, path) =>
        {
            if (operation != "CreateDirectory" || path != Path.GetDirectoryName(Archive)) return;
            files.Before = null;
            JunctionOperations.Create(path, external);
        };
        AssertRejectedWithCurrentTargetPreserved();
        Assert.Equal("outside-history", File.ReadAllText(Path.Combine(external, "keep.bin")));
        Assert.False(File.Exists(Path.Combine(external, oldBackup.Id + ".json")));
    }

    /// <summary>归档原文的原子移动失败时，旧活动清单仍在原处且目标资源保持未替换。</summary>
    [Fact]
    public void ArchiveMoveFailureLeavesOldManifestActive()
    {
        files.Before = (operation, path) =>
        {
            if (operation == "MoveFile" && path == Archive) throw new IOException("archive-move-fixture");
        };
        Assert.Throws<IOException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.False(File.Exists(Archive));
    }

    /// <summary>旧清单归档后来源容量读取失败时，不回搬旧记录，当前目标可用普通新共享事务恢复。</summary>
    [Fact]
    public void NewEnableFailureKeepsArchiveAndCurrentTarget()
    {
        files.Before = (operation, path) =>
        {
            if (operation != "MoveFile" || path != Archive) return;
            files.Before = (nextOperation, nextPath) =>
            {
                if (nextOperation == "Entries" && nextPath == source) throw new IOException("post-archive-source-read-fixture");
            };
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        files.Before = null;
        AssertArchivedAndCurrentTargetPreserved();
        Assert.Empty(service.GetBackups(game));
        var retry = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        service.Restore(retry.Id);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
    }

    /// <summary>归档后新事务首次写入或原子安装失败时，应清理临时文件且保留当前十九字节目标。</summary>
    [Theory]
    [InlineData("Write")]
    [InlineData("MoveFile")]
    public void NewIntentPersistenceFailurePreservesCurrentTarget(string failingOperation)
    {
        files.Before = (operation, path) =>
        {
            if (operation == failingOperation && path != Archive) throw new IOException("new-intent-fixture");
        };
        Assert.Throws<IOException>(() => service.RepairInvalidSharing(oldBackup.Id));
        files.Before = null;
        AssertArchivedAndCurrentTargetPreserved();
        Assert.Empty(service.GetBackups(game));
        Assert.Empty(Directory.GetFiles(Path.Combine(state, "resource-backups"), "*.tmp"));
    }

    /// <summary>当前目标移动前或移动后保存步骤失败时，新清单应支持普通还原，旧清单保持归档。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewTargetMoveOrSaveFailureLeavesRestorableNewTransaction(bool failAfterMove)
    {
        files.Before = (operation, path) =>
        {
            if ((!failAfterMove && operation == "MoveDirectory" && path == target) ||
                (failAfterMove && operation == "Replace")) throw new IOException("new-target-move-fixture");
        };
        Assert.Throws<IOException>(() => service.RepairInvalidSharing(oldBackup.Id));
        files.Before = null;
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Manifest));
        var pending = Assert.Single(service.GetBackups(game));
        Assert.NotEqual(oldBackup.Id, pending.Id);
        Assert.False(pending.Restored);
        service.Restore(pending.Id);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
    }

    /// <summary>旧清单归档时当前目标被第三方目录替换，应拒绝新事务接管且保留两份目标数据。</summary>
    [Fact]
    public void PostArchiveTargetReplacementIsRejectedBeforeNewIntent()
    {
        var heldTarget = Path.Combine(root, "held-confirmed-target");
        files.Before = (operation, path) =>
        {
            if (operation != "MoveFile" || path != Archive) return;
            files.Before = null;
            Directory.Move(target, heldTarget);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "third-party.bin"), "third-party-target");
        };
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(heldTarget, "current.bin")));
        Assert.Equal("third-party-target", File.ReadAllText(Path.Combine(target, "third-party.bin")));
        Assert.Null(JunctionOperations.GetTarget(target));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Empty(service.GetBackups(game));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(target)!, "0000.mdbackup-*"));
    }

    /// <summary>两个独立活动事务仅使用无关目标时，当前修复应继续且完整保留两份共享及其备份。</summary>
    [Fact]
    public void UnrelatedActiveTargetDoesNotBlockRepair()
    {
        var third = Resource("ABCDEF01");
        var fourth = Resource("9ABCDEF0");
        Directory.CreateDirectory(third);
        Directory.CreateDirectory(fourth);
        File.WriteAllText(Path.Combine(third, "third.bin"), "first-unrelated-original");
        File.WriteAllText(Path.Combine(fourth, "fourth.bin"), "second-unrelated-original");
        var other = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["ABCDEF01"]));
        var fourthBackup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["9ABCDEF0"]));
        var repaired = Assert.IsType<ShareBackup>(service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.Equal(source, JunctionOperations.GetTarget(third));
        Assert.Equal(source, JunctionOperations.GetTarget(fourth));
        Assert.Equal("first-unrelated-original", File.ReadAllText(Path.Combine(Assert.Single(other.Entries).BackupPath, "third.bin")));
        Assert.Equal("second-unrelated-original", File.ReadAllText(Path.Combine(Assert.Single(fourthBackup.Entries).BackupPath, "fourth.bin")));
        Assert.Equal(3, service.GetBackups(game).Count);
        service.Restore(repaired.Id);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(source, JunctionOperations.GetTarget(third));
        Assert.Equal(source, JunctionOperations.GetTarget(fourth));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>新 junction 已安装但最终状态保存失败时，新事务仍能识别实际链接并还原当前目标。</summary>
    [Fact]
    public void FinalSaveAfterNewJunctionInstallationLeavesRestorableTransaction()
    {
        var replacements = 0;
        files.Before = (operation, _) =>
        {
            if (operation == "Replace" && ++replacements == 3) throw new IOException("new-junction-final-save-fixture");
        };
        Assert.Throws<IOException>(() => service.RepairInvalidSharing(oldBackup.Id));
        files.Before = null;
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Manifest));
        Assert.Equal(source, JunctionOperations.GetTarget(target));
        var pending = Assert.Single(service.GetBackups(game));
        Assert.NotEqual(oldBackup.Id, pending.Id);
        Assert.False(pending.Restored);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(Assert.Single(pending.Entries).BackupPath, "current.bin")));
        service.Restore(pending.Id);
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
    }

    /// <summary>保存另一份格式合法的目标事务，模拟活动依赖或已完成的历史条目。</summary>
    private string SaveAdditionalTargetTransaction(bool restored, bool entryRestored)
    {
        var id = Guid.NewGuid().ToString("N");
        var document = JsonNode.Parse(File.ReadAllBytes(Manifest))!.AsObject();
        document["Id"] = id;
        document["Restored"] = restored;
        document["Entries"]![0]!["BackupPath"] = target + ".mdbackup-" + id;
        document["Entries"]![0]!["Restored"] = entryRestored;
        document["OriginalDirectoryIdentities"]!.AsObject()[target] = JunctionOperations.GetDirectoryIdentity(target);
        document["StagingPaths"]!.AsObject()[target] = target + ".mdjunction-" + id;
        File.WriteAllText(Path.Combine(state, "resource-backups", id + ".json"), document.ToJsonString());
        return id;
    }

    /// <summary>核验旧清单已原样归档而当前独立目标尚未替换，来源内容仍保持不变。</summary>
    private void AssertArchivedAndCurrentTargetPreserved()
    {
        Assert.Equal(oldManifestBytes, File.ReadAllBytes(Archive));
        Assert.False(File.Exists(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Null(JunctionOperations.GetTarget(target));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
    }

    /// <summary>在拒绝修复后检查清单、当前目标和来源均保持原样且没有创建归档清单。</summary>
    private void AssertRejectedWithCurrentTargetPreserved(bool expectArchiveMissing = true)
    {
        var manifest = File.ReadAllBytes(Manifest);
        Assert.Throws<InvalidOperationException>(() => service.RepairInvalidSharing(oldBackup.Id));
        Assert.Equal(manifest, File.ReadAllBytes(Manifest));
        Assert.Equal(currentTargetBytes, File.ReadAllBytes(Path.Combine(target, "current.bin")));
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        if (expectArchiveMissing) Assert.False(File.Exists(Archive));
    }

    /// <summary>对真实旧清单进行单项修改，保持其余格式和路径有效。</summary>
    private void RewriteManifest(Action<JsonObject> change)
    {
        var document = JsonNode.Parse(File.ReadAllBytes(Manifest))!.AsObject();
        change(document);
        File.WriteAllText(Manifest, document.ToJsonString());
    }

    /// <summary>旧事务当前活动清单的完整路径。</summary>
    private string Manifest => Path.Combine(state, "resource-backups", oldBackup.Id + ".json");
    /// <summary>旧事务应原文保留的独立历史清单路径。</summary>
    private string Archive => Path.Combine(state, "resource-backups", "invalid-history", oldBackup.Id + ".json");
    /// <summary>返回临时账号唯一允许管理的资源目录。</summary>
    private string Resource(string folder) => Path.Combine(game, "LocalData", folder, "0000");
    /// <summary>计算真实文件的内容哈希，检查共享来源保持不变。</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    /// <summary>只清理本测试临时根目录，不递归进入任何目录链接。</summary>
    public void Dispose() => ResourceFailureTests.DeleteTree(root);
}
