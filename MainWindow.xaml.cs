using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using WindowsCompressor.Models;
using WindowsCompressor.Services;

namespace WindowsCompressor;

public partial class MainWindow : Window
{
    private readonly CompressionService _compression = new();
    private CancellationTokenSource? _compressionCancellation;
    private bool _isBusy;

    public ObservableCollection<CompressionItem> Items { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        OutputTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "Compressed");
        UpdateQueueSummary();
        UpdateCompressionModeUI();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var preference = 1;
            _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch
        {
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Add files to Compressor",
            Multiselect = true,
            Filter = "All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            AddPaths(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Add a folder",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            AddPaths([dialog.FolderName]);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose output folder",
            Multiselect = false,
            InitialDirectory = Directory.Exists(OutputTextBox.Text) ? OutputTextBox.Text : null
        };
        if (dialog.ShowDialog(this) == true)
            OutputTextBox.Text = dialog.FolderName;
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = OutputTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetFooter("ERROR", ex.Message);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = QueueGrid.SelectedItems.Cast<CompressionItem>().ToArray();
        foreach (var item in selected)
            Items.Remove(item);
        UpdateQueueSummary();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Items.Clear();
        OverallProgress.Value = 0;
        UpdateQueueSummary();
        SetFooter("READY", "Add files to begin.");
    }

    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || Items.Count == 0) return;

        var outputFolder = OutputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            SetFooter("ERROR", "Choose an output folder first.");
            return;
        }

        try
        {
            Directory.CreateDirectory(outputFolder);
        }
        catch (Exception ex)
        {
            SetFooter("ERROR", ex.Message);
            return;
        }

        double? targetMegabytes = null;
        if (SelectedText(CompressionModeCombo).Equals("Target size", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetTargetMegabytes(out var parsedTarget))
            {
                SetFooter("ERROR", "Enter a target size between 0.1 MB and 100000 MB.");
                TargetSizeTextBox.Focus();
                TargetSizeTextBox.SelectAll();
                return;
            }
            targetMegabytes = parsedTarget;
        }

        _isBusy = true;
        _compressionCancellation = new CancellationTokenSource();
        SetBusyVisuals(true);

        var snapshot = Items.ToArray();
        var completed = 0;
        var failed = 0;
        var totalSaved = 0L;
        var wasCanceled = false;
        var quality = SelectedText(QualityCombo);
        var format = SelectedText(FormatCombo);

        foreach (var item in snapshot)
        {
            item.Progress = 0;
            item.Result = "—";
            item.Status = "Waiting";
        }

        try
        {
            for (var i = 0; i < snapshot.Length; i++)
            {
                var item = snapshot[i];
                _compressionCancellation.Token.ThrowIfCancellationRequested();
                item.Status = "Compressing";
                SetFooter("WORKING", item.Name);

                var itemIndex = i;
                var progress = new Progress<double>(value =>
                {
                    item.Progress = value;
                    OverallProgress.Value = ((itemIndex + value / 100d) / snapshot.Length) * 100d;
                });
                var status = new Progress<string>(text => SetFooter("WORKING", text));

                try
                {
                    var result = await _compression.CompressAsync(
                        item,
                        outputFolder,
                        quality,
                        format,
                        targetMegabytes,
                        progress,
                        status,
                        _compressionCancellation.Token);

                    item.Progress = 100;
                    var delta = item.OriginalBytes - result.OutputBytes;
                    totalSaved += Math.Max(0, delta);
                    if (delta >= 0)
                    {
                        var percentage = item.OriginalBytes == 0 ? 0 : delta * 100d / item.OriginalBytes;
                        item.Status = "Done";
                        item.Result = $"{CompressionItem.FormatBytes(result.OutputBytes)}  −{percentage:0.#}%";
                    }
                    else
                    {
                        var percentage = item.OriginalBytes == 0 ? 0 : -delta * 100d / item.OriginalBytes;
                        item.Status = "Larger";
                        item.Result = $"{CompressionItem.FormatBytes(result.OutputBytes)}  +{percentage:0.#}%";
                    }
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "Canceled";
                    wasCanceled = true;
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.Result = "ERROR";
                    failed++;
                    SetFooter("ERROR", $"{item.Name}: {Condense(ex.Message)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
        }
        finally
        {
            _isBusy = false;
            _compressionCancellation.Dispose();
            _compressionCancellation = null;
            SetBusyVisuals(false);
        }

        if (wasCanceled)
        {
            SetFooter("CANCELED", $"{completed} completed before cancellation.");
        }
        else
        {
            OverallProgress.Value = 100;
            var detail = failed == 0
                ? $"{completed} completed · saved {CompressionItem.FormatBytes(totalSaved)}"
                : $"{completed} completed · {failed} failed · saved {CompressionItem.FormatBytes(totalSaved)}";
            SetFooter(failed == 0 ? "DONE" : "DONE / ERRORS", detail);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _compressionCancellation?.Cancel();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(Items.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in ExpandPaths(paths))
        {
            if (!File.Exists(path) || !existing.Add(path)) continue;
            try
            {
                var info = new FileInfo(path);
                Items.Add(new CompressionItem
                {
                    Path = info.FullName,
                    Name = info.Name,
                    Kind = CompressionService.GetKind(info.FullName),
                    OriginalBytes = info.Length
                });
            }
            catch
            {
            }
        }
        UpdateQueueSummary();
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return path;
            }
            else if (Directory.Exists(path))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(path, "*", options); }
                catch { continue; }
                foreach (var file in files)
                    yield return file;
            }
        }
    }

    private void UpdateQueueSummary()
    {
        QueueCountText.Text = $"QUEUE  ·  {Items.Count} {(Items.Count == 1 ? "FILE" : "FILES")}";
        QueueBytesText.Text = $"  ·  {CompressionItem.FormatBytes(Items.Sum(x => x.OriginalBytes))}";
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompressButton.IsEnabled = Items.Count > 0 && !_isBusy;
    }

    private void SetBusyVisuals(bool busy)
    {
        CompressButton.IsEnabled = !busy && Items.Count > 0;
        CompressButton.Content = busy ? "COMPRESSING…" : "COMPRESS";
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CompressionModeCombo.IsEnabled = !busy;
        FormatCombo.IsEnabled = !busy;
        OutputTextBox.IsEnabled = !busy;
        UpdateCompressionModeUI();
    }

    private void CompressionModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCompressionModeUI();

    private void UpdateCompressionModeUI()
    {
        if (TargetSizePanel is null || QualityCombo is null || CompressionModeCombo is null) return;
        var targetMode = SelectedText(CompressionModeCombo).Equals("Target size", StringComparison.OrdinalIgnoreCase);
        TargetSizePanel.IsEnabled = targetMode && !_isBusy;
        TargetSizePanel.Opacity = targetMode ? 1 : 0.45;
        QualityCombo.IsEnabled = !targetMode && !_isBusy;
    }

    private void TargetPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: string value }) return;
        TargetSizeTextBox.Text = value;
        SetFooter("TARGET", $"Per-file target set to {value} MB.");
    }

    private void TargetSizeMinus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var current = TryGetTargetMegabytes(out var value) ? value : 20;
        TargetSizeTextBox.Text = Math.Max(0.1, current - 1).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void TargetSizePlus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var current = TryGetTargetMegabytes(out var value) ? value : 20;
        TargetSizeTextBox.Text = Math.Min(100000, current + 1).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void TargetSizeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch) && ch != '.' && ch != ',');
    }

    private bool TryGetTargetMegabytes(out double value)
    {
        var text = TargetSizeTextBox.Text.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && value >= 0.1
               && value <= 100000;
    }

    private void SetFooter(string state, string detail)
    {
        FooterStatus.Text = state;
        FooterDetail.Text = detail;
    }

    private static string SelectedText(ComboBox combo)
    {
        return combo.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : combo.Text;
    }

    private static string Condense(string value)
    {
        var firstLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return firstLine.Length <= 140 ? firstLine : firstLine[..137] + "…";
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (MaximizeButton is not null)
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
