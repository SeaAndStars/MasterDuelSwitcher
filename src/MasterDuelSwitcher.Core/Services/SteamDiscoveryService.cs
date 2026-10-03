using MasterDuelSwitcher.Core.Models;
using Microsoft.Win32;
using System.Globalization;
using System.Security;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>提供 Steam 安装、游戏安装与已记住账号的只读发现能力。</summary>
public interface ISteamDiscoveryService
{
    /// <summary>发现 Steam、Master Duel 与登录账号，可使用手工目录覆盖。</summary>
    DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null);

    /// <summary>读取指定 Steam 目录中的已记住账号。</summary>
    IReadOnlyList<SteamAccount> ReadAccounts(string steamPath);
}

/// <summary>提供只读安装候选，使发现逻辑可独立于实际注册表测试。</summary>
public interface ISteamDiscoveryEnvironment
{
    /// <summary>读取指定注册表根与视图中的已知 Steam 安装值。</summary>
    IReadOnlyList<object?> ReadRegistryValues(RegistryHive hive, RegistryView view);

    /// <summary>注册表无有效结果时尝试的默认 Steam 目录。</summary>
    string FallbackSteamDirectory { get; }
}

/// <summary>通过 Windows 注册表只读查询 Steam 安装候选。</summary>
public sealed class WindowsSteamDiscoveryEnvironment : ISteamDiscoveryEnvironment
{
    /// <summary>使用指定注册表子键与默认 Steam 目录创建只读查询环境。</summary>
    public WindowsSteamDiscoveryEnvironment(string registrySubKey = @"Software\Valve\Steam", string? fallbackSteamDirectory = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>注册表无有效结果时尝试的默认 Steam 目录。</summary>
    public string FallbackSteamDirectory => throw new NotImplementedException();

    /// <summary>读取指定注册表根与视图中的已知 Steam 安装值。</summary>
    public IReadOnlyList<object?> ReadRegistryValues(RegistryHive hive, RegistryView view) => throw new NotImplementedException();
}

/// <summary>读取 Steam 安装位置、游戏库与已记住的账号。</summary>
public sealed class SteamDiscoveryService : ISteamDiscoveryService
{
    /// <summary>使用可选只读发现环境和文本读取器；默认使用 Windows 注册表与真实文件。</summary>
    public SteamDiscoveryService(ISteamDiscoveryEnvironment? environment = null, Func<string, string>? readText = null)
    {
        if (environment is not null || readText is not null)
            throw new NotImplementedException();
    }

    /// <summary>发现 Steam、Master Duel 与登录账号，可使用手工目录覆盖。</summary>
    public DiscoveryResult Discover(string? steamOverride = null, string? gameOverride = null)
    {
        string steam = string.IsNullOrWhiteSpace(steamOverride)
            ? FindSteamInstallation()
            : ValidateExecutableDirectory(steamOverride, "steam.exe", "Steam");
        string game = string.IsNullOrWhiteSpace(gameOverride)
            ? FindGameInstallation(steam)
            : ValidateExecutableDirectory(gameOverride, "masterduel.exe", "Master Duel");
        return new DiscoveryResult
        {
            SteamPath = steam,
            GamePath = game,
            Accounts = string.IsNullOrEmpty(steam) ? [] : ReadAccounts(steam)
        };
    }

    /// <summary>读取指定 Steam 目录中的账号列表。</summary>
    public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamPath);
        string loginFile = Path.Combine(Path.GetFullPath(steamPath), "config", "loginusers.vdf");
        if (!File.Exists(loginFile))
            return [];

        var users = VdfDocument.Parse(File.ReadAllText(loginFile)).Find("users");
        if (users is null || users.Value is not null)
            return [];

        return users.Children
            .Where(node => node.Value is null && node.Key.Length == 17 && ulong.TryParse(node.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(node => new SteamAccount
            {
                SteamId = node.Key,
                AccountName = node.Find("AccountName")?.Value ?? "",
                PersonaName = node.Find("PersonaName")?.Value ?? "",
                RememberPassword = node.Find("RememberPassword")?.Value == "1",
                AllowAutoLogin = node.Find("AllowAutoLogin")?.Value == "1",
                MostRecent = node.Find("MostRecent")?.Value == "1"
            })
            .Where(account => !string.IsNullOrWhiteSpace(account.AccountName))
            .OrderByDescending(account => account.MostRecent)
            .ThenBy(account => account.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>从当前用户和两种机器注册表视图中查找 Steam 可执行文件。</summary>
    private static string FindSteamInstallation()
    {
        var candidates = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"Software\Valve\Steam");
                    foreach (string name in new[] { "SteamPath", "InstallPath", "SteamExe" })
                    {
                        if (key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value))
                            candidates.Add(value);
                    }
                }
                catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // 某个注册表视图不可读取时继续尝试其余已知安装位置。
                }
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string directory = GetExecutableDirectory(candidate, "steam.exe");
            if (File.Exists(Path.Combine(directory, "steam.exe")))
                return directory;
        }
        return "";
    }

    /// <summary>按主库和附加库清单定位 Master Duel 的真实安装目录。</summary>
    private static string FindGameInstallation(string steamPath)
    {
        if (string.IsNullOrEmpty(steamPath))
            return "";

        var libraries = new List<string> { steamPath };
        string foldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(foldersFile))
        {
            try
            {
                var folders = VdfDocument.Parse(File.ReadAllText(foldersFile)).Find("libraryfolders");
                if (folders?.Value is null)
                {
                    foreach (var node in folders?.Children ?? [])
                    {
                        if (!int.TryParse(node.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            continue;
                        string? library = node.Value ?? node.Find("path")?.Value;
                        if (!string.IsNullOrWhiteSpace(library) && Path.IsPathFullyQualified(library))
                            libraries.Add(Path.GetFullPath(library));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                // 附加库配置暂不可读或损坏时保留原文件，仍检查已知主库。
            }
        }

        foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string manifestFile = Path.Combine(library, "steamapps", "appmanifest_1449850.acf");
            if (!File.Exists(manifestFile))
                continue;
            try
            {
                var app = VdfDocument.Parse(File.ReadAllText(manifestFile)).Find("AppState");
                string? installDirectory = app?.Find("installdir")?.Value;
                if (app?.Find("appid")?.Value != "1449850" || string.IsNullOrWhiteSpace(installDirectory)
                    || installDirectory is "." or ".." || installDirectory.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                    || Path.IsPathRooted(installDirectory))
                    continue;
                string game = Path.GetFullPath(Path.Combine(library, "steamapps", "common", installDirectory));
                if (File.Exists(Path.Combine(game, "masterduel.exe")))
                    return game;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
            {
                // 单个库清单暂不可读或损坏时保留原文件，继续检查后续库。
            }
        }
        return "";
    }

    /// <summary>规范化手工覆盖路径并确认对应程序存在。</summary>
    private static string ValidateExecutableDirectory(string path, string executable, string displayName)
    {
        string directory = GetExecutableDirectory(path, executable);
        if (!File.Exists(Path.Combine(directory, executable)))
            throw new InvalidOperationException($"所选 {displayName} 目录中缺少 {executable}。");
        return directory;
    }

    /// <summary>允许使用程序所在目录或程序文件本身作为手工路径。</summary>
    private static string GetExecutableDirectory(string path, string executable)
    {
        string full = Path.GetFullPath(path.Trim());
        return string.Equals(Path.GetFileName(full), executable, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(full)!
            : Path.TrimEndingDirectorySeparator(full);
    }
}
