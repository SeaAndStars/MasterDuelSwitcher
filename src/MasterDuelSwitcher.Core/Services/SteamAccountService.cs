using MasterDuelSwitcher.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>提供 Steam 账号切换与登录配置备份还原功能。</summary>
public interface ISteamAccountService
{
    /// <summary>切换已记住账号并请求启动 Master Duel。</summary>
    Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default);
    /// <summary>还原指定 Steam 目录最近的登录配置备份。</summary>
    Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default);
}

/// <summary>为 Steam 进程与登录注册表提供可替换的系统边界。</summary>
public interface ISteamPlatform
{
    /// <summary>检测 Master Duel 是否仍在运行。</summary>
    bool IsGameRunning();
    /// <summary>请求 Steam 正常退出，并在有限时间内等待退出。</summary>
    Task ShutdownSteamAsync(string steamPath, CancellationToken cancellationToken);
    /// <summary>读取自动登录所需的原始注册表值。</summary>
    IReadOnlyList<SteamRegistryValue> ReadLoginRegistry();
    /// <summary>按保存的存在状态和类型写回自动登录注册表值。</summary>
    void WriteLoginRegistry(IReadOnlyList<SteamRegistryValue> values);
    /// <summary>启动 Steam 并请求运行 Master Duel。</summary>
    Task LaunchGameAsync(string steamPath, CancellationToken cancellationToken);
}

/// <summary>以本地备份事务切换 Steam 已记住的账号并启动游戏。</summary>
public sealed class SteamAccountService : ISteamAccountService
{
    /// <summary>登录配置备份与排他锁的绝对根目录。</summary>
    private readonly string stateDirectory;
    /// <summary>正常退出、启动与注册表操作的系统边界。</summary>
    private readonly ISteamPlatform platform;
    /// <summary>只记录操作阶段、事务标识与异常，不记录登录配置正文。</summary>
    private readonly ILogger<SteamAccountService> logger;
    /// <summary>事务清单使用便于检查的 JSON 格式。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>创建使用指定备份目录和系统边界的切号服务。</summary>
    public SteamAccountService(string stateDirectory, ISteamPlatform? platform = null, ILogger<SteamAccountService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.platform = platform ?? new WindowsSteamPlatform();
        this.logger = logger ?? NullLogger<SteamAccountService>.Instance;
    }

    /// <summary>正常退出 Steam，备份并切换配置，然后请求启动 Master Duel。</summary>
    public async Task<string> SwitchAndLaunchAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("开始 Steam 账号切换操作。");
        try
        {
            string result = await SwitchCoreAsync(steamPath, account, cancellationToken);
            logger.LogInformation("Steam 账号切换配置已完成并已发送游戏启动请求。");
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Steam 账号切换操作失败。");
            throw;
        }
    }

    /// <summary>执行已记住账号的受保护配置事务，保留原有失败还原行为。</summary>
    private async Task<string> SwitchCoreAsync(string steamPath, SteamAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        string steam = ValidateSteamPath(steamPath);
        EnsureGameStopped();
        using var transactionLock = AcquireTransactionLock();
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("正在请求 Steam 正常退出，配置尚未写入。");
        await platform.ShutdownSteamAsync(steam, cancellationToken);
        EnsureGameStopped();
        RecoverInterruptedTransaction(steam);

        string loginFile = Path.Combine(steam, "config", "loginusers.vdf");
        if (!File.Exists(loginFile))
            throw new InvalidOperationException("Steam 尚未创建账号配置，请先在 Steam 中登录并记住账号。");
        byte[] original = File.ReadAllBytes(loginFile);
        var document = ParseLoginBytes(original);
        var users = document.Find("users");
        var matches = users?.Children.Where(node => IsAccountNode(node) && string.Equals(node.Key, account.SteamId, StringComparison.Ordinal)).ToArray() ?? [];
        if (matches.Length > 1)
            throw new InvalidOperationException("Steam 登录配置中出现重复账号标识，请先修复配置再切换。");
        var selected = matches.SingleOrDefault();
        if (selected is null)
            throw new InvalidOperationException("所选账号已不在当前 Steam 登录配置中，请刷新账号列表。");
        string accountName = selected.Find("AccountName")!.Value!;
        if (!string.Equals(account.AccountName, accountName, StringComparison.Ordinal))
            throw new InvalidOperationException("所选账号信息已变化，请刷新账号列表。");

        cancellationToken.ThrowIfCancellationRequested();
        string id = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N");
        string backupDirectory = Path.Combine(stateDirectory, "steam-login-backups", id);
        Directory.CreateDirectory(backupDirectory);
        var backup = new SteamLoginBackup
        {
            Id = id,
            SteamPath = steam,
            CreatedUtc = DateTimeOffset.UtcNow,
            OriginalSha256 = Convert.ToHexString(SHA256.HashData(original)),
            RegistryValues = platform.ReadLoginRegistry().ToList()
        };
        ValidateRegistryValues(backup.RegistryValues);
        AtomicWrite(Path.Combine(backupDirectory, "loginusers.vdf"), original);
        WriteManifest(backupDirectory, backup);
        logger.LogDebug("登录配置备份已保存，事务 {TransactionId} 进入准备状态。", id);

        bool launchRequested = false;
        bool launchSucceeded = false;
        try
        {
            foreach (var user in users!.Children.Where(IsAccountNode))
                SetAllValues(user, "MostRecent", ReferenceEquals(user, selected) ? "1" : "0");
            SetAllValues(selected, "AllowAutoLogin", "1");
            AtomicWrite(loginFile, Encoding.UTF8.GetBytes(document.Serialize()));
            platform.WriteLoginRegistry([
                new() { Name = "AutoLoginUser", Exists = true, Kind = RegistryValueKind.String, Text = accountName },
                new() { Name = "RememberPassword", Exists = true, Kind = RegistryValueKind.DWord, Number = 1 }
            ]);
            backup.Status = "applied";
            WriteManifest(backupDirectory, backup);
            logger.LogDebug("登录选择配置已应用，事务 {TransactionId} 将发送 AppID 1449850 启动请求。", id);
            launchRequested = true;
            await platform.LaunchGameAsync(steam, cancellationToken);
            launchSucceeded = true;
            logger.LogDebug("Steam 已接收游戏启动请求，事务 {TransactionId} 正在提交完成状态。", id);
            backup.Status = "launched";
            WriteManifest(backupDirectory, backup);
            return "已请求 Steam 启动 Master Duel；登录及验证状态以 Steam 提示为准。";
        }
        catch (Exception operationError)
        {
            if (launchSucceeded)
            {
                logger.LogWarning("事务 {TransactionId} 在启动请求完成后记录失败，已保留当前配置。", id);
                throw new InvalidOperationException("Steam 已收到启动请求，但备份状态记录失败；当前登录配置已保留，请先正常退出游戏再使用备份还原。", operationError);
            }
            try
            {
                logger.LogDebug("事务 {TransactionId} 在启动完成前失败，开始自动还原。", id);
                if (launchRequested)
                {
                    EnsureGameStopped();
                    await platform.ShutdownSteamAsync(steam, CancellationToken.None);
                    EnsureGameStopped();
                }
                RestoreBackup(backupDirectory, backup, steam, "rolled-back");
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("切号失败，自动还原也发生错误；请保留本地备份并使用备份还原。", operationError, rollbackError);
            }
            throw;
        }
    }

    /// <summary>还原指定 Steam 安装目录最近的有效登录配置备份。</summary>
    public async Task<string> RestoreLatestAsync(string steamPath, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("开始 Steam 登录配置还原操作。");
        try
        {
            string result = await RestoreCoreAsync(steamPath, cancellationToken);
            logger.LogInformation("Steam 登录配置还原操作已完成。");
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Steam 登录配置还原操作失败。");
            throw;
        }
    }

    /// <summary>定位并核验同一安装目录最近的有效事务，然后正常退出客户端并还原。</summary>
    private async Task<string> RestoreCoreAsync(string steamPath, CancellationToken cancellationToken)
    {
        string steam = ValidateSteamPath(steamPath);
        EnsureGameStopped();
        using var transactionLock = AcquireTransactionLock();
        var latest = ReadBackups(steam).FirstOrDefault(item => item.Backup.Status is "prepared" or "applied" or "launched" or "restoring");
        if (latest.Backup is null)
            throw new InvalidOperationException("当前 Steam 安装目录没有可还原的登录配置备份。");
        ValidateBackup(latest.Directory, latest.Backup);
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogDebug("已核验备份事务 {TransactionId}，正在请求 Steam 正常退出后还原。", latest.Backup.Id);
        await platform.ShutdownSteamAsync(steam, cancellationToken);
        EnsureGameStopped();
        RestoreBackup(latest.Directory, latest.Backup, steam, "restored");
        return "已还原最近的 Steam 登录配置备份；可手工启动 Steam 检查账号。";
    }

    /// <summary>避免同时运行的应用实例交错改写 Steam 登录配置。</summary>
    private FileStream AcquireTransactionLock()
    {
        Directory.CreateDirectory(stateDirectory);
        try
        {
            return new FileStream(Path.Combine(stateDirectory, "steam-login.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("另一项 Steam 账号事务仍在进行，请等待完成后重试。", exception);
        }
    }

    /// <summary>拒绝在 Master Duel 运行时改写账号配置。</summary>
    private void EnsureGameStopped()
    {
        if (platform.IsGameRunning())
            throw new InvalidOperationException("请先正常退出 Master Duel，再切换或还原账号。");
    }

    /// <summary>规范化 Steam 路径并确认启动目标存在。</summary>
    private static string ValidateSteamPath(string steamPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamPath);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath));
        if (!File.Exists(Path.Combine(full, "steam.exe")))
            throw new InvalidOperationException("Steam 安装目录中缺少 steam.exe，请重新选择目录。");
        return full;
    }

    /// <summary>按 StreamReader 的 BOM 检测规则解析原始登录配置。</summary>
    private static VdfDocument ParseLoginBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return VdfDocument.Parse(reader.ReadToEnd());
    }

    /// <summary>仅将拥有有效 Steam64 标识和登录名的对象识别为账号。</summary>
    private static bool IsAccountNode(VdfNode node) => node.Value is null && node.Key.Length == 17
        && ulong.TryParse(node.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        && !string.IsNullOrWhiteSpace(node.Find("AccountName")?.Value);

    /// <summary>同步同名配置值，避免重复键产生多个相互冲突的选择。</summary>
    private static void SetAllValues(VdfNode node, string key, string value)
    {
        var matches = node.Children.Where(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            node.SetValue(key, value);
        foreach (var match in matches)
        {
            match.Value = value;
            match.Children.Clear();
        }
    }

    /// <summary>读取同一安装目录的清单，按创建时间从新到旧排列。</summary>
    private IReadOnlyList<(string Directory, SteamLoginBackup Backup)> ReadBackups(string steam)
    {
        string backupRoot = Path.Combine(stateDirectory, "steam-login-backups");
        if (!Directory.Exists(backupRoot))
            return [];
        var backups = new List<(string Directory, SteamLoginBackup Backup)>();
        foreach (string directory in Directory.GetDirectories(backupRoot))
        {
            string manifest = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifest))
                continue;
            SteamLoginBackup? backup;
            try
            {
                backup = JsonSerializer.Deserialize<SteamLoginBackup>(File.ReadAllText(manifest), JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("登录备份事务清单损坏，请保留备份并检查清单。", exception);
            }
            if (backup is null || !string.Equals(backup.Id, Path.GetFileName(directory), StringComparison.Ordinal))
                throw new InvalidOperationException("登录备份事务标识不匹配，请保留备份并检查清单。");
            if (string.Equals(backup.SteamPath, steam, StringComparison.OrdinalIgnoreCase))
                backups.Add((directory, backup));
        }
        return backups.OrderByDescending(item => item.Backup.CreatedUtc).ToArray();
    }

    /// <summary>在开始新事务前还原最近一次中断的准备或应用操作。</summary>
    private void RecoverInterruptedTransaction(string steam)
    {
        var latest = ReadBackups(steam).FirstOrDefault(item => item.Backup.Status is "prepared" or "applied" or "launched" or "restoring");
        if (latest.Backup?.Status is "prepared" or "applied" or "restoring")
        {
            logger.LogWarning("发现中断登录事务 {TransactionId}，正在先恢复其原始状态。", latest.Backup.Id);
            RestoreBackup(latest.Directory, latest.Backup, steam, "rolled-back");
        }
    }

    /// <summary>核验原始文件与注册表数据；路径与事务标识已由备份枚举边界检查。</summary>
    private static void ValidateBackup(string directory, SteamLoginBackup backup)
    {
        string originalFile = Path.Combine(directory, "loginusers.vdf");
        if (!File.Exists(originalFile)
            || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalFile))), backup.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("登录备份文件缺失或校验失败，当前配置保持原样。");
        ValidateRegistryValues(backup.RegistryValues);
    }

    /// <summary>限制事务仅触及两个明确的自动登录注册表字段。</summary>
    internal static void ValidateRegistryValues(IReadOnlyList<SteamRegistryValue>? values)
    {
        if (values is null || values.Count != 2
            || values.Count(value => value.Name == "AutoLoginUser") != 1
            || values.Count(value => value.Name == "RememberPassword") != 1)
            throw new InvalidOperationException("登录注册表备份字段不完整或含有额外字段。");
        foreach (var value in values.Where(value => value.Exists))
            _ = GetRegistryData(value);
    }

    /// <summary>统一校验与转换注册表原值，避免写入层重复类型分支产生不可达路径。</summary>
    internal static object GetRegistryData(SteamRegistryValue value)
    {
        switch (value.Kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                return value.Text ?? throw new InvalidOperationException("登录注册表字符串数据缺失。");
            case RegistryValueKind.DWord:
                if (!value.Number.HasValue || value.Number < int.MinValue || value.Number > int.MaxValue)
                    throw new InvalidOperationException("登录注册表 DWORD 数据缺失或超出范围。");
                return (int)value.Number.Value;
            case RegistryValueKind.QWord:
                return value.Number ?? throw new InvalidOperationException("登录注册表 QWORD 数据缺失。");
            case RegistryValueKind.MultiString:
                return value.Texts ?? throw new InvalidOperationException("登录注册表多字符串数据缺失。");
            case RegistryValueKind.Binary:
                return value.Bytes ?? throw new InvalidOperationException("登录注册表二进制数据缺失。");
            default:
                throw new InvalidOperationException("登录注册表数据类型无效。");
        }
    }

    /// <summary>核验备份后原子还原文件及注册表，再记录完成状态。</summary>
    private void RestoreBackup(string directory, SteamLoginBackup backup, string steam, string status)
    {
        logger.LogDebug("开始恢复登录备份事务 {TransactionId}。", backup.Id);
        ValidateBackup(directory, backup);
        backup.Status = "restoring";
        WriteManifest(directory, backup);
        string loginFile = Path.Combine(steam, "config", "loginusers.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(loginFile)!);
        AtomicWrite(loginFile, File.ReadAllBytes(Path.Combine(directory, "loginusers.vdf")));
        platform.WriteLoginRegistry(backup.RegistryValues);
        backup.Status = status;
        WriteManifest(directory, backup);
        logger.LogDebug("登录备份事务 {TransactionId} 已完成恢复，状态 {Status}。", backup.Id, status);
    }

    /// <summary>在同目录内替换事务清单，避免截断原清单。</summary>
    private static void WriteManifest(string directory, SteamLoginBackup backup) =>
        AtomicWrite(Path.Combine(directory, "manifest.json"), Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, JsonOptions)));

    /// <summary>先写同目录临时文件并刷新到磁盘，再原子替换目标。</summary>
    private static void AtomicWrite(string destination, byte[] bytes)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(destination))
                File.Replace(temporary, destination, null);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

}

/// <summary>表示已启动进程的最小状态与句柄生命周期。</summary>
public interface ISteamStartedProcess : IDisposable
{
    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }
    /// <summary>已退出进程的返回码。</summary>
    int ExitCode { get; }
}

/// <summary>提供可注入的进程枚举、启动与等待系统边界。</summary>
public interface ISteamProcessRuntime
{
    /// <summary>当前 UTC 时间，用于退出超时计算。</summary>
    DateTimeOffset UtcNow { get; }
    /// <summary>检测指定名称的进程是否存在。</summary>
    bool HasProcess(string processName);
    /// <summary>使用调用方提供的参数列表启动进程。</summary>
    ISteamStartedProcess? Start(ProcessStartInfo startInfo);
    /// <summary>执行可取消的等待。</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>可通过专属测试键隔离验证的自动登录注册表存储。</summary>
public sealed class SteamRegistryStore
{
    /// <summary>自动登录值所处的当前用户注册表子键。</summary>
    private readonly string registryPath;

    /// <summary>建立自动登录值存储，默认使用当前用户 Steam 配置键。</summary>
    public SteamRegistryStore(string registryPath = @"Software\Valve\Steam")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        this.registryPath = registryPath;
    }
    /// <summary>读取两个自动登录值的原始存在状态、类型和数据。</summary>
    public IReadOnlyList<SteamRegistryValue> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return new[] { "AutoLoginUser", "RememberPassword" }.Select(name => ReadRegistryValue(key, name)).ToArray();
    }
    /// <summary>按原始类型和存在状态写回两个自动登录值。</summary>
    public void Write(IReadOnlyList<SteamRegistryValue> values)
    {
        SteamAccountService.ValidateRegistryValues(values);
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
        foreach (var value in values)
        {
            if (!value.Exists)
            {
                key.DeleteValue(value.Name, false);
                continue;
            }
            object data = SteamAccountService.GetRegistryData(value);
            key.SetValue(value.Name, data, value.Kind);
        }
    }

    /// <summary>读取一个原始注册表值，不展开字符串中的环境变量。</summary>
    private static SteamRegistryValue ReadRegistryValue(RegistryKey? key, string name)
    {
        object? data = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null)
            return new SteamRegistryValue { Name = name, Exists = false };
        return new SteamRegistryValue
        {
            Name = name,
            Exists = true,
            Kind = key!.GetValueKind(name),
            Text = data as string,
            Number = data is int number ? number : data is long longNumber ? longNumber : null,
            Texts = data as string[],
            Bytes = data as byte[]
        };
    }
}

/// <summary>通过可注入系统边界执行 Windows Steam 的正常退出和启动。</summary>
public sealed class WindowsSteamPlatform : ISteamPlatform
{
    /// <summary>进程启动、运行状态与时间等待的可替换系统边界。</summary>
    private readonly ISteamProcessRuntime processRuntime;
    /// <summary>自动登录注册表存储。</summary>
    private readonly SteamRegistryStore registryStore;
    /// <summary>正常退出 Steam 的最大等待时间。</summary>
    private readonly TimeSpan shutdownTimeout;

    /// <summary>建立平台服务，可替换进程系统边界及注册表存储。</summary>
    public WindowsSteamPlatform(ISteamProcessRuntime? processRuntime = null, SteamRegistryStore? registryStore = null, TimeSpan? shutdownTimeout = null)
    {
        this.processRuntime = processRuntime ?? new SystemSteamProcessRuntime();
        this.registryStore = registryStore ?? new SteamRegistryStore();
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(25);
    }
    /// <summary>检测 Master Duel 是否仍在运行。</summary>
    public bool IsGameRunning() => processRuntime.HasProcess("masterduel");
    /// <summary>正常退出 Steam 并在有限时间内等待。</summary>
    public async Task ShutdownSteamAsync(string steamPath, CancellationToken cancellationToken)
    {
        if (!processRuntime.HasProcess("steam"))
            return;
        using var request = processRuntime.Start(CreateStartInfo(steamPath, "-shutdown"))
            ?? throw new InvalidOperationException("Steam 正常退出请求启动失败。");
        DateTimeOffset deadline = processRuntime.UtcNow + shutdownTimeout;
        while (processRuntime.HasProcess("steam"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processRuntime.UtcNow >= deadline)
                throw new TimeoutException("Steam 正常退出等待超时；请手工退出 Steam 后重试。");
            await processRuntime.DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
    /// <summary>读取原始自动登录注册表快照。</summary>
    public IReadOnlyList<SteamRegistryValue> ReadLoginRegistry() => registryStore.Read();
    /// <summary>写回原始自动登录注册表快照。</summary>
    public void WriteLoginRegistry(IReadOnlyList<SteamRegistryValue> values) => registryStore.Write(values);
    /// <summary>使用 Steam 参数列表请求启动 Master Duel。</summary>
    public async Task LaunchGameAsync(string steamPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var launched = processRuntime.Start(CreateStartInfo(steamPath, "-applaunch", "1449850"))
            ?? throw new InvalidOperationException("Steam 启动请求失败。");
        await processRuntime.DelayAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None);
        if (launched.HasExited && launched.ExitCode != 0)
            throw new InvalidOperationException("Steam 启动进程立即报告失败，已触发配置还原。");
    }

    /// <summary>构造参数列表形式的 Steam 请求，避免命令字符串拼接。</summary>
    private static ProcessStartInfo CreateStartInfo(string steamPath, params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(steamPath, "steam.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = steamPath,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }
}

/// <summary>将进程系统调用与真实时间等待集中到 Windows 边界。</summary>
public sealed class SystemSteamProcessRuntime : ISteamProcessRuntime
{
    /// <summary>启动进程的系统调用，可以由宿主替换。</summary>
    private readonly Func<ProcessStartInfo, Process?> processStarter;

    /// <summary>建立使用真实进程启动调用或宿主启动器的系统边界。</summary>
    public SystemSteamProcessRuntime(Func<ProcessStartInfo, Process?>? processStarter = null) => this.processStarter = processStarter ?? Process.Start;
    /// <summary>当前系统 UTC 时间。</summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    /// <summary>枚举指定进程名称并释放取得的句柄。</summary>
    public bool HasProcess(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
    /// <summary>启动请求并将真实进程句柄包装为最小系统边界。</summary>
    public ISteamStartedProcess? Start(ProcessStartInfo startInfo)
    {
        var process = processStarter(startInfo);
        return process is null ? null : new StartedProcess(process);
    }
    /// <summary>执行可取消的真实异步等待。</summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>包装真实进程的退出状态和句柄释放操作。</summary>
    private sealed class StartedProcess : ISteamStartedProcess
    {
        /// <summary>当前启动请求取得的真实进程句柄。</summary>
        private readonly Process process;
        /// <summary>建立真实进程的状态包装。</summary>
        public StartedProcess(Process process) => this.process = process;
        /// <summary>返回真实进程退出状态。</summary>
        public bool HasExited => process.HasExited;
        /// <summary>返回真实进程退出码。</summary>
        public int ExitCode => process.ExitCode;
        /// <summary>释放真实进程句柄，不结束进程。</summary>
        public void Dispose() => process.Dispose();
    }
}
