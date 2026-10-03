using MasterDuelSwitcher.Core.Models;
using System.Text.Json;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>以原子文件替换方式保存工具设置。</summary>
public sealed class SettingsStore
{
    /// <summary>独立的工具数据目录。</summary>
    public string StateDirectory { get; }
    /// <summary>设置文件的完整路径。</summary>
    private string SettingsPath => Path.Combine(StateDirectory, "settings.json");
    /// <summary>可读且兼容大小写的 JSON 选项。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    /// <summary>初始化设置存储，不访问 Steam 凭证。</summary>
    public SettingsStore(string stateDirectory) => StateDirectory = Path.GetFullPath(stateDirectory);
    /// <summary>读取设置，缺失时返回默认值。</summary>
    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new InvalidDataException("设置文件内容为空，请检查 settings.json。");
            value.AccountBindings ??= [];
            value.AccountNotes ??= [];
            value.HiddenAccounts ??= [];
            value.SteamPath ??= "";
            value.GamePath ??= "";
            value.SourceProfile ??= "";
            return value;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("设置文件格式损坏，原文件已保留，请检查 settings.json。", ex);
        }
    }
    /// <summary>完整保存设置并原子替换旧文件。</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(StateDirectory);
        var temporaryPath = Path.Combine(StateDirectory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
