namespace DiskSpace.Core;

public enum EntryKind { File, Directory, Link, Other }

public readonly record struct FileIdentity(ulong Device, ulong High, ulong Low);

public readonly record struct EntryMetadata(
    EntryKind Kind, long? AllocatedBytes, long LogicalBytes,
    FileIdentity Identity, ulong MountId, uint LinkCount);

public interface IFileSystem
{
    EntryMetadata ReadMetadata(string path);
    IEnumerable<string> EnumerateChildren(string path);
}

public sealed record VolumeInfo(string Name, string Path, long TotalBytes, long AvailableBytes)
{
    public string DisplayName => $"{Name}  ·  {ByteSize.Format(TotalBytes)}";
}

public interface IVolumeProvider
{
    IReadOnlyList<VolumeInfo> GetVolumes();
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N2} {units[unit]}";
    }
}
