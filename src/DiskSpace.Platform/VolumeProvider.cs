using DiskSpace.Core;

namespace DiskSpace.Platform;

public sealed class VolumeProvider : IVolumeProvider
{
    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var total = drive.TotalSize;
                if (total <= 0) continue; // Some Linux pseudo-filesystems identify themselves as fixed drives.
                var label = drive.VolumeLabel;
                var name = string.IsNullOrWhiteSpace(label) || label == drive.Name ? drive.Name : $"{label} ({drive.Name})";
                result.Add(new(name, drive.RootDirectory.FullName, total, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result.OrderBy(v => v.Path, StringComparer.Ordinal).ToArray();
    }
}
