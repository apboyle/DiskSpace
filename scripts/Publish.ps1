param(
    [ValidateSet('win-x64','win-arm64','osx-x64','osx-arm64','linux-x64','linux-arm64')]
    [string[]] $Runtime = @(),
    [string] $OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/packages' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!$Runtime.Count) { $Runtime = @([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier) }
$validRuntimes = @('win-x64','win-arm64','osx-x64','osx-arm64','linux-x64','linux-arm64')
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($rid in $Runtime) {
    if ($rid -notin $validRuntimes) { throw "Unsupported runtime: $rid" }
    # Each invocation gets a clean staging folder without deleting any existing output tree.
    $stage = Join-Path $repo "artifacts/staging/$rid-$([Guid]::NewGuid().ToString('N'))"
    $publish = Join-Path $stage 'DiskSpace'
    if ($rid.StartsWith('osx-')) { $publish = Join-Path $stage 'DiskSpace.app/Contents/MacOS' }
    & dotnet publish (Join-Path $repo 'src/DiskSpace.App/DiskSpace.App.csproj') -c Release -r $rid --self-contained true -o $publish '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
    Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $stage 'README.md')
    if ($rid.StartsWith('osx-')) {
        $plist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>DiskSpace</string>
  <key>CFBundleDisplayName</key><string>DiskSpace</string>
  <key>CFBundleIdentifier</key><string>io.diskspace.desktop</string>
  <key>CFBundleExecutable</key><string>DiskSpace.App</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
'@
        [IO.File]::WriteAllText((Join-Path $stage 'DiskSpace.app/Contents/Info.plist'), $plist)
    }
    $archive = Join-Path $OutputDirectory "DiskSpace-$rid.zip"
    # Create a new file first, so an interrupted publish cannot damage the previous package.
    $temporary = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($entry in $zip.Entries) {
            # Preserve executable mode even when cross-publishing on Windows.
            $mode = if (!$entry.Name) { 0x41ED } elseif ($entry.Name -eq 'DiskSpace.App') { 0x81ED } else { 0x81A4 }
            $entry.ExternalAttributes = [int](([long]$mode * 65536) - 4294967296L)
        }
    } finally { $zip.Dispose() }
    # ZipArchive writes its own host OS into "version made by". Unix unzip ignores
    # Unix mode bits when that host is DOS/Windows, so mark our central records as Unix.
    # These generated packages have no archive comment and are below ZIP64 limits.
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    try {
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
        $stream.Position = $stream.Length - 22
        $end = $reader.ReadBytes(22)
        if ([BitConverter]::ToUInt32($end, 0) -ne 0x06054B50) { throw 'Unexpected ZIP end record' }
        $count = [BitConverter]::ToUInt16($end, 10)
        $position = [long][BitConverter]::ToUInt32($end, 16)
        if ($count -eq 65535 -or $position -eq 4294967295L) { throw 'ZIP64 packages are not supported by this packager' }
        for ($index = 0; $index -lt $count; $index++) {
            $stream.Position = $position
            $header = $reader.ReadBytes(46)
            if ([BitConverter]::ToUInt32($header, 0) -ne 0x02014B50) { throw 'Unexpected ZIP central record' }
            $stream.Position = $position + 5
            $stream.WriteByte(3)
            $position += 46 + [BitConverter]::ToUInt16($header, 28) + [BitConverter]::ToUInt16($header, 30) + [BitConverter]::ToUInt16($header, 32)
        }
        $reader.Dispose()
    } finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archive -Force
    Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-Table Path,Hash -AutoSize
}
