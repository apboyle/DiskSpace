# DiskSpace

A read-only desktop storage analyzer for Windows, macOS, and Linux. Choose a drive or folder and explore a live, expandable tree sorted by allocated storage, largest first.

## Run

Install the .NET 10 SDK, then run:

```sh
dotnet run --project src/DiskSpace.App
```

Or extract a self-contained package from `artifacts/packages` (no .NET installation required):

| Platform | Launch |
| --- | --- |
| Windows | `DiskSpace/DiskSpace.App.exe` |
| macOS | `DiskSpace.app` |
| Linux | `./DiskSpace/DiskSpace.App` |

On Unix, use an archive utility that preserves Unix executable permissions. If necessary, run `chmod +x DiskSpace/DiskSpace.App` on Linux or `chmod +x DiskSpace.app/Contents/MacOS/DiskSpace.App` on macOS. Packages are unsigned development builds. Linux requires glibc and the normal Avalonia desktop dependencies: X11/XWayland, fontconfig, libICE, libSM, and OpenGL/EGL or software rendering support. On Ubuntu: `sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libgl1 libegl1`.

The baseline is Windows 11, macOS 14+, and modern glibc Linux such as Ubuntu 24.04. Packages target x64 and ARM64 on all three platforms. Native Linux Wayland is not enabled; Wayland sessions use XWayland.

## Use

1. Select a drive/volume, browse to a folder, or paste an absolute directory path.
2. Click **Scan target**. Expand folders with the arrow, double-click, or use Right/Left. Enter and Space toggle expansion.
3. Inspect **Space on disk** and **% of scan**. Files and folders share the same size ordering. Select an item for its exact bytes, path, logical length, and hard-link attribution.
4. Open **Scan issues** for unreadable or changing entries. Cancel preserves the partial tree; Rescan starts a fresh observation.

Updates arrive at most four times per second, with tree refreshes reduced to once per second for scans exceeding 100,000 entries. Percentages during scanning use the currently discovered scan total and are explicitly provisional. Binary units (KiB, MiB, GiB) are used. An empty scan shows 0%.

## What is measured

- Folder allocation is its own filesystem-reported allocation plus the attributed allocations of descendants. Consequently a folder may exceed the sum of the children shown beneath it.
- Every percentage is relative to the selected scan root, rather than its immediate parent or the disk's capacity.
- Hard links are identified by volume/device and file ID/inode. Allocation is charged once to the ordinal-smallest full path within this scan. Other links show zero attributed bytes and retain their original measurement in the details panel. Attribution can move as earlier paths are discovered. This does not estimate space freed by deleting one link.
- Windows uses metadata-only handles and native allocation queries, including compressed/sparse size queries. Linux uses `statx` allocated 512-byte blocks. macOS uses native `lstat` allocated blocks.
- Hidden/system entries are included. Symlinks, junctions, and directory redirections are listed but never traversed. Other mounted filesystems are shown as boundaries with no storage attributed to the selected volume. Scan those volumes separately.
- Permission failures and unsupported measurements produce issues and incomplete totals. Unknown allocation is never replaced with logical file length. The issue panel retains up to 1,000 examples while counting all issues.
- The scanner reads metadata, not file contents, and does not hydrate cloud file contents or request elevation. Special files such as sockets, FIFOs, and devices are not opened for I/O.

These are filesystem-reported allocations, not exclusive physical block ownership or a snapshot. Copy-on-write clones, deduplication, snapshots, filesystem metadata, NTFS named streams, cloud/provider behavior, and inaccessible data can cause differences from the OS drive-usage total. The Windows implementation measures the default data stream; it does not enumerate named alternate streams. Files can change during a scan. Network filesystems and pseudo-filesystems are not primary v1 targets. macOS privacy restrictions may limit scans; the issues panel reports what could not be accessed.

## Build, test, package

```sh
dotnet build DiskSpace.slnx -c Release
dotnet test DiskSpace.slnx -c Release --logger trx
```

Tests cover accounting, deterministic hard links, cancellation, unknown measurements, mount/link boundaries, deep trees, native sparse files, Unicode/hidden files, long paths, platform-specific permissions/compression, UI expansion/selection, scan replacement, virtualization, and a synthetic million-entry scan. Native tests create disposable fixtures on the current host filesystem. Compression tests run on Windows; Unix permissions and `stat` comparisons run on Unix. Windows test fixtures require NTFS and `cmd`/`compact`; Unix sparse fixtures use `dd`.

Publish packages using PowerShell 7 (available as `pwsh` on all platforms; Windows PowerShell 5.1 also works):

```powershell
./scripts/Publish.ps1 -Runtime win-x64,win-arm64,osx-x64,osx-arm64,linux-x64,linux-arm64
```

The script creates self-contained ZIPs, preserves executable permissions, and creates macOS application bundles. Run it without `-Runtime` to target the current host. Build staging and artifacts stay under the ignored `artifacts/` directory. CI runs the tests on Windows, macOS, and Linux and publishes all six packages. Packaging for an architecture does not establish that it has been executed on that architecture.

For repeatable native launch checks:

```sh
DiskSpace.App --smoke-test /absolute/folder --screenshot /absolute/output.png
```

This opens a real platform window, scans the folder, writes a PNG and JSON report (state, allocation, entries, issues, mounted-volume choices, and realized row count), and exits. Use `--scan /absolute/folder` to start an interactive scan immediately.

## Structure

`DiskSpace.Core` owns the platform-independent scan engine, compact nodes, immutable row snapshots, sorting, and aggregation. `DiskSpace.Platform` supplies native metadata and volume adapters. `DiskSpace.App` uses Avalonia with MVVM and a virtualized flattened tree made from open-source controls. Tests use xUnit and Avalonia's headless platform. The scan model remains in memory, targeting approximately one million entries; only expanded branches are projected into rows and only viewport containers are realized.

Export, saved scans, cleanup/deletion, installers, signing/notarization, and automatic updates are deferred.
