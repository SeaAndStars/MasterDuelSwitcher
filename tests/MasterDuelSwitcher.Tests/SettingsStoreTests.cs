using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>使用真实 SQLite 验证账号和偏好的持久化、事务及损坏保护。</summary>
public sealed class SettingsStoreTests : IDisposable
{
    /// <summary>当前测试独占的临时目录。</summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdswitch-sqlite-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建当前测试的独立目录。</summary>
    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    /// <summary>首次读取应创建 SQLite 并返回默认偏好及空账号集合。</summary>
    [Fact]
    public void FirstReadCreatesSqliteDatabaseWithDefaults()
    {
        ISettingsStore store = new SettingsStore(Path.Combine(_directory, "data", "..", "data"));
        var settings = store.Load();
        Assert.Equal(Path.Combine(_directory, "data"), store.StateDirectory);
        Assert.Empty(settings.SteamPath);
        Assert.Empty(settings.GamePath);
        Assert.Empty(settings.SourceProfile);
        Assert.False(settings.DarkTheme);
        Assert.Empty(settings.AccountBindings);
        Assert.Empty(settings.AccountNotes);
        Assert.Empty(settings.HiddenAccounts);
        Assert.Empty(store.GetAccounts());
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(store.StateDirectory, "accounts.db")), 0, 16));
        Assert.Empty(Directory.GetFiles(store.StateDirectory, "*.json"));
        using var database = OpenDatabase(store.StateDirectory);
        using var query = database.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM settings";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    /// <summary>中文、引号和 SQL 注入文本应原样往返且分别存入关系字段。</summary>
    [Fact]
    public void RoundtripPreservesUnicodeAndInjectionStrings()
    {
        var store = new SettingsStore(_directory);
        const string accountId = "76561198000000000'; DROP TABLE accounts;--";
        store.Save(new AppSettings
        {
            SteamPath = "D:\\游戏库\\Steam';--", GamePath = "E:\\游戏\\大师决斗",
            SourceProfile = "源目录'; DROP TABLE settings;--", DarkTheme = true,
            AccountNotes = new() { [accountId] = "中文备注 ' ; DROP TABLE accounts;--", ["note-only"] = "仅备注" },
            AccountBindings = new() { [accountId] = "资源映射';--", ["binding-only"] = "a1b2c3d4" },
            HiddenAccounts = [accountId, "hidden-only"]
        });
        var loaded = new SettingsStore(_directory).Load();
        Assert.Equal("D:\\游戏库\\Steam';--", loaded.SteamPath);
        Assert.Equal("E:\\游戏\\大师决斗", loaded.GamePath);
        Assert.Equal("源目录'; DROP TABLE settings;--", loaded.SourceProfile);
        Assert.True(loaded.DarkTheme);
        Assert.Equal("中文备注 ' ; DROP TABLE accounts;--", loaded.AccountNotes[accountId]);
        Assert.Equal("仅备注", loaded.AccountNotes["note-only"]);
        Assert.Equal("资源映射';--", loaded.AccountBindings[accountId]);
        Assert.Equal("a1b2c3d4", loaded.AccountBindings["binding-only"]);
        Assert.Equal(2, loaded.HiddenAccounts.Count);
        Assert.Contains("hidden-only", loaded.HiddenAccounts);
        Assert.Empty(store.GetAccounts());
        Assert.Single(Directory.GetFiles(_directory));
        using var database = OpenDatabase(_directory);
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Notes || '|' || ResourceFolder || '|' || Hidden FROM accounts WHERE SteamId = $id";
        query.Parameters.AddWithValue("$id", accountId);
        Assert.Equal("中文备注 ' ; DROP TABLE accounts;--|资源映射';--|1", query.ExecuteScalar());
    }

    /// <summary>重复保存应清除已撤销的备注、绑定和隐藏状态并保留账号元数据。</summary>
    [Fact]
    public void RepeatedSaveReplacesPreferencesWithoutDeletingAccounts()
    {
        var store = new SettingsStore(_directory);
        store.SynchronizeAccounts([new SteamAccount { SteamId = "1", AccountName = "account", PersonaName = "昵称" }]);
        store.Save(new AppSettings { SourceProfile = "11223344", DarkTheme = true, AccountNotes = new() { ["1"] = "旧备注" }, AccountBindings = new() { ["1"] = "aabbccdd" }, HiddenAccounts = ["1"] });
        store.Save(new AppSettings { SourceProfile = "aabbccdd" });
        var loaded = store.Load();
        Assert.Equal("aabbccdd", loaded.SourceProfile);
        Assert.False(loaded.DarkTheme);
        Assert.Empty(loaded.AccountNotes);
        Assert.Empty(loaded.AccountBindings);
        Assert.Empty(loaded.HiddenAccounts);
        Assert.Equal("昵称", Assert.Single(store.GetAccounts()).PersonaName);
    }

    /// <summary>Steam 刷新应更新元数据、按最近账号排序并保留人工备注及资源映射。</summary>
    [Fact]
    public void SynchronizationPreservesUserDataAndOrdersRecentAccountFirst()
    {
        var store = new SettingsStore(_directory);
        store.SynchronizeAccounts([
            new SteamAccount { SteamId = "1", AccountName = "old", PersonaName = "旧昵称", MostRecent = true },
            new SteamAccount { SteamId = "2", AccountName = "second", RememberPassword = true, AllowAutoLogin = true }
        ]);
        store.Save(new AppSettings { AccountNotes = new() { ["1"] = "移除后也保留", ["2"] = "账号备注" }, AccountBindings = new() { ["2"] = "1234abcd" }, HiddenAccounts = ["2"] });
        store.SynchronizeAccounts([
            new SteamAccount { SteamId = "3", AccountName = "third", PersonaName = "第三个" },
            new SteamAccount { SteamId = "2", AccountName = "更新';--", PersonaName = "更新中文", RememberPassword = true, AllowAutoLogin = true, MostRecent = true },
            new SteamAccount { SteamId = "4", AccountName = "fourth" }
        ]);
        var accounts = store.GetAccounts();
        Assert.Equal(["2", "3", "4"], accounts.Select(account => account.SteamId));
        Assert.Equal("更新';--", accounts[0].AccountName);
        Assert.Equal("更新中文", accounts[0].PersonaName);
        Assert.True(accounts[0].RememberPassword);
        Assert.True(accounts[0].AllowAutoLogin);
        Assert.True(accounts[0].MostRecent);
        Assert.False(accounts[1].RememberPassword);
        Assert.False(accounts[1].AllowAutoLogin);
        Assert.False(accounts[1].MostRecent);
        var loaded = store.Load();
        Assert.Equal("移除后也保留", loaded.AccountNotes["1"]);
        Assert.Equal("账号备注", loaded.AccountNotes["2"]);
        Assert.Equal("1234abcd", loaded.AccountBindings["2"]);
        Assert.Contains("2", loaded.HiddenAccounts);
        using var database = OpenDatabase(_directory);
        using var query = database.CreateCommand();
        query.CommandText = "SELECT Present FROM accounts WHERE SteamId = '1'";
        Assert.Equal(0L, query.ExecuteScalar());
        store.SynchronizeAccounts([]);
        Assert.Empty(store.GetAccounts());
        Assert.Equal("移除后也保留", store.Load().AccountNotes["1"]);
    }

    /// <summary>保存过程中 SQL 失败应回滚偏好和全部账号字段。</summary>
    [Fact]
    public void FailedSaveRollsBackPreferencesAndAccountChanges()
    {
        var store = new SettingsStore(_directory);
        store.Save(new AppSettings { SteamPath = "原路径", AccountNotes = new() { ["1"] = "原备注" }, AccountBindings = new() { ["1"] = "原映射" }, HiddenAccounts = ["1"] });
        ExecuteSql("CREATE TRIGGER reject_note BEFORE UPDATE OF Notes ON accounts WHEN NEW.Notes = 'reject' BEGIN SELECT RAISE(ABORT, 'fixture rejection'); END;");
        Assert.Throws<InvalidDataException>(() => store.Save(new AppSettings { SteamPath = "被回滚的路径", AccountNotes = new() { ["1"] = "reject" } }));
        var loaded = store.Load();
        Assert.Equal("原路径", loaded.SteamPath);
        Assert.Equal("原备注", loaded.AccountNotes["1"]);
        Assert.Equal("原映射", loaded.AccountBindings["1"]);
        Assert.Contains("1", loaded.HiddenAccounts);
    }

    /// <summary>账号同步失败应回滚先前的 Present 标记及同批新增账号。</summary>
    [Fact]
    public void FailedSynchronizationRollsBackPresenceAndEarlierRows()
    {
        var store = new SettingsStore(_directory);
        store.SynchronizeAccounts([new SteamAccount { SteamId = "1", AccountName = "original" }]);
        ExecuteSql("CREATE TRIGGER reject_account BEFORE INSERT ON accounts WHEN NEW.AccountName = 'reject' BEGIN SELECT RAISE(ABORT, 'fixture rejection'); END;");
        Assert.Throws<InvalidDataException>(() => store.SynchronizeAccounts([
            new SteamAccount { SteamId = "2", AccountName = "earlier" },
            new SteamAccount { SteamId = "3", AccountName = "reject" }
        ]));
        Assert.Equal("1", Assert.Single(store.GetAccounts()).SteamId);
    }

    /// <summary>损坏数据库的读写均应报告错误且保持原文件字节不变。</summary>
    [Fact]
    public void CorruptDatabaseIsReportedAndPreservedForEveryOperation()
    {
        byte[] original = [0x43, 0x4f, 0x52, 0x52, 0x55, 0x50, 0x54];
        var path = Path.Combine(_directory, "accounts.db");
        File.WriteAllBytes(path, original);
        var store = new SettingsStore(_directory);
        Assert.IsType<SqliteException>(Assert.Throws<InvalidDataException>(() => store.Load()).InnerException);
        Assert.Throws<InvalidDataException>(() => store.Save(new AppSettings()));
        Assert.Throws<InvalidDataException>(() => store.SynchronizeAccounts([]));
        Assert.Throws<InvalidDataException>(() => store.GetAccounts());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    /// <summary>程序目录移除和迁移后，独立系统数据目录中的账号数据应继续可读。</summary>
    [Fact]
    public void DatabaseSurvivesDeletingAndRelocatingExecutableDirectory()
    {
        var executableDirectory = Path.Combine(_directory, "installation");
        var dataDirectory = Path.Combine(_directory, "LocalAppData", "MasterDuelSwitcher");
        Directory.CreateDirectory(executableDirectory);
        File.WriteAllText(Path.Combine(executableDirectory, "MasterDuelSwitcher.exe"), "fixture");
        new SettingsStore(dataDirectory).Save(new AppSettings { AccountNotes = new() { ["1"] = "独立数据" } });
        Directory.Delete(executableDirectory, true);
        Directory.CreateDirectory(Path.Combine(_directory, "relocated"));
        Assert.Equal("独立数据", new SettingsStore(dataDirectory).Load().AccountNotes["1"]);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "accounts.db")));
    }

    /// <summary>空对象参数应在修改数据库之前被拒绝。</summary>
    [Fact]
    public void NullArgumentsDoNotCreateDatabase()
    {
        var store = new SettingsStore(_directory);
        Assert.Throws<ArgumentNullException>(() => store.Save(null!));
        Assert.Throws<ArgumentNullException>(() => store.SynchronizeAccounts(null!));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    /// <summary>空值偏好和集合应按默认值保存，避免外部配置产生空引用。</summary>
    [Fact]
    public void NullValuesAreNormalizedToDefaults()
    {
        var store = new SettingsStore(_directory);
        store.Save(new AppSettings { SteamPath = null!, GamePath = null!, SourceProfile = null!, AccountNotes = null!, AccountBindings = null!, HiddenAccounts = null! });
        var loaded = store.Load();
        Assert.Empty(loaded.SteamPath);
        Assert.Empty(loaded.GamePath);
        Assert.Empty(loaded.SourceProfile);
        Assert.Empty(loaded.AccountNotes);
        Assert.Empty(loaded.AccountBindings);
        Assert.Empty(loaded.HiddenAccounts);
    }

    /// <summary>所有成功数据库操作应记录 Debug 开始及完成，并且不记录账号正文。</summary>
    [Fact]
    public void SuccessfulOperationsLogDebugPhasesWithoutAccountData()
    {
        var logger = new RecordingLogger();
        var store = new SettingsStore(_directory, logger);
        store.Load();
        store.Save(new AppSettings { AccountNotes = new() { ["private-id"] = "private-note" }, AccountBindings = new() { ["private-id"] = "private-binding" } });
        store.SynchronizeAccounts([new SteamAccount { SteamId = "private-id", AccountName = "private-name", PersonaName = "private-persona" }]);
        store.GetAccounts();
        Assert.Equal(8, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        foreach (var operation in new[] { "Load", "Save", "SynchronizeAccounts", "GetAccounts" })
        {
            Assert.Contains(logger.Entries, entry => entry.Message.Contains(operation, StringComparison.Ordinal) && entry.Message.Contains("开始", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains(operation, StringComparison.Ordinal) && entry.Message.Contains("完成", StringComparison.Ordinal));
        }
        var text = string.Join("\n", logger.Entries.Select(entry => entry.Message));
        foreach (var secret in new[] { "private-id", "private-note", "private-binding", "private-name", "private-persona" }) Assert.DoesNotContain(secret, text);
    }

    /// <summary>损坏数据库应记录 Error 与原始异常栈，同时保留数据库字节。</summary>
    [Fact]
    public void CorruptDatabaseLogsOriginalExceptionAndKeepsOriginalFile()
    {
        var logger = new RecordingLogger();
        byte[] original = [0x42, 0x41, 0x44];
        var path = Path.Combine(_directory, "accounts.db");
        File.WriteAllBytes(path, original);
        var store = new SettingsStore(_directory, logger);
        var reported = Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Equal(2, logger.Entries.Count);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(reported.InnerException, error.Exception);
        Assert.False(string.IsNullOrEmpty(error.Exception!.StackTrace));
        Assert.Contains("Load", error.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    /// <summary>文件系统异常也应记录 Error 与异常栈，并保持原来的异常语义。</summary>
    [Fact]
    public void FileSystemFailureLogsExceptionWithoutChangingReportedType()
    {
        var logger = new RecordingLogger();
        var occupiedPath = Path.Combine(_directory, "occupied");
        File.WriteAllText(occupiedPath, "retained");
        var store = new SettingsStore(occupiedPath, logger);
        var reported = Assert.Throws<IOException>(() => store.Load());
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(reported, error.Exception);
        Assert.False(string.IsNullOrEmpty(error.Exception!.StackTrace));
        Assert.Equal("retained", File.ReadAllText(occupiedPath));
    }

    /// <summary>只在测试中捕获日志内容与异常，数据库操作仍使用真实 SQLite。</summary>
    private sealed class RecordingLogger : ILogger<SettingsStore>
    {
        /// <summary>已记录的级别、公开消息和原始异常。</summary>
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        /// <summary>测试无需创建日志作用域。</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>启用全部日志级别以验证实际调用。</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>保存由日志扩展方法格式化的公开消息和原始异常。</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    /// <summary>打开临时数据库供测试直接核实关系字段和制造真实事务错误。</summary>
    private static SqliteConnection OpenDatabase(string directory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "accounts.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>执行测试夹具 SQL，不替代被测存储实现。</summary>
    private void ExecuteSql(string sql)
    {
        using var database = OpenDatabase(_directory);
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>仅清理当前测试创建的目录。</summary>
    public void Dispose() => Directory.Delete(_directory, true);
}
