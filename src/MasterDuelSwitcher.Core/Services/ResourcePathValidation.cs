using System.Text.RegularExpressions;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>限定资源事务的本地路径、账号名称和无链接祖先边界。</summary>
internal static class ResourcePathValidation
{
    /// <summary>账号名必须严格为八位十六进制字符。</summary>
    private static readonly Regex AccountPattern = new("\\A[0-9a-fA-F]{8}\\z", RegexOptions.CultureInvariant);
    /// <summary>事务名必须为无分隔符的小写 GUID。</summary>
    private static readonly Regex BackupPattern = new("\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);

    /// <summary>规范化本地磁盘路径，拒绝 UNC、设备路径和备用数据流。</summary>
    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':'))
            throw new ArgumentException("路径必须是本地磁盘目录。", nameof(path));
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>按 Windows 大小写规则比较规范化目录。</summary>
    internal static bool Equal(string first, string second) => string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>判断账号名称是否严格符合约定。</summary>
    internal static bool IsAccount(string? name) => name is not null && AccountPattern.IsMatch(name);

    /// <summary>校验事务标识，避免读取任意状态文件。</summary>
    internal static void ValidateId(string id)
    {
        if (id is null || !BackupPattern.IsMatch(id)) throw new ArgumentException("备份标识格式错误。", nameof(id));
    }

    /// <summary>读取路径本身的属性；路径不存在时返回空值，其他错误保留。</summary>
    internal static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>逐级检查已有祖先，任何重解析点、文件或访问错误均阻止操作。</summary>
    internal static void EnsureNoReparseAncestors(string path, bool includeLeaf = true)
    {
        var normalized = Normalize(path);
        var current = includeLeaf ? normalized : Path.GetDirectoryName(normalized);
        while (current is not null)
        {
            if (Attributes(current) is { } attributes)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException($"路径祖先包含目录链接：{current}");
                if ((attributes & FileAttributes.Directory) == 0) throw new InvalidOperationException($"目录路径被文件占用：{current}");
            }
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>检查游戏根目录，允许尚未产生 LocalData 的安装。</summary>
    internal static string Game(string path)
    {
        var game = Normalize(path);
        EnsureNoReparseAncestors(game);
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException($"游戏安装目录不存在：{game}");
        return game;
    }

    /// <summary>从八位账号名构造唯一允许操作的 0000 路径。</summary>
    internal static string Resource(string game, string folder)
    {
        if (!IsAccount(folder)) throw new ArgumentException("账号目录名必须是八位十六进制字符。", nameof(folder));
        var account = Path.Combine(game, "LocalData", folder);
        EnsureNoReparseAncestors(account);
        if (!Directory.Exists(account)) throw new DirectoryNotFoundException($"账号目录不存在：{account}");
        return Path.Combine(account, "0000");
    }

    /// <summary>核验清单路径严格匹配游戏账号的 0000，且不存在路径逃逸。</summary>
    internal static void ManifestResource(string game, string resource)
    {
        var normalized = Normalize(resource);
        var account = Path.GetDirectoryName(normalized);
        var localData = account is null ? null : Path.GetDirectoryName(account);
        if (account is null || localData is null || !IsAccount(Path.GetFileName(account)) ||
            !string.Equals(Path.GetFileName(normalized), "0000", StringComparison.Ordinal) ||
            !Equal(localData, Path.Combine(game, "LocalData")) || !string.Equals(resource, normalized, StringComparison.Ordinal))
            throw new InvalidDataException($"备份清单包含越界资源路径：{resource}");
        EnsureNoReparseAncestors(account);
    }

    /// <summary>检查状态目录祖先及其与 LocalData 的隔离关系。</summary>
    internal static void State(string state, string game)
    {
        var normalized = Normalize(state);
        EnsureNoReparseAncestors(normalized);
        var data = Normalize(Path.Combine(game, "LocalData"));
        if (Equal(normalized, data) || normalized.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("事务清单目录必须位于 LocalData 之外。");
    }

    /// <summary>检查存在的路径是未重解析的实际目录。</summary>
    internal static void RealDirectory(string path)
    {
        EnsureNoReparseAncestors(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"资源目录不存在：{path}");
    }
}
