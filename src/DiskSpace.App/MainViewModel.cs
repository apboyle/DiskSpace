using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DiskSpace.Core;
using DiskSpace.Platform;

namespace DiskSpace.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ScanService _scanner = new(new NativeFileSystem());
    private readonly HashSet<int> _expanded = [0];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CancellationTokenSource? _cancellation;
    private ScanSession? _session;
    private long _revision = -1;
    private string _selectedPath = "";
    private VolumeInfo? _selectedVolume;
    private TreeRow? _selectedRow;
    private long _selectedId = -1;
    private DateTime _lastTreeRefresh;

    public MainViewModel()
    {
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RowsChanging;
    public event Action? RowsChanged;
    public IReadOnlyList<VolumeInfo> Volumes { get; private set; } = [];
    public List<TreeRow> Rows { get; private set; } = [];
    public IReadOnlyList<ScanIssue> Issues { get; private set; } = [];
    public string SelectedPath { get => _selectedPath; set { _selectedPath = value; Changed(); Changed(nameof(CanScan)); } }
    public VolumeInfo? SelectedVolume
    {
        get => _selectedVolume;
        set { _selectedVolume = value; Changed(); if (value is not null) SelectedPath = value.Path; Changed(nameof(VolumeDescription)); }
    }
    public string VolumeDescription => SelectedVolume is null ? "Choose a drive or browse to a folder."
        : $"{ByteSize.Format(SelectedVolume.AvailableBytes)} available of {ByteSize.Format(SelectedVolume.TotalBytes)}";
    public TreeRow? SelectedRow
    {
        get => _selectedRow;
        set { _selectedRow = value; if (value is { } row) _selectedId = row.Id; Changed(); Changed(nameof(SelectionDetails)); }
    }
    public string SelectionDetails
    {
        get
        {
            if (SelectedRow is not { } row) return "Select an item to inspect its path and storage measurements.";
            var node = row.Node;
            var allocation = row.OwnBytes is { } bytes ? $"{bytes:N0} bytes" : "unknown";
            var shared = row.SharedWith is null ? "" : $"\nStorage charged to: {row.SharedWith.FullPath}";
            return $"{node.FullPath}\nSubtree: {row.TotalBytes:N0} bytes  ·  Own allocation: {allocation}  ·  File length: {row.LogicalBytes:N0} bytes{shared}";
        }
    }
    public bool IsScanning { get; private set; }
    public bool CanScan => !IsScanning && !string.IsNullOrWhiteSpace(SelectedPath);
    public bool HasResults => _session is not null;
    public string ScanButtonText => HasResults ? "Rescan target" : "Scan target";
    public bool IsEmpty => _session is null;
    public string Status { get; private set; } = "Ready to scan";
    public string Size { get; private set; } = "—";
    public string Entries { get; private set; } = "—";
    public string Elapsed { get; private set; } = "—";
    public string CurrentPath { get; private set; } = "Your files stay on your computer.";
    public string IssueSummary { get; private set; } = "Scan issues (0)";
    public string MeasurementNote { get; private set; } = "Allocated storage · hard links counted once";
    public ScanSession? Session => _session;

    public async Task LoadVolumesAsync()
    {
        try
        {
            Volumes = await Task.Run(() => new VolumeProvider().GetVolumes());
            Changed(nameof(Volumes));
            if (string.IsNullOrEmpty(SelectedPath)) SelectedVolume = OperatingSystem.IsMacOS()
                ? Volumes.FirstOrDefault(v => v.Path.TrimEnd('/') == "/System/Volumes/Data") ?? Volumes.FirstOrDefault()
                : Volumes.FirstOrDefault();
        }
        catch (Exception ex) { Status = "Drive list unavailable"; CurrentPath = ex.Message; Changed(nameof(Status)); Changed(nameof(CurrentPath)); }
    }

    public async Task StartScanAsync()
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var session = _scanner.Start(SelectedPath, cancellation.Token);
            _session = session;
            _expanded.Clear(); _expanded.Add(0);
            _selectedRow = null; _selectedId = -1;
            _revision = -1;
            Changed(nameof(HasResults)); Changed(nameof(IsEmpty)); Changed(nameof(ScanButtonText));
            Refresh(true);
            await session.Completion;
            if (ReferenceEquals(_session, session)) Refresh(true);
        }
        catch (Exception ex)
        {
            Status = "Could not start scan"; CurrentPath = ex.Message;
            Changed(nameof(Status)); Changed(nameof(CurrentPath));
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void Toggle(TreeRow row)
    {
        if (!row.CanExpand) return;
        if (!_expanded.Add(row.Id)) _expanded.Remove(row.Id);
        Refresh(true);
    }
    public void Expand(TreeRow row) { if (row.CanExpand && _expanded.Add(row.Id)) Refresh(true); }
    public void Collapse(TreeRow row) { if (_expanded.Remove(row.Id)) Refresh(true); }

    public void Refresh(bool force = false)
    {
        if (_session is not { } session) return;
        var progress = session.GetProgress();
        if (!force && progress.Revision == _revision && progress.State != ScanState.Scanning) return;
        IsScanning = progress.State == ScanState.Scanning;
        Status = progress.State switch
        {
            ScanState.Scanning => "Scanning · provisional", ScanState.Complete => "Scan complete",
            ScanState.Incomplete => "Complete · some items unreadable", ScanState.Cancelled => "Cancelled · partial results", _ => "Scan failed"
        };
        Size = ByteSize.Format(progress.Bytes);
        Entries = progress.Entries.ToString("N0");
        Elapsed = progress.Elapsed.TotalHours >= 1 ? progress.Elapsed.ToString(@"h\:mm\:ss") : progress.Elapsed.ToString(@"m\:ss");
        CurrentPath = IsScanning ? progress.CurrentPath : session.Root.FullPath;
        IssueSummary = $"Scan issues ({progress.Issues:N0})";
        MeasurementNote = IsScanning || progress.State == ScanState.Cancelled ? "Provisional totals · % of discovered scan storage"
            : progress.State is ScanState.Incomplete or ScanState.Failed ? "Incomplete totals · % of measured scan storage"
            : "Allocated storage · % of whole scan · hard links counted once";
        foreach (var property in new[] { nameof(IsScanning), nameof(CanScan), nameof(Status), nameof(Size), nameof(Entries), nameof(Elapsed), nameof(CurrentPath), nameof(IssueSummary), nameof(MeasurementNote) }) Changed(property);
        if (!force && IsScanning && progress.Entries > 100_000 && (DateTime.UtcNow - _lastTreeRefresh).TotalSeconds < 1) return;
        if (force || progress.Revision != _revision)
        {
            RowsChanging?.Invoke();
            Rows = session.GetRows(_expanded);
            _revision = progress.Revision;
            _lastTreeRefresh = DateTime.UtcNow;
            Issues = session.GetIssues();
            Changed(nameof(Rows)); Changed(nameof(Issues));
            SelectedRow = _selectedId < 0 ? null : Rows.Where(r => r.Id == _selectedId).Select(r => (TreeRow?)r).FirstOrDefault();
            RowsChanged?.Invoke();
        }
    }

    public void Dispose() { _timer.Stop(); _cancellation?.Cancel(); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
