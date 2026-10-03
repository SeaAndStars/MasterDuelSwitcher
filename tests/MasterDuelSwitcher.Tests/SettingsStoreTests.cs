using MasterDuelSwitcher.Core.Models;
using MasterDuelSwitcher.Core.Services;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证设置的真实文件读写及损坏文件保护。</summary>
public sealed class SettingsStoreTests : IDisposable
{
    /// <summary>当前测试独占的临时目录。</summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mdswitch-settings-" + Guid.NewGuid().ToString("N"));
    /// <summary>创建当前测试的独立目录。</summary>
    public SettingsStoreTests() => Directory.CreateDirectory(_directory);
    /// <summary>新安装应返回空账号和默认主题。</summary>
    [Fact]
    public void MissingFileReturnsDefaults()
    {
        var value = new SettingsStore(_directory).Load();
        Assert.Empty(value.AccountBindings);
        Assert.Empty(value.SteamPath);
        Assert.False(value.DarkTheme);
    }
    /// <summary>中文备注、路径和账号绑定在保存后应完整保留。</summary>
    [Fact]
    public void RoundtripPreservesUnicodeAndBindings()
    {
        var store = new SettingsStore(_directory);
        store.Save(new AppSettings { SteamPath = "D:\\游戏库\\Steam", DarkTheme = true,
            AccountNotes = new() { ["76561198000000000"] = "练习账号" },
            AccountBindings = new() { ["76561198000000000"] = "a1b2c3d4" } });
        var loaded = new SettingsStore(_directory).Load();
        Assert.Equal("练习账号", loaded.AccountNotes["76561198000000000"]);
        Assert.Equal("a1b2c3d4", loaded.AccountBindings["76561198000000000"]);
        Assert.Equal("D:\\游戏库\\Steam", loaded.SteamPath);
        Assert.True(loaded.DarkTheme);
    }
    /// <summary>多次写入后只能看到最新设置且无残留临时文件。</summary>
    [Fact]
    public void SaveReplacesAtomicallyWithoutStaleTemporaryFiles()
    {
        var store = new SettingsStore(_directory);
        store.Save(new AppSettings { SourceProfile = "11223344" });
        store.Save(new AppSettings { SourceProfile = "aabbccdd" });
        Assert.Equal("aabbccdd", store.Load().SourceProfile);
        Assert.Single(Directory.GetFiles(_directory));
    }
    /// <summary>损坏 JSON 应报告具体错误并原样保留文件。</summary>
    [Fact]
    public void CorruptJsonIsReportedAndPreserved()
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{invalid}");
        Assert.Throws<InvalidDataException>(() => new SettingsStore(_directory).Load());
        Assert.Equal("{invalid}", File.ReadAllText(path));
    }
    /// <summary>外部编辑为 null 的集合应规范化为可操作的空集合。</summary>
    [Fact]
    public void NullCollectionsAreNormalized()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"),
            "{\"AccountBindings\":null,\"AccountNotes\":null,\"HiddenAccounts\":null,\"SteamPath\":null}");
        var result = new SettingsStore(_directory).Load();
        Assert.Empty(result.AccountBindings);
        Assert.Empty(result.AccountNotes);
        Assert.Empty(result.HiddenAccounts);
        Assert.Empty(result.SteamPath);
    }
    /// <summary>测试结束后仅删除当前测试创建的目录。</summary>
    public void Dispose() => Directory.Delete(_directory, true);
}
