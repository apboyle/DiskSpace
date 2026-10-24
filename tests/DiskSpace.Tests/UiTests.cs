using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DiskSpace.App;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DiskSpace.Tests.TestAppBuilder))]

namespace DiskSpace.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DiskSpace.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task WindowScansAndExpandsWhileKeepingSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "diskspace-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        await File.WriteAllTextAsync(Path.Combine(root, "folder", "hello.txt"), "hello", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext!;
            vm.SelectedPath = root;
            await vm.StartScanAsync();
            Assert.False(vm.IsScanning);
            Assert.Equal("Scan complete", vm.Status);
            Assert.Equal(2, vm.Rows.Count);
            var folder = vm.Rows.Single(r => r.Name == "folder");
            vm.SelectedRow = folder;
            vm.Expand(folder);
            Assert.Equal(3, vm.Rows.Count);
            Assert.Equal(folder.Id, vm.SelectedRow?.Id);
            vm.Collapse(folder);
            Assert.Equal(2, vm.Rows.Count);
            Assert.Contains(root, vm.SelectionDetails);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task NewScanReplacesOldResultsAndRowsAreVirtualized()
    {
        var root = Path.Combine(Path.GetTempPath(), "diskspace-ui-" + Guid.NewGuid().ToString("N"));
        var replacement = Path.Combine(root, "replacement");
        Directory.CreateDirectory(replacement);
        for (var i = 0; i < 500; i++) await File.WriteAllTextAsync(Path.Combine(root, $"file-{i:D4}"), "test", TestContext.Current.CancellationToken);
        var window = new MainWindow();
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext!;
            vm.SelectedPath = root;
            var first = vm.StartScanAsync();
            vm.Cancel();
            vm.SelectedPath = replacement;
            var second = vm.StartScanAsync();
            await Task.WhenAll(first, second);
            Assert.Single(vm.Rows);
            Assert.Equal(replacement, vm.Rows[0].Name);
            Assert.Equal("Scan complete", vm.Status);
            vm.SelectedPath = root;
            await vm.StartScanAsync();
            window.UpdateLayout();
            Assert.True(vm.Rows.Count > 500);
            Assert.True(window.GetVisualDescendants().OfType<ListBoxItem>().Count() < 100);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }
}
