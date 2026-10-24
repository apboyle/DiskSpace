using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskSpace.Core;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace.Platform;

public sealed class NativeFileSystem : IFileSystem
{
    public IEnumerable<string> EnumerateChildren(string path) => Directory.EnumerateFileSystemEntries(path,
        "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false });

    public EntryMetadata ReadMetadata(string path)
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(path);
        if (OperatingSystem.IsLinux()) return ReadLinux(path);
        if (OperatingSystem.IsMacOS()) return ReadMac(path);
        throw new PlatformNotSupportedException("DiskSpace supports Windows, macOS and Linux.");
    }

    private static EntryMetadata ReadWindows(string path)
    {
        // Metadata-only handles, including the reparse point itself. Never read file contents.
        using var handle = CreateFile(ToExtendedPath(path), 0, 7, IntPtr.Zero, 3,
            0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError(path);
        if (!GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())) throw NativeError(path);
        if (!GetAttributeInfo(handle, 9, out var attributes, 8)) throw NativeError(path);

        FileIdentity identity;
        if (GetIdInfo(handle, 18, out var id, 24)) identity = new(id.Volume, id.High, id.Low);
        else if (GetLegacyInfo(handle, out var legacy))
            identity = new(legacy.Volume, 0, ((ulong)legacy.FileIndexHigh << 32) | legacy.FileIndexLow);
        else throw NativeError(path);

        var directory = (attributes.Attributes & 0x10) != 0;
        var redirected = (attributes.Attributes & 0x400) != 0 && (attributes.Tag & 0x20000000) != 0;
        long? allocation = standard.AllocationSize;
        // Standard allocation is cluster-rounded for ordinary files. For compressed/sparse files
        // the compressed-file query exposes physically allocated storage instead of logical extents.
        if ((attributes.Attributes & (0x800u | 0x200u)) != 0 && !redirected)
        {
            var low = GetCompressedFileSize(ToExtendedPath(path), out var high);
            allocation = low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0
                ? null : checked((long)(((ulong)high << 32) | low));
        }
        return new(redirected ? EntryKind.Link : directory ? EntryKind.Directory : EntryKind.File,
            allocation, standard.EndOfFile, identity, 0, standard.NumberOfLinks);
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static EntryMetadata ReadLinux(string path)
    {
        if (Statx(-100, path, 0x100 | 0x800, 0x7ff | 0x1000, out var stat) != 0) throw NativeError(path);
        if ((stat.Mask & 0x100) == 0 || (stat.Mask & 0x2) == 0)
            throw new IOException($"File identity is unavailable: {path}");
        var device = ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor;
        return new(KindFromMode(stat.Mode), (stat.Mask & 0x400) != 0 ? checked((long)stat.Blocks * 512) : null,
            checked((long)stat.Size), new(device, 0, stat.Inode), (stat.Mask & 0x1000) != 0 ? stat.MountId : 0, stat.Links);
    }

    private static EntryMetadata ReadMac(string path)
    {
        MacStat stat;
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? MacLStat64(path, out stat) : MacLStat(path, out stat);
        if (result != 0) throw NativeError(path);
        return new(KindFromMode(stat.Mode), checked(stat.Blocks * 512), stat.Size,
            new(unchecked((uint)stat.Device), 0, stat.Inode), 0, stat.Links);
    }

    private static EntryKind KindFromMode(ushort mode) => (mode & 0xf000) switch
    { 0x4000 => EntryKind.Directory, 0x8000 => EntryKind.File, 0xa000 => EntryKind.Link, _ => EntryKind.Other };

    private static IOException NativeError(string path) => new($"{path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize, EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending, Directory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong Volume, Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, FileSizeHigh, FileSizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacStat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Links;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(96)] public long Size;
        [FieldOffset(104)] public long Blocks;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandardInfo(SafeFileHandle handle, int type, out StandardInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAttributeInfo(SafeFileHandle handle, int type, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetIdInfo(SafeFileHandle handle, int type, out IdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLegacyInfo(SafeFileHandle handle, out LegacyInfo info);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out LinuxStatx stat);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int MacLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat stat);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int MacLStat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacStat stat);
}
