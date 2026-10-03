using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MasterDuelSwitcher.Core.Services;

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

    /// <summary>创建原先不存在的目录并将其设置为指向真实来源的 junction。</summary>
    internal static void Create(string path, string target)
    {
        if (!CreateDirectoryNative(path, IntPtr.Zero)) ThrowLastError("创建 junction 目录失败", path);
        try
        {
            using var handle = Open(path, GenericWrite);
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
            if (!DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
                ThrowLastError("设置 NTFS junction 失败", path);
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
    internal static string? GetTarget(string path)
    {
        if (ResourcePathValidation.Attributes(path) is not { } attributes || (attributes & FileAttributes.ReparsePoint) == 0) return null;
        using var handle = Open(path, 0);
        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, GetReparsePoint, null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero))
            ThrowLastError("读取 NTFS junction 失败", path);
        if (returned < 16 || BitConverter.ToUInt32(buffer, 0) != MountPointTag) return null;
        var offset = BitConverter.ToUInt16(buffer, 8);
        var length = BitConverter.ToUInt16(buffer, 10);
        if ((length & 1) != 0 || 16 + offset + length > returned) throw new InvalidDataException($"junction 数据损坏：{path}");
        var target = Encoding.Unicode.GetString(buffer, 16 + offset, length);
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        return ResourcePathValidation.Normalize(target);
    }

    /// <summary>再次核验链接目标后，仅删除匹配 junction 自身。</summary>
    internal static void RemoveMatching(string path, string expectedTarget)
    {
        var actual = GetTarget(path);
        if (actual is null || !ResourcePathValidation.Equal(actual, expectedTarget))
            throw new InvalidOperationException($"目标已被其他目录或链接替换，保留现场：{path}");
        Directory.Delete(path, false);
    }

    /// <summary>读取目录稳定的卷标识和文件标识，用于区分原目录回迁与第三方替换。</summary>
    internal static string GetDirectoryIdentity(string path)
    {
        ResourcePathValidation.RealDirectory(path);
        using var handle = Open(path, 0);
        if (!GetFileInformationByHandle(handle, out var information)) ThrowLastError("读取资源目录标识失败", path);
        return $"{information.VolumeSerialNumber:X8}:{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
    }

    /// <summary>以不跟随重解析点的方式打开目录句柄。</summary>
    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, ShareAll, IntPtr.Zero, OpenExisting, ReparseDirectoryFlags, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"打开 junction 失败：{path}", new Win32Exception(error));
        }
        return handle;
    }

    /// <summary>将最近的 Windows 错误转为包含操作路径的异常。</summary>
    private static void ThrowLastError(string message, string path) => throw new IOException($"{message}：{path}", new Win32Exception(Marshal.GetLastWin32Error()));

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
