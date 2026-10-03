using MasterDuelSwitcher.Core.Models;
using Microsoft.Data.Sqlite;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>以 SQLite 事务保存独立系统数据目录中的偏好和账号关系数据。</summary>
public sealed class SettingsStore : ISettingsStore
{
    /// <summary>可重复执行的关系表结构及默认偏好行，不保存密码或令牌。</summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS settings (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            SteamPath TEXT NOT NULL DEFAULT '',
            GamePath TEXT NOT NULL DEFAULT '',
            SourceProfile TEXT NOT NULL DEFAULT '',
            DarkTheme INTEGER NOT NULL DEFAULT 0
        );
        INSERT OR IGNORE INTO settings (Id) VALUES (1);
        CREATE TABLE IF NOT EXISTS accounts (
            SteamId TEXT PRIMARY KEY NOT NULL,
            AccountName TEXT NOT NULL DEFAULT '',
            PersonaName TEXT NOT NULL DEFAULT '',
            RememberPassword INTEGER NOT NULL DEFAULT 0,
            AllowAutoLogin INTEGER NOT NULL DEFAULT 0,
            MostRecent INTEGER NOT NULL DEFAULT 0,
            Notes TEXT NOT NULL DEFAULT '',
            ResourceFolder TEXT NOT NULL DEFAULT '',
            Hidden INTEGER NOT NULL DEFAULT 0,
            Present INTEGER NOT NULL DEFAULT 0
        );
        """;

    /// <summary>指向固定数据目录的连接字符串，关闭池化以便即时释放文件句柄。</summary>
    private readonly string _connectionString;

    /// <summary>数据库和工具状态所在的规范化完整目录，与 EXE 所在位置独立。</summary>
    public string StateDirectory { get; }

    /// <summary>初始化数据库位置；首次实际访问时创建目录和关系表。</summary>
    public SettingsStore(string stateDirectory)
    {
        StateDirectory = Path.GetFullPath(stateDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(StateDirectory, "accounts.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    /// <summary>在同一事务快照中读取偏好和人工账号字段，损坏数据库报告错误。</summary>
    public AppSettings Load() => AccessDatabase(connection =>
    {
        var settings = new AppSettings();
        using var transaction = connection.BeginTransaction();
        using (var command = CreateCommand(connection, transaction,
            "SELECT SteamPath, GamePath, SourceProfile, DarkTheme FROM settings WHERE Id = 1"))
        {
            using var reader = command.ExecuteReader();
            reader.Read();
            settings.SteamPath = reader.GetString(0);
            settings.GamePath = reader.GetString(1);
            settings.SourceProfile = reader.GetString(2);
            settings.DarkTheme = reader.GetBoolean(3);
        }
        using (var command = CreateCommand(connection, transaction,
            "SELECT SteamId, Notes, ResourceFolder, Hidden FROM accounts WHERE Notes <> '' OR ResourceFolder <> '' OR Hidden <> 0"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var steamId = reader.GetString(0);
                var notes = reader.GetString(1);
                var resourceFolder = reader.GetString(2);
                if (notes.Length > 0) settings.AccountNotes[steamId] = notes;
                if (resourceFolder.Length > 0) settings.AccountBindings[steamId] = resourceFolder;
                if (reader.GetBoolean(3)) settings.HiddenAccounts.Add(steamId);
            }
        }
        transaction.Commit();
        return settings;
    });

    /// <summary>原子保存偏好、备注、映射及隐藏状态，并保留 Steam 元数据及存在标记。</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var notes = settings.AccountNotes ?? [];
        var bindings = settings.AccountBindings ?? [];
        var hidden = settings.HiddenAccounts ?? [];
        AccessDatabase(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var command = CreateCommand(connection, transaction, """
                UPDATE settings SET SteamPath = $steam, GamePath = $game,
                    SourceProfile = $source, DarkTheme = $dark WHERE Id = 1;
                UPDATE accounts SET Notes = '', ResourceFolder = '', Hidden = 0;
                """))
            {
                command.Parameters.AddWithValue("$steam", settings.SteamPath ?? "");
                command.Parameters.AddWithValue("$game", settings.GamePath ?? "");
                command.Parameters.AddWithValue("$source", settings.SourceProfile ?? "");
                command.Parameters.AddWithValue("$dark", settings.DarkTheme);
                command.ExecuteNonQuery();
            }
            foreach (var steamId in notes.Keys.Concat(bindings.Keys).Concat(hidden).Distinct(StringComparer.Ordinal))
            {
                using var command = CreateCommand(connection, transaction, """
                    INSERT INTO accounts (SteamId, Notes, ResourceFolder, Hidden)
                    VALUES ($id, $notes, $folder, $hidden)
                    ON CONFLICT (SteamId) DO UPDATE SET
                        Notes = excluded.Notes, ResourceFolder = excluded.ResourceFolder, Hidden = excluded.Hidden;
                    """);
                command.Parameters.AddWithValue("$id", steamId);
                command.Parameters.AddWithValue("$notes", notes.GetValueOrDefault(steamId, ""));
                command.Parameters.AddWithValue("$folder", bindings.GetValueOrDefault(steamId, ""));
                command.Parameters.AddWithValue("$hidden", hidden.Contains(steamId));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        });
    }

    /// <summary>事务同步 Steam 元数据；移除账号仅标记缺席，不删除备注和映射。</summary>
    public void SynchronizeAccounts(IReadOnlyList<SteamAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        AccessDatabase(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var command = CreateCommand(connection, transaction, "UPDATE accounts SET Present = 0"))
            {
                command.ExecuteNonQuery();
            }
            foreach (var account in accounts)
            {
                using var command = CreateCommand(connection, transaction, """
                    INSERT INTO accounts (SteamId, AccountName, PersonaName, RememberPassword, AllowAutoLogin, MostRecent, Present)
                    VALUES ($id, $name, $persona, $remember, $auto, $recent, 1)
                    ON CONFLICT (SteamId) DO UPDATE SET AccountName = excluded.AccountName,
                        PersonaName = excluded.PersonaName, RememberPassword = excluded.RememberPassword,
                        AllowAutoLogin = excluded.AllowAutoLogin, MostRecent = excluded.MostRecent, Present = 1;
                    """);
                command.Parameters.AddWithValue("$id", account.SteamId);
                command.Parameters.AddWithValue("$name", account.AccountName);
                command.Parameters.AddWithValue("$persona", account.PersonaName);
                command.Parameters.AddWithValue("$remember", account.RememberPassword);
                command.Parameters.AddWithValue("$auto", account.AllowAutoLogin);
                command.Parameters.AddWithValue("$recent", account.MostRecent);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        });
    }

    /// <summary>读取当前仍存在的账号；最近使用账号优先，同优先级按账号标识排序。</summary>
    public IReadOnlyList<SteamAccount> GetAccounts() => AccessDatabase<IReadOnlyList<SteamAccount>>(connection =>
    {
        var accounts = new List<SteamAccount>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SteamId, AccountName, PersonaName, RememberPassword, AllowAutoLogin, MostRecent
            FROM accounts WHERE Present = 1 ORDER BY MostRecent DESC, SteamId ASC
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            accounts.Add(new SteamAccount
            {
                SteamId = reader.GetString(0), AccountName = reader.GetString(1), PersonaName = reader.GetString(2),
                RememberPassword = reader.GetBoolean(3), AllowAutoLogin = reader.GetBoolean(4), MostRecent = reader.GetBoolean(5)
            });
        }
        return accounts;
    });

    /// <summary>打开连接并事务初始化结构；任何 SQLite 错误均向上报告且保留原始数据库。</summary>
    private T AccessDatabase<T>(Func<SqliteConnection, T> operation)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using (var transaction = connection.BeginTransaction())
            {
                using var command = CreateCommand(connection, transaction, Schema);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            return operation(connection);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("账号数据库访问失败，原始数据已保留。请检查 accounts.db。", exception);
        }
    }

    /// <summary>创建绑定给定事务的 SQL 命令，数据值由调用方逐项参数化传入。</summary>
    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
