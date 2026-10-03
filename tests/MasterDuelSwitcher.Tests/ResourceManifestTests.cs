using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>污染真实 v1 事务清单，验证读取与还原在任何目录变更前拒绝损坏记录。</summary>
public sealed class ResourceManifestTests : IDisposable
{
    /// <summary>本测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.ManifestTests", Guid.NewGuid().ToString("N"));
    /// <summary>真实临时游戏目录。</summary>
    private readonly string game;
    /// <summary>共享来源的真实 0000 目录。</summary>
    private readonly string source;
    /// <summary>启用共享后的真实目标 junction 路径。</summary>
    private readonly string target;
    /// <summary>原始目标资源被保留的真实备份路径。</summary>
    private readonly string originalBackup;
    /// <summary>真实 EnableSharing 生成的 v1 清单路径。</summary>
    private readonly string manifestPath;
    /// <summary>使用真实 Windows 目录接口的资源服务。</summary>
    private readonly ResourceSharingService service;
    /// <summary>真实共享事务的标识及原路径记录。</summary>
    private readonly ShareBackup backup;
    /// <summary>污染清单前来源资源的内容哈希。</summary>
    private readonly string sourceHash;
    /// <summary>污染清单前原始目标备份的内容哈希。</summary>
    private readonly string backupHash;
    /// <summary>污染清单前来源目录的稳定身份。</summary>
    private readonly string sourceIdentity;
    /// <summary>污染清单前原始目标备份目录的稳定身份。</summary>
    private readonly string backupIdentity;

    /// <summary>创建两个真实账号资源目录并启用真实 NTFS junction 共享。</summary>
    public ResourceManifestTests()
    {
        game = Path.Combine(root, "game");
        source = Path.Combine(game, "LocalData", "1234ABCD", "0000");
        target = Path.Combine(game, "LocalData", "5678EF90", "0000");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "bundle.bin"), "manifest-source-resource");
        File.WriteAllText(Path.Combine(target, "bundle.bin"), "manifest-original-target-resource");
        service = new ResourceSharingService(Path.Combine(root, "state"), () => false);
        backup = Assert.IsType<ShareBackup>(service.EnableSharing(game, "1234ABCD", ["5678EF90"]));
        originalBackup = Assert.Single(backup.Entries).BackupPath;
        manifestPath = Path.Combine(root, "state", "resource-backups", backup.Id + ".json");
        sourceHash = Hash(Path.Combine(source, "bundle.bin"));
        backupHash = Hash(Path.Combine(originalBackup, "bundle.bin"));
        sourceIdentity = JunctionOperations.GetDirectoryIdentity(source);
        backupIdentity = JunctionOperations.GetDirectoryIdentity(originalBackup);
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        Assert.Equal(1, manifest["FormatVersion"]!.GetValue<int>());
        Assert.Single(manifest["OriginalDirectoryIdentities"]!.AsObject());
        Assert.Single(manifest["StagingPaths"]!.AsObject());
    }

    /// <summary>每种污染都必须被读取和还原拒绝，且清单及三个真实资源路径保持原样。</summary>
    [Theory]
    [MemberData(nameof(CorruptionCases))]
    public void CorruptManifestIsRejectedBeforeAnyResourceMutation(ManifestCorruption corruption)
    {
        var polluted = Pollute(corruption);
        File.WriteAllText(manifestPath, polluted);
        Assert.Throws<InvalidDataException>(() => service.Restore(backup.Id));
        AssertPreserved(polluted);
        Assert.Throws<InvalidDataException>(() => service.GetBackups(game));
        AssertPreserved(polluted);
    }

    /// <summary>为理论测试提供每个独立污染行为，测试名称显示具体场景。</summary>
    public static IEnumerable<object[]> CorruptionCases() => Enum.GetValues<ManifestCorruption>().Select(corruption => new object[] { corruption });

    /// <summary>对真实清单单独污染一个字段或边界，不创建虚构事务。</summary>
    private string Pollute(ManifestCorruption corruption)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var entries = manifest["Entries"]!.AsArray();
        var entry = entries[0]!.AsObject();
        var identities = manifest["OriginalDirectoryIdentities"]!.AsObject();
        var staging = manifest["StagingPaths"]!.AsObject();
        var identity = identities[target]!.GetValue<string>();
        var stagingPath = staging[target]!.GetValue<string>();
        switch (corruption)
        {
            case ManifestCorruption.RootNull: return "null";
            case ManifestCorruption.RootScalar: return "42";
            case ManifestCorruption.VersionMissing: manifest.Remove("FormatVersion"); break;
            case ManifestCorruption.VersionNotNumber: manifest["FormatVersion"] = "1"; break;
            case ManifestCorruption.VersionFraction: manifest["FormatVersion"] = 1.5; break;
            case ManifestCorruption.VersionWrongInteger: manifest["FormatVersion"] = 2; break;
            case ManifestCorruption.IdNull: manifest["Id"] = null; break;
            case ManifestCorruption.IdInvalid: manifest["Id"] = "../outside"; break;
            case ManifestCorruption.IdDoesNotMatchFile: manifest["Id"] = Guid.NewGuid().ToString("N"); break;
            case ManifestCorruption.GamePathNotCanonical: manifest["GamePath"] = game + "\\."; break;
            case ManifestCorruption.SourcePathInvalid: manifest["SourcePath"] = Path.Combine(game, "LocalSave", "0000"); break;
            case ManifestCorruption.SourcePathNull: manifest["SourcePath"] = null; break;
            case ManifestCorruption.EntriesNull: manifest["Entries"] = null; break;
            case ManifestCorruption.EntriesEmpty: manifest["Entries"] = new JsonArray(); break;
            case ManifestCorruption.EntryNull: entries[0] = null; break;
            case ManifestCorruption.EntryResourceOutside: entry["ResourcePath"] = Path.Combine(root, "outside", "LocalData", "5678EF90", "0000"); break;
            case ManifestCorruption.SourceIsTarget: entry["ResourcePath"] = source; break;
            case ManifestCorruption.DuplicateTargets: entries.Add(entry.DeepClone()); break;
            case ManifestCorruption.BackupPathEscapes: entry["BackupPath"] = Path.Combine(root, "outside-backup"); break;
            case ManifestCorruption.RestoredStateMismatch: manifest["Restored"] = true; break;
            case ManifestCorruption.IdentitiesMissing: manifest.Remove("OriginalDirectoryIdentities"); break;
            case ManifestCorruption.IdentitiesNull: manifest["OriginalDirectoryIdentities"] = null; break;
            case ManifestCorruption.IdentitiesNotObject: manifest["OriginalDirectoryIdentities"] = new JsonArray(); break;
            case ManifestCorruption.IdentityWrongKey: identities.Remove(target); identities[source] = identity; break;
            case ManifestCorruption.IdentityNull: identities[target] = null; break;
            case ManifestCorruption.IdentityWrongLength: identities[target] = "0"; break;
            case ManifestCorruption.IdentityWrongColon: identities[target] = new string('0', 25); break;
            case ManifestCorruption.IdentityNotHex: identities[target] = "00000000:000000000000000G"; break;
            case ManifestCorruption.IdentityExtraColon: identities[target] = "00000000:000000000000000:"; break;
            case ManifestCorruption.IdentityDuplicatedCaseKey: identities[target.ToLowerInvariant()] = identity; break;
            case ManifestCorruption.IdentityMissingKey: identities.Remove(target); break;
            case ManifestCorruption.StagingNull: manifest["StagingPaths"] = null; break;
            case ManifestCorruption.StagingNotObject: manifest["StagingPaths"] = new JsonArray(); break;
            case ManifestCorruption.StagingWrongCount: staging.Clear(); break;
            case ManifestCorruption.StagingMissingKey: staging.Remove(target); staging[source] = stagingPath; break;
            case ManifestCorruption.StagingWrongValue: staging[target] = source; break;
        }
        return manifest.ToJsonString();
    }

    /// <summary>核验来源、原备份、目标 junction 及污染后的清单均未被改变。</summary>
    private void AssertPreserved(string polluted)
    {
        Assert.Equal(sourceHash, Hash(Path.Combine(source, "bundle.bin")));
        Assert.Equal(backupHash, Hash(Path.Combine(originalBackup, "bundle.bin")));
        Assert.Equal(sourceIdentity, JunctionOperations.GetDirectoryIdentity(source));
        Assert.Equal(backupIdentity, JunctionOperations.GetDirectoryIdentity(originalBackup));
        Assert.True((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(source, JunctionOperations.GetTarget(target));
        Assert.Equal(sourceHash, Hash(Path.Combine(target, "bundle.bin")));
        Assert.Equal(polluted, File.ReadAllText(manifestPath));
    }

    /// <summary>计算真实资源文件的 SHA-256，以核验字节内容保持完整。</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>仅删除本测试拥有的临时目录，目录链接不跟随到来源。</summary>
    public void Dispose() => DeleteOwnedDirectory(root);

    /// <summary>逐级清理隔离目录，遇到重解析点只删除链接本身。</summary>
    private static void DeleteOwnedDirectory(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory, false);
            return;
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) DeleteOwnedDirectory(path);
            else File.Delete(path);
        }
        Directory.Delete(directory, false);
    }

    /// <summary>每个值对应一次清单污染，覆盖独立验证条件。</summary>
    public enum ManifestCorruption
    {
        /// <summary>根对象为空。</summary>
        RootNull,
        /// <summary>根对象为标量。</summary>
        RootScalar,
        /// <summary>缺少格式版本。</summary>
        VersionMissing,
        /// <summary>格式版本不是数字。</summary>
        VersionNotNumber,
        /// <summary>格式版本为小数。</summary>
        VersionFraction,
        /// <summary>格式版本整数不受支持。</summary>
        VersionWrongInteger,
        /// <summary>事务标识为空。</summary>
        IdNull,
        /// <summary>事务标识格式非法。</summary>
        IdInvalid,
        /// <summary>事务标识与文件名不同。</summary>
        IdDoesNotMatchFile,
        /// <summary>游戏路径包含未规范化的点段。</summary>
        GamePathNotCanonical,
        /// <summary>来源路径越出账号资源层级。</summary>
        SourcePathInvalid,
        /// <summary>来源路径为空。</summary>
        SourcePathNull,
        /// <summary>目标列表为空值。</summary>
        EntriesNull,
        /// <summary>目标列表没有任何记录。</summary>
        EntriesEmpty,
        /// <summary>列表含空目标记录。</summary>
        EntryNull,
        /// <summary>目标资源指向游戏目录外。</summary>
        EntryResourceOutside,
        /// <summary>来源同时成为目标。</summary>
        SourceIsTarget,
        /// <summary>目标列表重复同一资源路径。</summary>
        DuplicateTargets,
        /// <summary>原资源备份路径越出事务范围。</summary>
        BackupPathEscapes,
        /// <summary>整体恢复状态与条目不一致。</summary>
        RestoredStateMismatch,
        /// <summary>缺少原目录身份字典。</summary>
        IdentitiesMissing,
        /// <summary>原目录身份字典为空值。</summary>
        IdentitiesNull,
        /// <summary>原目录身份记录不是对象。</summary>
        IdentitiesNotObject,
        /// <summary>原目录身份键不属于原始目标。</summary>
        IdentityWrongKey,
        /// <summary>原目录身份值为空。</summary>
        IdentityNull,
        /// <summary>原目录身份长度非法。</summary>
        IdentityWrongLength,
        /// <summary>原目录身份的卷标识分隔符非法。</summary>
        IdentityWrongColon,
        /// <summary>原目录身份含非十六进制字符。</summary>
        IdentityNotHex,
        /// <summary>原目录身份末尾含第二个冒号。</summary>
        IdentityExtraColon,
        /// <summary>原目录身份含仅大小写不同的重复键。</summary>
        IdentityDuplicatedCaseKey,
        /// <summary>原目录身份缺少一个原始目标。</summary>
        IdentityMissingKey,
        /// <summary>暂存路径字典为空值。</summary>
        StagingNull,
        /// <summary>暂存路径记录不是对象。</summary>
        StagingNotObject,
        /// <summary>暂存路径数量与条目数量不同。</summary>
        StagingWrongCount,
        /// <summary>暂存路径缺少本事务目标键。</summary>
        StagingMissingKey,
        /// <summary>暂存路径值指向非预期目录。</summary>
        StagingWrongValue
    }
}
