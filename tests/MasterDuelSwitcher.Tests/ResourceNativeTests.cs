using System.ComponentModel;
using System.Text;
using MasterDuelSwitcher.Core.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace MasterDuelSwitcher.Tests;

/// <summary>验证 junction 原生错误返回及其对真实隔离目录的影响。</summary>
public sealed class ResourceNativeTests : IDisposable
{
    /// <summary>本测试独占的临时根目录。</summary>
    private readonly string root = Path.Combine(Path.GetTempPath(), "MasterDuelSwitcher.NativeTests", Guid.NewGuid().ToString("N"));

    /// <summary>创建独立测试目录。</summary>
    public ResourceNativeTests()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "bundle.bin"), "native-shared-resource");
    }

    /// <summary>本测试独立的真实共享源目录。</summary>
    private string Source => Path.Combine(root, "source");

    /// <summary>原生创建失败时保留原错误码且不创建目标目录。</summary>
    [Fact]
    public void NativeCreateFailurePreservesErrorCodeWithoutCreatingDirectory()
    {
        var path = Path.Combine(root, "link");
        var api = new FakeNativeApi { CreateSucceeded = false, ErrorCode = 123 };
        var exception = Assert.Throws<IOException>(() => JunctionOperations.Create(path, root, api));
        Assert.Equal(123, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>无效原生句柄保留原错误码，并移除本次创建的空目录。</summary>
    [Fact]
    public void InvalidHandleRemovesCreatedEmptyDirectory()
    {
        var path = Path.Combine(root, "link");
        var exception = Assert.Throws<IOException>(() => JunctionOperations.Create(path, Source, new FakeNativeApi { HandleIsInvalid = true, ErrorCode = 6 }));
        Assert.Equal(6, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>设置重解析点失败时仅清理本次空目录，来源数据保持完整。</summary>
    [Fact]
    public void SetReparseFailureRemovesOnlyCreatedEmptyDirectory()
    {
        var path = Path.Combine(root, "link");
        var exception = Assert.Throws<IOException>(() => JunctionOperations.Create(path, Source, new FakeNativeApi { ControlSucceeded = false, ErrorCode = 87 }));
        Assert.Equal(87, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        Assert.False(Directory.Exists(path));
        Assert.Equal("native-shared-resource", File.ReadAllText(Path.Combine(Source, "bundle.bin")));
    }

    /// <summary>设置失败前目标被同时移除时仍报告原生失败。</summary>
    [Fact]
    public void SetReparseFailureWhenCreatedDirectoryDisappearsPreservesOriginalError()
    {
        var path = Path.Combine(root, "link");
        var api = new FakeNativeApi { ControlSucceeded = false, BeforeControl = () => Directory.Delete(path, false) };
        Assert.Throws<IOException>(() => JunctionOperations.Create(path, Source, api));
        Assert.False(Directory.Exists(path));
    }

    /// <summary>设置失败前目标变为真实链接时，清理分支保留该链接及来源。</summary>
    [Fact]
    public void SetReparseFailurePreservesReplacementJunction()
    {
        var path = Path.Combine(root, "link");
        var api = new FakeNativeApi
        {
            ControlSucceeded = false,
            BeforeControl = () => { Directory.Delete(path, false); JunctionOperations.Create(path, Source); }
        };
        Assert.Throws<IOException>(() => JunctionOperations.Create(path, Source, api));
        Assert.Equal(Source, JunctionOperations.GetTarget(path));
        Assert.Equal("native-shared-resource", File.ReadAllText(Path.Combine(path, "bundle.bin")));
    }

    /// <summary>注入原生成功返回时仍验证实际 junction 读写效果。</summary>
    [Fact]
    public void InjectedNativeSuccessSharesRealSourceFiles()
    {
        var path = Path.Combine(root, "link");
        var api = new FakeNativeApi { BeforeControl = () => { Directory.Delete(path, false); JunctionOperations.Create(path, Source); } };
        JunctionOperations.Create(path, Source, api);
        File.WriteAllText(Path.Combine(path, "through-link.bin"), "native-write");
        Assert.Equal("native-write", File.ReadAllText(Path.Combine(Source, "through-link.bin")));
    }

    /// <summary>缺失目录和普通目录均不被误识别为 junction。</summary>
    [Fact]
    public void MissingAndOrdinaryDirectoriesHaveNoJunctionTarget()
    {
        Assert.Null(JunctionOperations.GetTarget(Path.Combine(root, "missing"), new FakeNativeApi()));
        Assert.Null(JunctionOperations.GetTarget(Source));
    }

    /// <summary>读取重解析点的原生错误保留 Windows 错误码。</summary>
    [Fact]
    public void GetReparseFailurePreservesNativeErrorCode()
    {
        var path = RealJunction();
        var exception = Assert.Throws<IOException>(() => JunctionOperations.GetTarget(path, new FakeNativeApi { ControlSucceeded = false, ErrorCode = 4390 }));
        Assert.Equal(4390, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
        Assert.Equal("native-shared-resource", File.ReadAllText(Path.Combine(path, "bundle.bin")));
    }

    /// <summary>短缓冲区和其他重解析类型均返回空目标。</summary>
    [Theory]
    [InlineData(8, 0xA0000003u)]
    [InlineData(16, 0xA000000Cu)]
    public void ShortAndNonMountPointBuffersHaveNoJunctionTarget(int returned, uint tag)
    {
        var buffer = new byte[16];
        BitConverter.GetBytes(tag).CopyTo(buffer, 0);
        Assert.Null(JunctionOperations.GetTarget(RealJunction(), new FakeNativeApi { ReparseBuffer = buffer, ReturnedBytes = returned }));
    }

    /// <summary>奇数长度和超出返回数据的目标区间均被报告为损坏。</summary>
    [Theory]
    [InlineData(1, 0, 18)]
    [InlineData(4, 100, 20)]
    public void MalformedTargetRangesAreRejected(ushort length, ushort offset, int returned)
    {
        var buffer = new byte[32];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(offset).CopyTo(buffer, 8);
        BitConverter.GetBytes(length).CopyTo(buffer, 10);
        Assert.Throws<InvalidDataException>(() => JunctionOperations.GetTarget(RealJunction(), new FakeNativeApi { ReparseBuffer = buffer, ReturnedBytes = returned }));
    }

    /// <summary>带原生前缀和不带前缀的有效目标缓冲区得到同一规范路径。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidTargetBuffersAreDecodedToActualSource(bool prefix)
    {
        var buffer = TargetBuffer(Source, prefix);
        Assert.Equal(Source, JunctionOperations.GetTarget(RealJunction(), new FakeNativeApi { ReparseBuffer = buffer }));
    }

    /// <summary>普通目录和不同来源的链接在移除时均保留原内容。</summary>
    [Fact]
    public void RemovingUnmatchedTargetPreservesDirectoryAndJunction()
    {
        Assert.Throws<InvalidOperationException>(() => JunctionOperations.RemoveMatching(Source, Source, new FakeNativeApi()));
        var path = RealJunction();
        Assert.Throws<InvalidOperationException>(() => JunctionOperations.RemoveMatching(path, root));
        Assert.Equal(Source, JunctionOperations.GetTarget(path));
        Assert.Equal("native-shared-resource", File.ReadAllText(Path.Combine(Source, "bundle.bin")));
    }

    /// <summary>匹配链接只删除链接本身，不影响真实来源。</summary>
    [Fact]
    public void RemovingMatchedJunctionLeavesSourceUntouched()
    {
        var path = RealJunction();
        JunctionOperations.RemoveMatching(path, Source);
        Assert.False(Directory.Exists(path));
        Assert.Equal("native-shared-resource", File.ReadAllText(Path.Combine(Source, "bundle.bin")));
    }

    /// <summary>目录身份原生失败应保留错误码。</summary>
    [Fact]
    public void NativeDirectoryIdentityFailurePreservesErrorCode()
    {
        var exception = Assert.Throws<IOException>(() => JunctionOperations.GetDirectoryIdentity(Source, new FakeNativeApi { InformationSucceeded = false, ErrorCode = 6 }));
        Assert.Equal(6, Assert.IsType<Win32Exception>(exception.InnerException).NativeErrorCode);
    }

    /// <summary>原生卷和文件标识使用固定十六进制格式，并读取真实目录身份。</summary>
    [Fact]
    public void DirectoryIdentityFormatsAllNativeComponents()
    {
        Assert.Equal("00000001:0000000200000003", JunctionOperations.GetDirectoryIdentity(Source, new FakeNativeApi()));
        Assert.Matches("^[0-9A-F]{8}:[0-9A-F]{16}$", JunctionOperations.GetDirectoryIdentity(Source));
    }

    /// <summary>默认实现直接操作真实 WinAPI，并覆盖原生错误码和失败信息返回。</summary>
    [Fact]
    public void DefaultNativeApiReportsRealWindowsFailures()
    {
        var api = JunctionOperations.NativeApi;
        Assert.False(api.CreateDirectory(Source));
        Assert.NotEqual(0, api.GetLastError());
        using var missing = api.CreateFile(Path.Combine(root, "missing"), 0);
        Assert.True(missing.IsInvalid);
        Assert.NotEqual(0, api.GetLastError());
        using var handle = api.CreateFile(Source, 0);
        Assert.False(api.DeviceIoControl(handle, 0x000900A8, null, 0, new byte[16 * 1024], 16 * 1024, out _));
        Assert.NotEqual(0, api.GetLastError());
        Assert.False(api.GetDirectoryInformation(missing, out _, out _, out _));
        Assert.NotEqual(0, api.GetLastError());
        Assert.Throws<IOException>(() => JunctionOperations.Create(Source, root));
    }

    /// <summary>创建真实 NTFS junction，使解析测试仍针对实际链接路径。</summary>
    private string RealJunction()
    {
        var path = Path.Combine(root, "link-" + Guid.NewGuid().ToString("N"));
        JunctionOperations.Create(path, Source);
        return path;
    }

    /// <summary>构造符合挂载点目标区间布局的原生缓冲区。</summary>
    private static byte[] TargetBuffer(string target, bool prefix)
    {
        var bytes = Encoding.Unicode.GetBytes((prefix ? @"\??\" : "") + target);
        var buffer = new byte[16 + bytes.Length];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(buffer, 10);
        bytes.CopyTo(buffer, 16);
        return buffer;
    }

    /// <summary>仅清理本测试创建的隔离目录，目录链接不递归进入来源。</summary>
    public void Dispose()
    {
        DeleteOwnedDirectory(root);
    }

    /// <summary>删除本测试拥有的目录树，识别链接后仅移除链接本身。</summary>
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

    /// <summary>以确定原生返回值覆盖 Win32 错误处理，同时保留真实目录动作。</summary>
    private sealed class FakeNativeApi : IJunctionNativeApi
    {
        /// <summary>目录创建调用的返回结果。</summary>
        internal bool CreateSucceeded { get; init; } = true;
        /// <summary>原生错误码。</summary>
        internal int ErrorCode { get; init; } = 5;
        /// <summary>是否返回无效原生句柄。</summary>
        internal bool HandleIsInvalid { get; init; }
        /// <summary>文件系统控制请求是否成功。</summary>
        internal bool ControlSucceeded { get; init; } = true;
        /// <summary>文件系统控制请求执行前的真实目录变化。</summary>
        internal Action? BeforeControl { get; init; }
        /// <summary>模拟返回的重解析点缓冲区。</summary>
        internal byte[]? ReparseBuffer { get; init; }
        /// <summary>需要单独验证的返回数据长度。</summary>
        internal int? ReturnedBytes { get; init; }
        /// <summary>目录身份调用是否成功。</summary>
        internal bool InformationSucceeded { get; init; } = true;
        /// <summary>根据指定结果创建真实空目录。</summary>
        public bool CreateDirectory(string path)
        {
            if (CreateSucceeded) Directory.CreateDirectory(path);
            return CreateSucceeded;
        }
        /// <summary>返回不拥有操作系统资源的测试句柄。</summary>
        public SafeFileHandle CreateFile(string path, uint desiredAccess) => new(new IntPtr(HandleIsInvalid ? -1 : 1), false);
        /// <summary>默认模拟原生控制操作成功。</summary>
        public bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[]? input, int inputSize, byte[]? output, int outputSize, out int bytesReturned)
        {
            BeforeControl?.Invoke();
            ReparseBuffer?.CopyTo(output!, 0);
            bytesReturned = ReturnedBytes ?? ReparseBuffer?.Length ?? 0;
            return ControlSucceeded;
        }
        /// <summary>默认返回稳定的测试目录标识。</summary>
        public bool GetDirectoryInformation(SafeFileHandle handle, out uint volumeSerialNumber, out uint fileIndexHigh, out uint fileIndexLow)
        {
            volumeSerialNumber = 1;
            fileIndexHigh = 2;
            fileIndexLow = 3;
            return InformationSucceeded;
        }
        /// <summary>返回本次配置的 Windows 错误码。</summary>
        public int GetLastError() => ErrorCode;
    }
}
