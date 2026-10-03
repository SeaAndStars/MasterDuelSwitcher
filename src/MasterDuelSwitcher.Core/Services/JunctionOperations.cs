using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MasterDuelSwitcher.Core.Services;

/// <summary>封装目录重解析点所需的原生调用，便于验证真实错误返回和缓冲区边界。</summary>
internal interface IJunctionNativeApi
{
    /// <summary>创建不存在的普通目录。</summary>
    bool CreateDirectory(string path);
    /// <summary>以目录和重解析点自身语义打开句柄。</summary>
    SafeFileHandle CreateFile(string path, uint desiredAccess);
    /// <summary>读取或设置重解析点原生缓冲区。</summary>
    bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[]? input, int inputSize, byte[]? output, int outputSize, out int bytesReturned);
    /// <summary>读取目录卷序列号和稳定文件标识。</summary>
    bool GetDirectoryInformation(SafeFileHandle handle, out uint volumeSerialNumber, out uint fileIndexHigh, out uint fileIndexLow);
    /// <summary>取得最近一次原生调用的 Windows 错误码。</summary>
    int GetLastError();
}

/// <summary>直接调用 Windows 重解析点接口，创建、读取和删除 NTFS junction。</summary>
internal static class JunctionOperations
{
    /// <summary>Windows 目录挂载点类型标记。</summary>
    private const uint MountPointTag = 0xA0000003;
    /// <summary>设置重解析点的文件系统控制码。</summary>
    private const uint SetReparsePoint = 0x000900A4;
    /// <summary>读取重解析点的文件系统控制码。</summary>
    private const uint GetReparsePoint = 0x000900A8;
    /// <summary>以重解析点自身和目录语义打开句柄的标志。</summary>
    private const uint ReparseDirectoryFlags = 0x02200000;
    /// <summary>允许同时读写和删除的句柄共享标志。</summary>
    private const uint ShareAll = 7;
    /// <summary>打开已有路径的创建模式。</summary>
    private const uint OpenExisting = 3;
    /// <summary>修改目录重解析点所需的通用写权限。</summary>
    private const uint GenericWrite = 0x40000000;
    /// <summary>未指定注入实现时使用的真实 Windows 原生接口。</summary>
    internal static readonly IJunctionNativeApi NativeApi = new WindowsJunctionNativeApi();

    /// <summary>创建原先不存在的目录并将其设置为指向真实来源的 junction。</summary>
    internal static void Create(string path, string target, IJunctionNativeApi? api = null)
    {
        api ??= NativeApi;
        if (!api.CreateDirectory(path)) ThrowLastError("创建 junction 目录失败", path, api);
        try
        {
            using var handle = Open(path, GenericWrite, api);
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
            BitConverter.GetBytes(MountPointTag).CopyTo(buffer, 0);
            BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
            BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
            BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
            BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 18 + substitute.Length);
            if (!api.DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, null, 0, out _))
                ThrowLastError("设置 NTFS junction 失败", path, api);
        }
        catch
        {
            // 只移除本次新建的空目录；非递归操作不会触及链接指向的资源。
            if (ResourcePathValidation.Attributes(path) is { } attributes && (attributes & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(path, false);
            throw;
        }
    }

    /// <summary>读取路径自身的 junction 目标；其他重解析点类型返回空值。</summary>
    internal static string? GetTarget(string path, IJunctionNativeApi? api = null)
    {
        api ??= NativeApi;
        if (ResourcePathValidation.Attributes(path) is not { } attributes || (attributes & FileAttributes.ReparsePoint) == 0) return null;
        using var handle = Open(path, 0, api);
        var buffer = new byte[16 * 1024];
        if (!api.DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out var returned))
            ThrowLastError("读取 NTFS junction 失败", path, api);
        if (returned < 16 || BitConverter.ToUInt32(buffer, 0) != MountPointTag) return null;
        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);
        if ((length & 1) != 0 || 16 + offset + length > returned) throw new InvalidDataException($"junction 数据损坏：{path}");
        var target = Encoding.Unicode.GetString(buffer, 16 + offset, length);
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return ResourcePathValidation.Normalize(target);
    }

    /// <summary>再次核验链接目标后，仅删除匹配 junction 自身。</summary>
    internal static void RemoveMatching(string path, string expectedTarget, IJunctionNativeApi? api = null)
    {
        var actual = GetTarget(path, api);
        if (actual is null || !ResourcePathValidation.Equal(actual, expectedTarget))
            throw new InvalidOperationException($"目标已被其他目录或链接替换，保留现场：{path}");
        Directory.Delete(path, false);
    }

    /// <summary>读取目录稳定的卷标识和文件标识，用于区分原目录回迁与第三方替换。</summary>
    internal static string GetDirectoryIdentity(string path, IJunctionNativeApi? api = null)
    {
        api ??= NativeApi;
        ResourcePathValidation.RealDirectory(path);
        using var handle = Open(path, 0, api);
        if (!api.GetDirectoryInformation(handle, out var volumeSerialNumber, out var fileIndexHigh, out var fileIndexLow))
            ThrowLastError("读取资源目录标识失败", path, api);
        return $"{volumeSerialNumber:X8}:{fileIndexHigh:X8}{fileIndexLow:X8}";
    }

    /// <summary>以不跟随重解析点的方式打开目录句柄。</summary>
    private static SafeFileHandle Open(string path, uint access, IJunctionNativeApi api)
    {
        var handle = api.CreateFile(path, access);
        if (handle.IsInvalid)
        {
            var error = api.GetLastError();
            handle.Dispose();
            throw new IOException($"打开 junction 失败：{path}", new Win32Exception(error));
        }
        return handle;
    }

    /// <summary>将最近的 Windows 错误转为包含操作路径的异常。</summary>
    private static void ThrowLastError(string message, string path, IJunctionNativeApi api) => throw new IOException($"{message}：{path}", new Win32Exception(api.GetLastError()));

    /// <summary>把目录原生接口转发至真实 Win32 调用，不改变目录标志或错误码语义。</summary>
    private sealed class WindowsJunctionNativeApi : IJunctionNativeApi
    {
        /// <summary>创建真实普通目录，已存在路径返回原生失败。</summary>
        public bool CreateDirectory(string path) => CreateDirectoryNative(path, IntPtr.Zero);
        /// <summary>打开真实目录自身，禁止跟随末级重解析点。</summary>
        public SafeFileHandle CreateFile(string path, uint desiredAccess) => JunctionOperations.CreateFile(path, desiredAccess, ShareAll, IntPtr.Zero, OpenExisting, ReparseDirectoryFlags, IntPtr.Zero);
        /// <summary>转发读取或写入重解析点缓冲区的原生控制请求。</summary>
        public bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[]? input, int inputSize, byte[]? output, int outputSize, out int bytesReturned) => JunctionOperations.DeviceIoControl(handle, controlCode, input, inputSize, output, outputSize, out bytesReturned, IntPtr.Zero);
        /// <summary>取得真实目录卷序列号和文件标识，并保留原生成功状态。</summary>
        public bool GetDirectoryInformation(SafeFileHandle handle, out uint volumeSerialNumber, out uint fileIndexHigh, out uint fileIndexLow)
        {
            var succeeded = GetFileInformationByHandle(handle, out var information);
            volumeSerialNumber = information.VolumeSerialNumber;
            fileIndexHigh = information.FileIndexHigh;
            fileIndexLow = information.FileIndexLow;
            return succeeded;
        }
        /// <summary>读取由最近一次带 SetLastError 调用保存的 Win32 错误。</summary>
        public int GetLastError() => Marshal.GetLastWin32Error();
    }

    /// <summary>创建新目录；目录已存在时返回失败，避免覆盖竞争产生的路径。</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, IntPtr securityAttributes);

    /// <summary>以指定目录及重解析点语义获取 Windows 文件句柄。</summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>调用 Windows 文件系统控制接口读取或设置重解析点。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[]? input, int inputSize, byte[]? output, int outputSize, out int bytesReturned, IntPtr overlapped);

    /// <summary>读取目录句柄对应的文件系统稳定身份。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out DirectoryInformation information);

    /// <summary>Windows 文件句柄信息的原生字段布局。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryInformation
    {
        /// <summary>目录的文件属性位。</summary>
        public uint FileAttributes;
        /// <summary>创建时间低位。</summary>
        public uint CreationTimeLow;
        /// <summary>创建时间高位。</summary>
        public uint CreationTimeHigh;
        /// <summary>访问时间低位。</summary>
        public uint LastAccessTimeLow;
        /// <summary>访问时间高位。</summary>
        public uint LastAccessTimeHigh;
        /// <summary>写入时间低位。</summary>
        public uint LastWriteTimeLow;
        /// <summary>写入时间高位。</summary>
        public uint LastWriteTimeHigh;
        /// <summary>文件所在卷的序列号。</summary>
        public uint VolumeSerialNumber;
        /// <summary>文件长度高位。</summary>
        public uint FileSizeHigh;
        /// <summary>文件长度低位。</summary>
        public uint FileSizeLow;
        /// <summary>文件硬链接数量。</summary>
        public uint NumberOfLinks;
        /// <summary>稳定文件标识高位。</summary>
        public uint FileIndexHigh;
        /// <summary>稳定文件标识低位。</summary>
        public uint FileIndexLow;
    }
}
