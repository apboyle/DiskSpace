using System.Diagnostics;

namespace DiskSpace.Core;

public enum ScanState { Scanning, Complete, Incomplete, Cancelled, Failed }
public sealed record ScanIssue(string Path, string Reason);

// Scan nodes are owned by the scanner. Readers must hold ScanSession.SyncRoot.
public sealed class ScanNode
{
    internal ScanNode(int id, string name, ScanNode? parent)
    { Id = id; Name = name; Parent = parent; }

    public int Id { get; }
    public string Name { get; }
    public ScanNode? Parent { get; }
    public EntryKind Kind { get; internal set; }
    public long? ReportedAllocatedBytes { get; internal set; }
    public long AttributedBytes { get; internal set; }
    public long TotalBytes { get; internal set; }
    public long LogicalBytes { get; internal set; }
    public int IssueCount { get; internal set; }
    public string? Note { get; internal set; }
    public bool Enumerated { get; internal set; }
    public ScanNode? SharedWith { get; internal set; }
    public IReadOnlyList<ScanNode> Children => ChildNodes ?? (IReadOnlyList<ScanNode>)Array.Empty<ScanNode>();
    internal List<ScanNode>? ChildNodes;
    internal ScanNode[]? SortedChildren;
    internal bool SortDirty;

    public string FullPath
    {
        get
        {
            var names = new Stack<string>();
            ScanNode? node = this;
            while (node?.Parent is not null) { names.Push(node.Name); node = node.Parent; }
            var path = node?.Name ?? "";
            while (names.TryPop(out var name)) path = Path.Combine(path, name);
            return path;
        }
    }

    internal ScanNode[] GetSortedChildren()
    {
        if (ChildNodes is null) return [];
        if (SortedChildren is null || SortDirty)
        {
            SortedChildren = ChildNodes.ToArray();
            Array.Sort(SortedChildren, (a, b) =>
            {
                var size = b.TotalBytes.CompareTo(a.TotalBytes);
                return size != 0 ? size : StringComparer.Ordinal.Compare(a.Name, b.Name);
            });
            SortDirty = false;
        }
        return SortedChildren;
    }
}

public readonly record struct ScanProgress(ScanState State, long Entries, long Bytes,
    int Issues, string CurrentPath, TimeSpan Elapsed, long Revision);

public sealed class ScanSession
{
    internal ScanSession(string path)
    {
        Root = new ScanNode(0, path, null) { Kind = EntryKind.Directory };
        CurrentPath = path;
    }

    public object SyncRoot { get; } = new();
    public ScanNode Root { get; }
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public ScanState State { get; internal set; } = ScanState.Scanning;
    public int IssueCount { get; internal set; }
    public IReadOnlyList<ScanIssue> Issues => IssueSamples;
    internal readonly List<ScanIssue> IssueSamples = [];
    internal readonly Stopwatch Watch = Stopwatch.StartNew();
    internal string CurrentPath;
    internal long Entries;
    internal long Revision;

    public ScanProgress GetProgress()
    {
        lock (SyncRoot) return new(State, Entries, Root.TotalBytes, IssueCount, CurrentPath, Watch.Elapsed, Revision);
    }

    public IReadOnlyList<ScanIssue> GetIssues()
    {
        lock (SyncRoot) return IssueSamples.ToArray();
    }

    public List<TreeRow> GetRows(ISet<int> expanded)
    {
        lock (SyncRoot)
        {
            var rows = new List<TreeRow>();
            var stack = new Stack<(ScanNode Node, int Depth)>();
            stack.Push((Root, 0));
            while (stack.TryPop(out var entry))
            {
                var node = entry.Node;
                rows.Add(new(node, entry.Depth, expanded.Contains(node.Id), Root.TotalBytes, State));
                if (!expanded.Contains(node.Id)) continue;
                var children = node.GetSortedChildren();
                for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], entry.Depth + 1));
            }
            return rows;
        }
    }
}

// A value per expanded row; only realized list containers allocate presentation objects.
public readonly record struct TreeRow
{
    public TreeRow(ScanNode node, int depth, bool expanded, long scanBytes, ScanState scanState)
    {
        Node = node; Depth = depth; Expanded = expanded; ScanBytes = scanBytes; ScanState = scanState;
        TotalBytes = node.TotalBytes; OwnBytes = node.ReportedAllocatedBytes; LogicalBytes = node.LogicalBytes;
        Kind = node.Kind; Note = node.Note; HasIssues = node.IssueCount > 0;
        SharedWith = node.SharedWith; HasChildren = node.Children.Count > 0;
    }
    public ScanNode Node { get; }
    public int Depth { get; }
    public bool Expanded { get; }
    public long ScanBytes { get; }
    public ScanState ScanState { get; }
    public long TotalBytes { get; }
    public long? OwnBytes { get; }
    public long LogicalBytes { get; }
    public EntryKind Kind { get; }
    public string? Note { get; }
    public bool HasIssues { get; }
    public ScanNode? SharedWith { get; }
    public bool HasChildren { get; }
    public int Id => Node.Id;
    public string Name => Node.Name;
    public string Glyph => Kind == EntryKind.Directory ? (Expanded ? "▾" : "▸") : Kind == EntryKind.Link ? "↗" : "·";
    public bool CanExpand => Kind == EntryKind.Directory && Note != "Other mounted filesystem";
    public double Indent => Depth * 20;
    public double Percentage => ScanBytes == 0 ? 0 : 100d * TotalBytes / ScanBytes;
    public string PercentageText => $"{Percentage:0.0}%";
    public string SizeText => OwnBytes is null && !HasChildren ? "Unknown" : ByteSize.Format(TotalBytes);
    public string Status => SharedWith is not null ? "Shared hard link"
        : Note ?? (HasIssues ? "Incomplete" : ScanState is ScanState.Scanning or ScanState.Cancelled ? "Partial" : "");
}
