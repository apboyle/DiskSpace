using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskSpace.Core;
using DiskSpace.Platform;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DiskSpace.Tests;

public sealed class NativeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "diskspace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly NativeFileSystem _fs = new();
    public NativeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NativeTreeIncludesHiddenUnicodeAndEmptyEntries()
    {
        var empty = Path.Combine(_root, "empty"); Directory.CreateDirectory(empty);
        var data = Path.Combine(_root, "héllo-世界.bin");
        await File.WriteAllBytesAsync(data, new byte[12345], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_root, ".hidden"), [1], TestContext.Current.CancellationToken);
        var metadata = _fs.ReadMetadata(data);
        Assert.Equal(EntryKind.File, metadata.Kind);
        Assert.Equal(12345, metadata.LogicalBytes);
        Assert.True(metadata.AllocatedBytes > 0, $"Expected allocated storage, got {metadata}");
        Assert.Equal(EntryKind.Directory, _fs.ReadMetadata(_root).Kind);
        Assert.Equal(EntryKind.Directory, _fs.ReadMetadata(empty).Kind);
        var scan = new ScanService(_fs).Start(_root, TestContext.Current.CancellationToken); await scan.Completion;
        Assert.Equal(ScanState.Complete, scan.State);
        Assert.Equal(3, scan.Root.Children.Count);
        Assert.True(scan.Root.TotalBytes >= metadata.AllocatedBytes);
    }

    [Fact]
    public async Task NativeHardLinksAreCountedOnce()
    {
        var original = Path.Combine(_root, "z.bin");
        var other = Path.Combine(_root, "a.bin");
        await File.WriteAllBytesAsync(original, new byte[65536], TestContext.Current.CancellationToken);
        var linked = OperatingSystem.IsWindows() ? CreateHardLink(other, original, IntPtr.Zero) : Link(original, other) == 0;
        Assert.True(linked, $"Hard-link creation failed: {Marshal.GetLastPInvokeError()}");
        var first = _fs.ReadMetadata(original); var second = _fs.ReadMetadata(other);
        Assert.Equal(first.Identity, second.Identity);
        Assert.True(first.LinkCount >= 2);
        var scan = new ScanService(_fs).Start(_root, TestContext.Current.CancellationToken); await scan.Completion;
        Assert.Equal(ScanState.Complete, scan.State);
        Assert.Equal(first.AllocatedBytes, scan.Root.Children.Sum(c => c.AttributedBytes));
        Assert.Equal("a.bin", scan.Root.Children.Single(c => c.SharedWith is null).Name);
    }

    [Fact]
    public async Task NativeSparseAllocationIsSmallerThanFileLength()
    {
        // APFS may allocate small holes eagerly; a 128 MiB fixture exercises sparse extents.
        const int length = 128 * 1024 * 1024;
        var path = Path.Combine(_root, "sparse.bin");
        if (OperatingSystem.IsWindows())
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            Assert.True(DeviceIoControl(file.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            file.SetLength(length);
            file.Position = file.Length - 1; file.WriteByte(1);
            file.Flush(true); // Commit delayed allocation before measuring the native result.
        }
        else
        {
            // dd seeks directly, avoiding runtime/platform-specific SetLength preallocation.
            var seed = Path.Combine(_root, "seed");
            await File.WriteAllTextAsync(seed, "1", TestContext.Current.CancellationToken);
            Assert.Equal(0, Run("dd", $"if={seed}", $"of={path}", "bs=1", "count=1", $"seek={length - 1}").ExitCode);
        }
        var metadata = _fs.ReadMetadata(path);
        Assert.Equal(length, metadata.LogicalBytes);
        Assert.NotNull(metadata.AllocatedBytes);
        Assert.True(metadata.AllocatedBytes < metadata.LogicalBytes / 2, $"Allocated {metadata.AllocatedBytes} of {metadata.LogicalBytes}");
        Assert.True(metadata.AllocatedBytes > 0, $"Expected a nonzero sparse extent, got {metadata}");
    }

    [Fact]
    public async Task NativeLinksDoNotFollowCycles()
    {
        // Windows can create junctions without Developer Mode or administrator privileges.
        var link = Path.Combine(_root, "cycle");
        if (OperatingSystem.IsWindows())
        {
            var result = Run("cmd.exe", "/c", "mklink", "/J", link, _root);
            Assert.Equal(0, result.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, _root);
        try
        {
            Assert.Equal(EntryKind.Link, _fs.ReadMetadata(link).Kind);
            var scan = new ScanService(_fs).Start(_root, TestContext.Current.CancellationToken); await scan.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(ScanState.Complete, scan.State);
            Assert.Single(scan.Root.Children);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task NativePermissionsAreReportedAsIncomplete()
    {
        if (OperatingSystem.IsWindows()) return; // Windows ACL denial requires account-specific setup.
        var directory = Path.Combine(_root, "denied"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "secret"), "test", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(directory, UnixFileMode.None);
        try
        {
            var scan = new ScanService(_fs).Start(_root, TestContext.Current.CancellationToken); await scan.Completion;
            // Root can bypass mode bits; ordinary user runs must report the denied subtree.
            if (GetEuid() != 0) { Assert.Equal(ScanState.Incomplete, scan.State); Assert.True(scan.IssueCount > 0); }
        }
        finally { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task WindowsCompressionUsesPhysicalAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "compressed.bin");
        await File.WriteAllBytesAsync(path, new byte[1024 * 1024], TestContext.Current.CancellationToken);
        var compressed = Run("compact.exe", "/c", "/i", "/q", path);
        Assert.Equal(0, compressed.ExitCode);
        var metadata = _fs.ReadMetadata(path);
        Assert.Equal(1024 * 1024, metadata.LogicalBytes);
        Assert.True(metadata.AllocatedBytes < metadata.LogicalBytes / 2);
    }

    [Fact]
    public async Task UnixAllocationAgreesWithSystemStat()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "ordinary.bin");
        await File.WriteAllBytesAsync(path, new byte[123456], TestContext.Current.CancellationToken);
        var result = OperatingSystem.IsMacOS() ? Run("/usr/bin/stat", "-f", "%b", path) : Run("stat", "-c", "%b", path);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(long.Parse(result.Output.Trim()) * 512, _fs.ReadMetadata(path).AllocatedBytes);
    }

    [Fact]
    public void MissingPathFailsAndVolumeListIsUsable()
    {
        Assert.Throws<IOException>(() => _fs.ReadMetadata(Path.Combine(_root, "missing")));
        var volumes = new VolumeProvider().GetVolumes();
        Assert.NotEmpty(volumes);
        Assert.All(volumes, v => { Assert.True(Path.IsPathRooted(v.Path)); Assert.True(v.TotalBytes > 0); });
    }

    [Fact]
    public async Task NativeLongPathsCanBeScanned()
    {
        var path = _root;
        for (var i = 0; i < 12; i++) path = Path.Combine(path, "long-directory-name-" + i);
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "file.txt"), "long path", TestContext.Current.CancellationToken);
        var scan = new ScanService(_fs).Start(_root, TestContext.Current.CancellationToken); await scan.Completion;
        Assert.Equal(ScanState.Complete, scan.State);
        Assert.True(scan.Root.TotalBytes > 0);
    }

    public void Dispose() => Directory.Delete(_root, true);
    private static (int ExitCode, string Output) Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(); return (process.ExitCode, stdout + stderr);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEuid();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
