using System.Diagnostics;
using DiskSpace.Core;
using Xunit;

namespace DiskSpace.Tests;

public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Scale")]
    public async Task MillionEntryFlatScanFitsInMemoryAndCanBeProjected()
    {
        const int count = 1_000_000;
        var root = Path.GetFullPath("synthetic-drive");
        var before = GC.GetTotalMemory(true);
        var clock = Stopwatch.StartNew();
        var session = new ScanService(new GeneratedFileSystem(root, count)).Start(root, TestContext.Current.CancellationToken);
        await session.Completion.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var scanTime = clock.Elapsed;
        Assert.Equal(ScanState.Complete, session.State);
        Assert.Equal(count + 1, session.GetProgress().Entries);
        Assert.Equal(count * 4096L, session.Root.TotalBytes);
        clock.Restart();
        var rows = session.GetRows(new HashSet<int> { 0 });
        var projectionTime = clock.Elapsed;
        Assert.Equal(count + 1, rows.Count);
        var retainedBytes = GC.GetTotalMemory(true) - before;
        output.WriteLine($"Entries={count:N0}; scan={scanTime}; project/sort={projectionTime}; retained={ByteSize.Format(retainedBytes)}");
        Assert.True(retainedBytes < 1024L * 1024 * 1024, $"Retained {ByteSize.Format(retainedBytes)}");
        // A collapsed subtree must not allocate one presentation object per underlying entry.
        Assert.Single(session.GetRows(new HashSet<int>()));
        GC.KeepAlive(rows);
    }

    private sealed class GeneratedFileSystem(string root, int count) : IFileSystem
    {
        public EntryMetadata ReadMetadata(string path) => path == root
            ? new(EntryKind.Directory, 0, 0, new(1, 0, 0), 1, 1)
            : new(EntryKind.File, 4096, 100, new(1, 0, ulong.Parse(Path.GetFileName(path))), 1, 1);
        public IEnumerable<string> EnumerateChildren(string path)
        {
            for (var i = count; i >= 1; i--) yield return Path.Combine(root, i.ToString("D7"));
        }
    }
}
