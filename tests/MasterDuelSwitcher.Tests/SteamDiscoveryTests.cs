using MasterDuelSwitcher.Core.Services;
using Microsoft.Win32;
using System.Security;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证真实临时 Steam 库与账号配置的发现结果。</summary>
public sealed class SteamDiscoveryTests : IDisposable
{
    /// <summary>每次测试独占的临时目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelDiscoveryTests", Guid.NewGuid().ToString("N"));

    /// <summary>创建测试所需的临时根目录。</summary>
    public SteamDiscoveryTests() => Directory.CreateDirectory(root);

    /// <summary>验证从附加 Steam 库的清单读取真实 installdir。</summary>
    [Fact]
    public void DiscoverFindsGameInSecondaryLibraryFromManifest()
    {
        string steam = Path.Combine(root, "Steam");
        string library = Path.Combine(root, "Library");
        string game = Path.Combine(library, "steamapps", "common", "Master Duel Custom");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(steam, "steam.exe"), "fixture");
        File.WriteAllText(Path.Combine(game, "masterduel.exe"), "fixture");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + library.Replace("\\", "\\\\") + "\" } }");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1449850.acf"), "\"AppState\" { \"appid\" \"1449850\" \"installdir\" \"Master Duel Custom\" }");

        var result = new SteamDiscoveryService().Discover(steam);

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal(game, result.GamePath);
    }

    /// <summary>验证主库清单损坏时继续发现次库游戏，且保留损坏清单。</summary>
    [Fact]
    public void DiscoverSkipsMalformedPrimaryManifestAndFindsSecondaryGame()
    {
        string steam = CreateSteamFixture();
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        string corruptManifest = Path.Combine(steam, "steamapps", "appmanifest_1449850.acf");
        File.WriteAllText(corruptManifest, "AppState { appid");
        WriteLibraryFolders(steam, "\"1\" { \"path\" \"" + EscapePath(library) + "\" }");

        var result = new SteamDiscoveryService().Discover(steam);

        Assert.Equal(game, result.GamePath);
        Assert.Equal("AppState { appid", File.ReadAllText(corruptManifest));
    }

    /// <summary>验证库配置损坏时仍使用主库，且保留原库配置。</summary>
    [Fact]
    public void DiscoverFallsBackToPrimaryLibraryWhenLibraryFoldersIsMalformed()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(folders, "libraryfolders { 1");

        var result = new SteamDiscoveryService().Discover(steam);

        Assert.Equal(game, result.GamePath);
        Assert.Equal("libraryfolders { 1", File.ReadAllText(folders));
    }

    /// <summary>验证主库清单被独占时跳过当前库，继续发现次库。</summary>
    [Fact]
    public void DiscoverSkipsPrimaryManifestWhileItIsExclusivelyLocked()
    {
        string steam = CreateSteamFixture();
        string primaryGame = CreateGameFixture(steam, "Primary Duel");
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");
        string manifest = Path.Combine(steam, "steamapps", "appmanifest_1449850.acf");
        string original = File.ReadAllText(manifest);

        using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
        }

        Assert.Equal(original, File.ReadAllText(manifest));
        Assert.True(File.Exists(Path.Combine(primaryGame, "masterduel.exe")));
    }

    /// <summary>验证附加库配置被独占时回退到主库。</summary>
    [Fact]
    public void DiscoverUsesPrimaryLibraryWhileLibraryFoldersIsExclusivelyLocked()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        WriteLibraryFolders(steam, "1 ignored");
        string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string original = File.ReadAllText(folders);

        using (var locked = new FileStream(folders, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
        }

        Assert.Equal(original, File.ReadAllText(folders));
    }

    /// <summary>验证不匹配的应用清单与缺失安装目录均跳过，继续寻找有效次库。</summary>
    [Theory]
    [InlineData("Other { appid 1449850 installdir Primary }")]
    [InlineData("AppState scalar")]
    [InlineData("AppState { installdir Primary }")]
    [InlineData("AppState { appid 123 installdir Primary }")]
    [InlineData("AppState { appid 1449850 }")]
    [InlineData("AppState { appid 1449850 installdir \"\" }")]
    [InlineData("AppState { appid 1449850 installdir \"   \" }")]
    public void DiscoverSkipsManifestWithoutMatchingAppAndInstallDirectory(string manifest)
    {
        string steam = CreateSteamFixture();
        CreateGameFixture(steam, "Primary");
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1449850.acf"), manifest);
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证清单目录中的父路径与目录分隔符不用于游戏发现。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Nested/Game")]
    [InlineData("Nested\\Game")]
    public void DiscoverRejectsManifestInstallDirectoriesWithTraversalOrSeparators(string installDirectory)
    {
        string steam = CreateSteamFixture();
        string decoy = Path.GetFullPath(Path.Combine(steam, "steamapps", "common", installDirectory));
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, "masterduel.exe"), "decoy");
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1449850.acf"),
            "AppState { appid 1449850 installdir \"" + EscapePath(installDirectory) + "\" }");
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证驱动器相对目录被拒绝，即使后续库存在有效游戏。</summary>
    [Fact]
    public void DiscoverRejectsDriveRelativeManifestInstallDirectory()
    {
        string steam = CreateSteamFixture();
        string driveRelative = Path.GetPathRoot(root)![..2] + "Game";
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1449850.acf"),
            "AppState { appid 1449850 installdir \"" + driveRelative + "\" }");
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证缺少游戏程序的有效清单不会阻断后续库。</summary>
    [Fact]
    public void DiscoverSkipsManifestWhenExecutableIsMissing()
    {
        string steam = CreateSteamFixture();
        string missingGame = CreateGameFixture(steam, "Primary");
        File.Delete(Path.Combine(missingGame, "masterduel.exe"));
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证旧式路径库、无效库条目与重复路径均按真实配置处理。</summary>
    [Fact]
    public void DiscoverAcceptsLegacyLibrariesAndIgnoresInvalidLibraryEntries()
    {
        string steam = CreateSteamFixture();
        string library = Path.Combine(root, "Secondary");
        string game = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam,
            "metadata ignored -1 ignored 2 \"\" 3 relative/path 4 {} 5 { path \"   \" } "
            + "6 \"" + EscapePath(steam) + "\" 7 \"" + EscapePath(library) + "\"");

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证缺失或字符串类型的库根节点仍允许发现主库游戏。</summary>
    [Theory]
    [InlineData("other { 1 ignored }")]
    [InlineData("libraryfolders scalar")]
    public void DiscoverUsesPrimaryLibraryWhenLibraryFoldersRootIsNotAnObject(string folders)
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), folders);

        Assert.Equal(game, new SteamDiscoveryService().Discover(steam).GamePath);
    }

    /// <summary>验证所有库均未安装游戏时返回空游戏目录。</summary>
    [Fact]
    public void DiscoverReturnsEmptyGamePathWhenNoManifestExists()
    {
        string steam = CreateSteamFixture();

        var result = new SteamDiscoveryService().Discover(steam);

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal("", result.GamePath);
        Assert.Empty(result.Accounts);
    }

    /// <summary>验证手工路径可以直接使用 Steam 与游戏可执行文件。</summary>
    [Fact]
    public void DiscoverAcceptsExecutablePathsForBothOverrides()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");

        var result = new SteamDiscoveryService().Discover(Path.Combine(steam, "steam.exe"), Path.Combine(game, "masterduel.exe"));

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal(game, result.GamePath);
    }

    /// <summary>验证手工 Steam 目录必须存在实际 Steam 程序。</summary>
    [Fact]
    public void DiscoverRejectsManualSteamDirectoryWithoutExecutable()
    {
        Assert.Throws<InvalidOperationException>(() => new SteamDiscoveryService().Discover(root));
    }

    /// <summary>验证四种注册表候选中有效的程序文件路径可被发现，其他类型与空值被忽略。</summary>
    [Fact]
    public void DiscoverFindsSteamFromInjectedRegistryViewAndIgnoresInvalidValues()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        var environment = new TestDiscoveryEnvironment(
            (hive, view) => hive == RegistryHive.LocalMachine && view == RegistryView.Registry32
                ? [null, 123, " ", Path.Combine(root, "Missing"), Path.Combine(steam, "steam.exe"), steam]
                : [],
            Path.Combine(root, "MissingFallback"));

        var result = new SteamDiscoveryService(environment).Discover();

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal(game, result.GamePath);
    }

    /// <summary>验证各类注册表读取失败均继续使用默认安装目录。</summary>
    [Theory]
    [InlineData("security")]
    [InlineData("unauthorized")]
    [InlineData("io")]
    public void DiscoverUsesFallbackWhenRegistryViewsAreUnreadable(string failure)
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        var environment = new TestDiscoveryEnvironment((_, _) => throw CreateReadFailure(failure), steam);

        var result = new SteamDiscoveryService(environment).Discover();

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal(game, result.GamePath);
    }

    /// <summary>验证没有有效 Steam 候选时返回空发现结果而不读取账号。</summary>
    [Fact]
    public void DiscoverReturnsEmptyWhenEverySteamCandidateIsMissing()
    {
        var environment = new TestDiscoveryEnvironment((_, _) => [], Path.Combine(root, "Missing"));

        var result = new SteamDiscoveryService(environment).Discover(" ", " ");

        Assert.Equal("", result.SteamPath);
        Assert.Equal("", result.GamePath);
        Assert.Empty(result.Accounts);
    }

    /// <summary>验证只读原生注册表适配器从专属临时键找到 Steam 程序，且测试结束还原键。</summary>
    [Fact]
    public void DiscoverReadsNativeRegistryFromTemporaryTestKey()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        string subKey = @"Software\MasterDuelSwitcherTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                key.SetValue("SteamPath", " ");
                key.SetValue("InstallPath", 123, RegistryValueKind.DWord);
                key.SetValue("SteamExe", Path.Combine(steam, "steam.exe"));
            }

            var environment = new WindowsSteamDiscoveryEnvironment(subKey, Path.Combine(root, "Missing"));
            var result = new SteamDiscoveryService(environment).Discover();

            Assert.Equal(steam, result.SteamPath);
            Assert.Equal(game, result.GamePath);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(subKey, false);
        }
    }

    /// <summary>验证原生注册表适配器遇到缺失键时使用指定默认安装目录。</summary>
    [Fact]
    public void DiscoverUsesNativeFallbackWhenTestRegistryKeyDoesNotExist()
    {
        string steam = CreateSteamFixture();
        string game = CreateGameFixture(steam, "Primary Duel");
        var environment = new WindowsSteamDiscoveryEnvironment(@"Software\MasterDuelSwitcherTests\" + Guid.NewGuid().ToString("N"), steam);

        var result = new SteamDiscoveryService(environment).Discover();

        Assert.Equal(steam, result.SteamPath);
        Assert.Equal(game, result.GamePath);
    }

    /// <summary>验证读取器返回访问拒绝时仍按清单与库配置的回退规则发现游戏。</summary>
    [Theory]
    [InlineData("manifest")]
    [InlineData("folders")]
    public void DiscoverContinuesWhenKnownFileReadIsDenied(string deniedFile)
    {
        string steam = CreateSteamFixture();
        string primary = CreateGameFixture(steam, "Primary Duel");
        string library = Path.Combine(root, "Secondary");
        string secondary = CreateGameFixture(library, "Secondary Duel");
        WriteLibraryFolders(steam, "1 \"" + EscapePath(library) + "\"");
        string deniedPath = Path.Combine(steam, "steamapps", deniedFile == "manifest" ? "appmanifest_1449850.acf" : "libraryfolders.vdf");
        string original = File.ReadAllText(deniedPath);
        Func<string, string> readText = path => path == deniedPath
            ? throw new UnauthorizedAccessException("测试读取器拒绝此临时文件。")
            : File.ReadAllText(path);

        var result = new SteamDiscoveryService(readText: readText).Discover(steam);

        Assert.Equal(deniedFile == "manifest" ? secondary : primary, result.GamePath);
        Assert.Equal(original, File.ReadAllText(deniedPath));
    }

    /// <summary>验证发现不吞掉不属于已知读取失败的程序异常。</summary>
    [Fact]
    public void DiscoverPropagatesUnexpectedRegistryFailure()
    {
        var environment = new TestDiscoveryEnvironment((_, _) => throw new InvalidOperationException("测试异常。"), root);

        Assert.Throws<InvalidOperationException>(() => new SteamDiscoveryService(environment).Discover());
    }

    /// <summary>验证库配置与清单读取不吞掉不属于已知读取失败的程序异常。</summary>
    [Theory]
    [InlineData("appmanifest_1449850.acf")]
    [InlineData("libraryfolders.vdf")]
    public void DiscoverPropagatesUnexpectedFileReadFailure(string failedFile)
    {
        string steam = CreateSteamFixture();
        CreateGameFixture(steam, "Primary Duel");
        WriteLibraryFolders(steam, "1 ignored");
        string failedPath = Path.Combine(steam, "steamapps", failedFile);
        Func<string, string> readText = path => path == failedPath
            ? throw new InvalidOperationException("测试异常。")
            : File.ReadAllText(path);

        Assert.Throws<InvalidOperationException>(() => new SteamDiscoveryService(readText: readText).Discover(steam));
    }

    /// <summary>验证手工游戏目录必须含有实际游戏可执行文件。</summary>
    [Fact]
    public void DiscoverRejectsManualGameDirectoryWithoutExecutable()
    {
        string steam = Path.Combine(root, "Steam");
        string game = Path.Combine(root, "Game");
        Directory.CreateDirectory(steam);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(steam, "steam.exe"), "fixture");

        Assert.Throws<InvalidOperationException>(() => new SteamDiscoveryService().Discover(steam, game));
    }

    /// <summary>验证键名大小写不影响账号读取，并保留转义昵称。</summary>
    [Fact]
    public void ReadAccountsAcceptsCaseInsensitiveKeysAndEscapedNames()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"), "\"UsErS\" { \"76561198000000001\" { \"AccountName\" \"first\" \"PersonaName\" \"Name \\\"One\\\"\" \"RememberPassword\" \"1\" \"AllowAutoLogin\" \"0\" \"MostRecent\" \"1\" } \"not-an-id\" { \"AccountName\" \"ignored\" } }");

        var accounts = new SteamDiscoveryService().ReadAccounts(root);

        var account = Assert.Single(accounts);
        Assert.Equal("76561198000000001", account.SteamId);
        Assert.Equal("Name \"One\"", account.PersonaName);
        Assert.True(account.RememberPassword);
        Assert.True(account.MostRecent);
        Assert.False(account.AllowAutoLogin);
    }

    /// <summary>验证缺少账号配置时返回空账号列表。</summary>
    [Fact]
    public void ReadAccountsReturnsEmptyWhenConfigIsMissing() => Assert.Empty(new SteamDiscoveryService().ReadAccounts(root));

    /// <summary>验证账号根节点缺失或为字符串时返回空账号集合。</summary>
    [Theory]
    [InlineData("other { key value }")]
    [InlineData("users scalar")]
    public void ReadAccountsReturnsEmptyWhenUsersRootIsNotAnObject(string text)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"), text);

        Assert.Empty(new SteamDiscoveryService().ReadAccounts(root));
    }

    /// <summary>验证仅保留合法账号、缺失字段默认值及最近账号优先的昵称排序。</summary>
    [Fact]
    public void ReadAccountsFiltersInvalidEntriesAndSortsRecentThenDisplayName()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"), """
            users {
              76561198000000001 { AccountName zulu PersonaName Zulu MostRecent 1 }
              76561198000000002 { AccountName alpha PersonaName alpha MostRecent 1 }
              76561198000000003 { AccountName bravo }
              76561198000000004 { AccountName empty PersonaName "" RememberPassword true AllowAutoLogin 1 MostRecent 0 }
              76561198000000005 { PersonaName missing }
              76561198000000006 { AccountName "   " }
              76561198000000007 scalar
              7656119800000000 { AccountName short }
              765611980000000000 { AccountName long }
              7656119800000000x { AccountName nondigit }
            }
            """);

        var accounts = new SteamDiscoveryService().ReadAccounts(root);

        Assert.Equal(new[] { "alpha", "zulu", "bravo", "empty" }, accounts.Select(account => account.AccountName));
        Assert.Equal("bravo", accounts[2].DisplayName);
        Assert.False(accounts[2].RememberPassword);
        Assert.False(accounts[2].AllowAutoLogin);
        Assert.False(accounts[2].MostRecent);
        Assert.False(accounts[3].RememberPassword);
        Assert.True(accounts[3].AllowAutoLogin);
    }

    /// <summary>验证时间戳只读解析为非负整数秒，缺失、非法及越界值使用零。</summary>
    [Theory]
    [InlineData("1712345678", 1712345678L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0", 0L)]
    [InlineData(null, 0L)]
    [InlineData("", 0L)]
    [InlineData(" ", 0L)]
    [InlineData("-1", 0L)]
    [InlineData("not-a-timestamp", 0L)]
    [InlineData("1712345678.5", 0L)]
    [InlineData("9223372036854775808", 0L)]
    [InlineData("+1712345678", 0L)]
    public void ReadAccountsParsesTimestampWithoutChangingLoginConfiguration(string? timestamp, long expectedSeconds)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        string loginFile = Path.Combine(root, "config", "loginusers.vdf");
        string timestampEntry = timestamp is null ? "" : "Timestamp \"" + timestamp + "\"";
        string original = "users { 76561198000000001 { AccountName account " + timestampEntry + " } }";
        File.WriteAllText(loginFile, original);

        var account = Assert.Single(new SteamDiscoveryService().ReadAccounts(root));

        Assert.Equal(expectedSeconds, account.LastLoginTimestamp);
        Assert.Equal(original, File.ReadAllText(loginFile));
    }

    /// <summary>验证对象类型的非法时间戳不作为登录时间。</summary>
    [Fact]
    public void ReadAccountsTreatsObjectTimestampAsMissing()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"),
            "users { 76561198000000001 { AccountName account Timestamp { nested ignored } } }");

        Assert.Equal(0L, Assert.Single(new SteamDiscoveryService().ReadAccounts(root)).LastLoginTimestamp);
    }

    /// <summary>验证最近账号优先，再按时间降序、显示名及 SteamId 排出稳定顺序。</summary>
    [Fact]
    public void ReadAccountsOrdersRecentThenNewestTimestampThenDisplayNameAndSteamId()
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "loginusers.vdf"), """
            users {
              76561198000000004 { AccountName fourth PersonaName ALPHA Timestamp 300 }
              76561198000000005 { AccountName fifth PersonaName Zulu Timestamp 300 }
              76561198000000008 { AccountName alpha-recent-old MostRecent 1 Timestamp 100 }
              76561198000000001 { AccountName no-time }
              76561198000000003 { AccountName third PersonaName alpha Timestamp 300 }
              76561198000000002 { AccountName bravo Timestamp 300 }
              76561198000000006 { AccountName newest Timestamp 900 }
              76561198000000007 { AccountName zulu-recent-new MostRecent 1 Timestamp 200 }
            }
            """);

        var accounts = new SteamDiscoveryService().ReadAccounts(root);

        Assert.Equal(new[]
        {
            "76561198000000007", "76561198000000008", "76561198000000006", "76561198000000003",
            "76561198000000004", "76561198000000002", "76561198000000005", "76561198000000001"
        }, accounts.Select(account => account.SteamId));
        Assert.Equal(new[] { 200L, 100L, 900L, 300L, 300L, 300L, 300L, 0L }, accounts.Select(account => account.LastLoginTimestamp));
    }

    /// <summary>验证账号读取拒绝缺失的安装目录参数。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ReadAccountsRejectsMissingSteamPath(string? steamPath)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SteamDiscoveryService().ReadAccounts(steamPath!));
    }

    /// <summary>创建仅包含安装结构的真实临时 Steam 目录。</summary>
    private string CreateSteamFixture()
    {
        string steam = Path.Combine(root, "Steam");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "steam.exe"), "fixture");
        return steam;
    }

    /// <summary>创建真实临时游戏可执行文件与匹配的安装清单。</summary>
    private static string CreateGameFixture(string library, string installDirectory)
    {
        string game = Path.Combine(library, "steamapps", "common", installDirectory);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "masterduel.exe"), "fixture");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1449850.acf"),
            "\"AppState\" { \"appid\" \"1449850\" \"installdir\" \"" + installDirectory + "\" }");
        return game;
    }

    /// <summary>写入指定子项的真实临时 Steam 库配置。</summary>
    private static void WriteLibraryFolders(string steam, string children) =>
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { " + children + " }");

    /// <summary>转义 VDF 引号路径中的反斜杠。</summary>
    private static string EscapePath(string path) => path.Replace("\\", "\\\\");

    /// <summary>创建模拟只读环境所需的指定读取异常。</summary>
    private static Exception CreateReadFailure(string failure) => failure switch
    {
        "security" => new SecurityException("测试注册表安全异常。"),
        "unauthorized" => new UnauthorizedAccessException("测试注册表权限异常。"),
        _ => new IOException("测试注册表读取异常。")
    };

    /// <summary>向实际发现逻辑提供可控注册表候选和默认目录。</summary>
    private sealed class TestDiscoveryEnvironment : ISteamDiscoveryEnvironment
    {
        /// <summary>生成各注册表根与视图候选的测试读取器。</summary>
        private readonly Func<RegistryHive, RegistryView, IReadOnlyList<object?>> registryValues;

        /// <summary>创建只读测试环境。</summary>
        public TestDiscoveryEnvironment(Func<RegistryHive, RegistryView, IReadOnlyList<object?>> registryValues, string fallbackSteamDirectory)
        {
            this.registryValues = registryValues;
            FallbackSteamDirectory = fallbackSteamDirectory;
        }

        /// <summary>发现注册表候选失败时使用的真实临时目录。</summary>
        public string FallbackSteamDirectory { get; }

        /// <summary>返回指定根与视图对应的测试候选。</summary>
        public IReadOnlyList<object?> ReadRegistryValues(RegistryHive hive, RegistryView view) => registryValues(hive, view);
    }

    /// <summary>清理每次测试独占的临时目录。</summary>
    public void Dispose() => Directory.Delete(root, true);
}
