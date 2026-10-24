# Validation — 24 October 2026

| Environment | Automated suite | Native desktop launch | Extracted self-contained package |
| --- | --- | --- | --- |
| Windows x64, .NET SDK 10.0.401 | 22 passed | Passed; screenshot inspected | Passed |
| macOS 27.0.1, Apple Silicon, .NET SDK 10.0.401 | 22 passed | Passed; screenshot inspected | Passed via Launch Services |
| Ubuntu 24.04.4 x64, WSL2/WSLg, .NET SDK 10.0.401 | 22 passed | Passed; screenshot inspected | Passed after fresh `unzip`; executable bit preserved |

Platform-specific assertions run only on the relevant OS. Native fixtures verified allocated sizes, sparse allocation, hard-link identity/deduplication, link cycles, hidden/Unicode entries, and long paths. Windows additionally verified NTFS compression; Unix verified permission failures and agreement with system `stat`. Shared tests exercised sorting, unknown values, mount boundaries, cancellation, and failed roots. Headless UI tests exercised expansion, selection preservation, cancellation/replacement, and viewport virtualization.

The synthetic million-entry flat scan retained about **249 MiB** in the initial Windows measurement. Scanning took approximately **0.76 s** and sorting/projecting the expanded root approximately **0.67 s**. This measures the engine with generated metadata, not real filesystem traversal speed. The scale test enforces a retained-memory ceiling of 1 GiB.

PNG/JSON smoke reports and TRX test reports are retained under the ignored `artifacts/` directory. Portable ZIPs are in `artifacts/packages/`. SHA-256 checksums are in `artifacts/packages/SHA256SUMS.txt`.

Windows ARM64, macOS x64, and Linux ARM64 packages were cross-published but not executed on corresponding hardware. Rosetta was unavailable on the Mac. No installers, signing, notarization, or publishing were performed. CI configuration was added but has not run on a hosted CI service.

WSL's pre-existing system `dotnet` executable could not find `libhostfxr`. Testing used an isolated Linux SDK under `.tools/dotnet` in the Windows workspace, with test sources/fixtures on WSL's native filesystem. The Mac test workspace is `~/diskspace-codex-01a16b2b`. The Mac display needed to be awake for native Avalonia GUI launch; a temporary `caffeinate -u` invocation enabled the package test without changing persistent power settings.

Known accounting boundaries are documented in README.md, including filesystem overhead, copy-on-write sharing, snapshots, and Windows named alternate streams.
