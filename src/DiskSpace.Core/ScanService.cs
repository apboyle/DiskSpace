namespace DiskSpace.Core;

public sealed class ScanService(IFileSystem fileSystem)
{
    public ScanSession Start(string root, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var session = new ScanSession(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        session.Completion = Task.Run(() => Run(session, cancellationToken), CancellationToken.None);
        return session;
    }

    private void Run(ScanSession session, CancellationToken token)
    {
        var hardLinks = new Dictionary<FileIdentity, List<ScanNode>>();
        var pending = new Stack<ScanNode>();
        var nextId = 1;
        try
        {
            token.ThrowIfCancellationRequested();
            var rootMetadata = fileSystem.ReadMetadata(session.Root.FullPath);
            if (rootMetadata.Kind != EntryKind.Directory)
                throw new IOException("Choose a directory or volume, rather than a file or symbolic link.");
            lock (session.SyncRoot)
            {
                ApplyMetadata(session, session.Root, rootMetadata, hardLinks);
                session.Entries = 1;
            }
            pending.Push(session.Root);
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                var directoryPath = directory.FullPath;
                lock (session.SyncRoot) session.CurrentPath = directoryPath;
                try
                {
                    foreach (var path in fileSystem.EnumerateChildren(directoryPath))
                    {
                        token.ThrowIfCancellationRequested();
                        EntryMetadata metadata = default;
                        Exception? error = null;
                        try { metadata = fileSystem.ReadMetadata(path); }
                        catch (Exception ex) when (IsFileError(ex)) { error = ex; }
                        lock (session.SyncRoot)
                        {
                            var child = new ScanNode(nextId++, Path.GetFileName(path), directory);
                            (directory.ChildNodes ??= []).Add(child);
                            directory.SortDirty = true;
                            session.Entries++;
                            if (error is not null) RecordIssue(session, child, path, error.Message);
                            else
                            {
                                ApplyMetadata(session, child, metadata, hardLinks);
                                if (metadata.Kind == EntryKind.Directory)
                                {
                                    var otherMount = metadata.Identity.Device != rootMetadata.Identity.Device ||
                                        (rootMetadata.MountId != 0 && metadata.MountId != 0 && metadata.MountId != rootMetadata.MountId);
                                    if (otherMount)
                                    {
                                        AdjustAllocation(child, -child.AttributedBytes);
                                        child.AttributedBytes = 0;
                                        child.ReportedAllocatedBytes = 0;
                                        child.Note = "Other mounted filesystem";
                                    }
                                    else pending.Push(child);
                                }
                            }
                            session.Revision++;
                        }
                    }
                    lock (session.SyncRoot) directory.Enumerated = true;
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    lock (session.SyncRoot) RecordIssue(session, directory, directoryPath, ex.Message);
                }
            }
            token.ThrowIfCancellationRequested();
            lock (session.SyncRoot) session.State = session.IssueCount == 0 ? ScanState.Complete : ScanState.Incomplete;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { lock (session.SyncRoot) session.State = ScanState.Cancelled; }
        catch (Exception ex)
        {
            lock (session.SyncRoot)
            {
                RecordIssue(session, session.Root, session.Root.FullPath, ex.Message);
                session.State = ScanState.Failed;
            }
        }
        finally
        {
            lock (session.SyncRoot) { session.Watch.Stop(); session.Revision++; }
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or NotSupportedException or System.Security.SecurityException;

    private static void ApplyMetadata(ScanSession session, ScanNode node, EntryMetadata metadata,
        Dictionary<FileIdentity, List<ScanNode>> links)
    {
        node.Kind = metadata.Kind;
        node.ReportedAllocatedBytes = metadata.AllocatedBytes;
        node.LogicalBytes = metadata.LogicalBytes;
        if (metadata.Kind == EntryKind.Link) node.Note = "Link · target not followed";
        if (metadata.Kind == EntryKind.Other) node.Note = "Special file · not opened";
        if (metadata.AllocatedBytes is null)
            RecordIssue(session, node, node.FullPath, "Allocated storage could not be measured.");
        var allocation = metadata.AllocatedBytes ?? 0;
        if (allocation < 0) throw new IOException("The filesystem returned a negative allocation.");
        node.AttributedBytes = allocation;
        if (metadata.Kind == EntryKind.File && metadata.LinkCount > 1)
        {
            if (links.TryGetValue(metadata.Identity, out var group))
            {
                var owner = group[0];
                if (StringComparer.Ordinal.Compare(node.FullPath, owner.FullPath) < 0)
                {
                    AdjustAllocation(owner, -owner.AttributedBytes);
                    owner.AttributedBytes = 0;
                    foreach (var link in group) link.SharedWith = node;
                    group.Insert(0, node);
                }
                else { node.SharedWith = owner; node.AttributedBytes = 0; group.Add(node); }
            }
            else links.Add(metadata.Identity, [node]);
        }
        AdjustAllocation(node, node.AttributedBytes);
    }

    private static void AdjustAllocation(ScanNode node, long delta)
    {
        for (ScanNode? ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.TotalBytes = checked(ancestor.TotalBytes + delta);
            if (ancestor.Parent is not null) ancestor.Parent.SortDirty = true;
        }
    }

    private static void RecordIssue(ScanSession session, ScanNode node, string path, string message)
    {
        session.IssueCount++;
        if (session.IssueSamples.Count < 1000) session.IssueSamples.Add(new(path, message));
        for (ScanNode? ancestor = node; ancestor is not null; ancestor = ancestor.Parent) ancestor.IssueCount++;
        session.Revision++;
    }
}
