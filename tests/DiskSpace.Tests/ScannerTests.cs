using DiskSpace.Core;
using Xunit;

namespace DiskSpace.Tests;

public sealed class ScannerTests
{
    private static readonly string Root = Path.GetFullPath("test-volume");
    private static string P(string name) => Path.Combine(Root, name);

    [Fact]
    public async Task AggregatesOwnAllocationAndSortsMixedSiblings()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 4);
        fs.Add("small.txt", EntryKind.File, 100);
        fs.Add("folder", EntryKind.Directory, 8);
        fs.Add("folder/large.bin", EntryKind.File, 400);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(ScanState.Complete, scan.State);
        Assert.Equal(512, scan.Root.TotalBytes);
        var rows = scan.GetRows(new HashSet<int> { 0 });
        Assert.Equal(new[] { Root, "folder", "small.txt" }, rows.Select(r => r.Name));
        Assert.Equal(100, rows[0].Percentage);
        Assert.Equal(408d / 512 * 100, rows[1].Percentage, 8);
        Assert.Equal(0, scan.IssueCount);
    }

    [Fact]
    public async Task HardLinksHaveStableOwnerEvenWhenEarlierPathArrivesLast()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        var identity = new FileIdentity(1, 0, 500);
        fs.Add("z", EntryKind.File, 4096, identity, 3);
        fs.Add("m", EntryKind.File, 4096, identity, 3);
        fs.Add("a", EntryKind.File, 4096, identity, 3);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(4096, scan.Root.TotalBytes);
        var owner = scan.Root.Children.Single(n => n.Name == "a");
        Assert.Equal(4096, owner.AttributedBytes);
        Assert.All(scan.Root.Children.Where(n => n != owner), n =>
        { Assert.Equal(0, n.AttributedBytes); Assert.Same(owner, n.SharedWith); Assert.Equal(4096, n.ReportedAllocatedBytes); });
    }

    [Fact]
    public async Task DoesNotMergeEqualInodesFromDifferentDevices()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        fs.Add("a", EntryKind.File, 512, new(1, 0, 500), 2);
        fs.Add("b", EntryKind.File, 512, new(2, 0, 500), 2);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(1024, scan.Root.TotalBytes);
    }

    [Fact]
    public async Task LinksAndMountBoundariesAreNotTraversed()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        fs.Add("link", EntryKind.Link, 0);
        fs.Add("other-volume", EntryKind.Directory, 8192, new(2, 0, 2));
        fs.Add("bind-mount", EntryKind.Directory, 8192, mount: 2);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(new[] { Root }, fs.Enumerated);
        Assert.Equal(0, scan.Root.TotalBytes);
        Assert.Equal(2, scan.Root.Children.Count(n => n.Note == "Other mounted filesystem"));
    }

    [Fact]
    public async Task ReportsDisappearingAndInaccessibleEntriesWithoutLosingSiblings()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        fs.Add("gone", EntryKind.File, 10);
        fs.Add("denied", EntryKind.Directory, 0);
        fs.Add("good", EntryKind.File, 20);
        fs.ReadErrors[P("gone")] = new FileNotFoundException("Disappeared");
        fs.EnumerationErrors[P("denied")] = new UnauthorizedAccessException("Denied");
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(ScanState.Incomplete, scan.State);
        Assert.Equal(2, scan.IssueCount);
        Assert.Equal(20, scan.Root.TotalBytes);
        Assert.Equal(2, scan.Root.IssueCount);
        Assert.Equal("Unknown", scan.GetRows(new HashSet<int> { 0 }).Single(r => r.Name == "gone").SizeText);
    }

    [Fact]
    public async Task UnknownAllocationDoesNotFallBackToLogicalLength()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        fs.Add("unknown", EntryKind.File, null);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(ScanState.Incomplete, scan.State);
        Assert.Equal(0, scan.Root.TotalBytes);
        Assert.All(scan.GetRows(new HashSet<int> { 0 }), r => Assert.Equal(0, r.Percentage));
    }

    [Fact]
    public async Task CancellationRetainsPartialResults()
    {
        using var cts = new CancellationTokenSource();
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        fs.Add("a", EntryKind.File, 50);
        fs.Add("b", EntryKind.File, 60);
        fs.OnRead = path => { if (path == P("b")) cts.Cancel(); };
        var scan = new ScanService(fs).Start(Root, cts.Token);
        await scan.Completion;
        Assert.Equal(ScanState.Cancelled, scan.State);
        Assert.True(scan.Root.TotalBytes >= 50);
        Assert.True(scan.Root.Children.Count >= 1);
    }

    [Fact]
    public async Task PrecancelledAndInvalidRootFinishCleanly()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var fs = new FakeFileSystem(Root);
        var cancelled = new ScanService(fs).Start(Root, cts.Token);
        await cancelled.Completion;
        Assert.Equal(ScanState.Cancelled, cancelled.State);
        var failed = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await failed.Completion;
        Assert.Equal(ScanState.Failed, failed.State);
        Assert.Single(failed.GetIssues());
    }

    [Fact]
    public async Task DeepTreesUseIterativeTraversal()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        var relative = "";
        for (var i = 0; i < 500; i++)
        { relative = Path.Combine(relative, "d"); fs.Add(relative, EntryKind.Directory, 1); }
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(ScanState.Complete, scan.State);
        Assert.Equal(500, scan.Root.TotalBytes);
    }

    [Fact]
    public async Task EqualSizesSortByOrdinalName()
    {
        var fs = new FakeFileSystem(Root);
        fs.Add("", EntryKind.Directory, 0);
        foreach (var name in new[] { "z", "b", "a" }) fs.Add(name, EntryKind.File, 100);
        var scan = new ScanService(fs).Start(Root, TestContext.Current.CancellationToken);
        await scan.Completion;
        Assert.Equal(new[] { "a", "b", "z" }, scan.GetRows(new HashSet<int> { 0 }).Skip(1).Select(r => r.Name));
    }
}

internal sealed class FakeFileSystem(string root) : IFileSystem
{
    private readonly Dictionary<string, EntryMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);
    public Dictionary<string, Exception> ReadErrors { get; } = [];
    public Dictionary<string, Exception> EnumerationErrors { get; } = [];
    public List<string> Enumerated { get; } = [];
    public Action<string>? OnRead { get; set; }
    public void Add(string relative, EntryKind kind, long? bytes, FileIdentity? identity = null, uint links = 1, ulong mount = 1)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        var path = relative.Length == 0 ? root : Path.Combine(root, relative);
        _metadata.Add(path, new(kind, bytes, bytes ?? 10000, identity ?? new(1, 0, (ulong)_metadata.Count + 1), mount, links));
        if (relative.Length != 0) (_children.TryGetValue(Path.GetDirectoryName(path)!, out var siblings)
            ? siblings : _children[Path.GetDirectoryName(path)!] = []).Add(path);
    }
    public EntryMetadata ReadMetadata(string path)
    { OnRead?.Invoke(path); if (ReadErrors.TryGetValue(path, out var ex)) throw ex; return _metadata.TryGetValue(path, out var value) ? value : throw new FileNotFoundException(path); }
    public IEnumerable<string> EnumerateChildren(string path)
    { Enumerated.Add(path); if (EnumerationErrors.TryGetValue(path, out var ex)) throw ex; return _children.TryGetValue(path, out var values) ? values : []; }
}
