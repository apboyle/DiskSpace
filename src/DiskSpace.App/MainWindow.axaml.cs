using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiskSpace.Core;

namespace DiskSpace.App;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private Vector _scrollOffset;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.RowsChanging += () => _scrollOffset = TreeList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
        _viewModel.RowsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (TreeList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll) scroll.Offset = _scrollOffset;
        }, DispatcherPriority.Loaded);
        Opened += OnOpened;
        Closing += (_, _) => _viewModel.Dispose();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.LoadVolumesAsync();
        var args = Program.Arguments;
        var smoke = Array.IndexOf(args, "--smoke-test");
        var scan = smoke >= 0 ? smoke : Array.IndexOf(args, "--scan");
        if (scan < 0 || scan + 1 >= args.Length) return;
        _viewModel.SelectedPath = args[scan + 1];
        await _viewModel.StartScanAsync();
        if (smoke < 0) return;
        // Deterministic diagnostic mode exercises an actual platform window and exits itself.
        await Task.Delay(800);
        var screenshotArg = Array.IndexOf(args, "--screenshot");
        var output = screenshotArg >= 0 && screenshotArg + 1 < args.Length ? args[screenshotArg + 1] : "diskspace-smoke.png";
        var code = 0;
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render(this);
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
            var progress = _viewModel.Session!.GetProgress();
            File.WriteAllText(output + ".json", JsonSerializer.Serialize(new
            {
                State = progress.State.ToString(), progress.Bytes, progress.Entries, progress.Issues,
                VisibleRows = _viewModel.Rows.Count,
                RealizedRows = TreeList.GetVisualDescendants().OfType<ListBoxItem>().Count(),
                Volumes = _viewModel.Volumes.Select(v => v.Path),
                Errors = _viewModel.Session.GetIssues()
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (progress.State == ScanState.Failed) code = 1;
        }
        catch (Exception ex) { File.WriteAllText(output + ".error.txt", ex.ToString()); code = 1; }
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(code);
    }

    private async void BrowseClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to analyze", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _viewModel.SelectedPath = path;
    }
    private async void ScanClicked(object? sender, RoutedEventArgs e) => await _viewModel.StartScanAsync();
    private void CancelClicked(object? sender, RoutedEventArgs e) => _viewModel.Cancel();
    private void ExpanderClicked(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: TreeRow row }) { _viewModel.Toggle(row); e.Handled = true; } }
    private void TreeDoubleTapped(object? sender, TappedEventArgs e)
    { if (TreeList.SelectedItem is TreeRow row) _viewModel.Toggle(row); }
    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (TreeList.SelectedItem is not TreeRow row) return;
        if (e.Key == Key.Right) { _viewModel.Expand(row); e.Handled = true; }
        else if (e.Key == Key.Left) { _viewModel.Collapse(row); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Space) { _viewModel.Toggle(row); e.Handled = true; }
    }
}

public sealed class IndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new Thickness(value is double indent ? indent : 0, 0, 0, 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
