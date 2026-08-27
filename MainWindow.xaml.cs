using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WindowsCompressor.Models;
using WindowsCompressor.Services;

namespace WindowsCompressor;

public partial class MainWindow : Window
{
    private readonly CompressionService _compression = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;

    public ObservableCollection<CompressionItem> Items { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        OutputTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "Compressed");
        UpdateCompressionModeUI();
        UpdateQueueUi();
            Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        try
        {
            RootShell.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
      From = 0,
      To = 1,
      Duration = TimeSpan.FromMilliseconds(240),
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
            {
      From = 10,
      To = 0,
      Duration = TimeSpan.FromMilliseconds(280),
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        catch
        {
            RootShell.BeginAnimation(OpacityProperty, null);
            RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            RootShell.Opacity = 1;
            RootTranslate.Y = 0;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e) => UpdateMaximizeGlyph();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is not null)
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
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
        var dialog = new OpenFolderDialog { Title = "Add a folder" };
        if (dialog.ShowDialog(this) == true)
            AddPaths(Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.AllDirectories));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose output folder",
            InitialDirectory = Directory.Exists(OutputTextBox.Text) ? OutputTextBox.Text : null
        };
        if (dialog.ShowDialog(this) == true)
            OutputTextBox.Text = dialog.FolderName;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(ExpandPaths(paths));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var selected = QueueGrid.SelectedItems.Cast<CompressionItem>().ToArray();
        foreach (var item in selected) Items.Remove(item);
        UpdateQueueUi();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Items.Clear();
        UpdateQueueUi();
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || Items.Count == 0) return;

        var output = OutputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            SetFooter("ERROR", "Choose an output folder.");
            return;
        }

        try { Directory.CreateDirectory(output); }
        catch (Exception ex)
        {
            SetFooter("ERROR", Condense(ex.Message));
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
        SetBusyVisuals(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var quality = SelectedText(QualityCombo);
        var format = SelectedText(FormatCombo);
        var completed = 0;
        var failed = 0;
        long totalSaved = 0;

        try
        {
            SetFooter("WORKING", $"Starting {Items.Count} file{(Items.Count == 1 ? string.Empty : "s")}…");

            foreach (var item in Items)
            {
                token.ThrowIfCancellationRequested();
                item.Status = "Compressing";
                item.Progress = 0;
                item.Result = "—";

                var progress = new Progress<double>(p => item.Progress = Math.Clamp(p, 0, 100));
                var status = new Progress<string>(text =>
                {
                    item.Status = text;
                    SetFooter("WORKING", $"{item.Name} · {text}");
                });

                try
                {
                    var result = await _compression.CompressAsync(
                        item,
                        output,
                        quality,
                        format,
                        targetMegabytes,
                        progress,
                        status,
                        token);

                    item.Progress = 100;
                    item.Status = "Done";
                    if (result.OutputBytes <= item.OriginalBytes)
                    {
                        var saved = item.OriginalBytes - result.OutputBytes;
                        totalSaved += saved;
                        var percentage = item.OriginalBytes == 0 ? 0 : saved * 100d / item.OriginalBytes;
                        item.Result = $"{CompressionItem.FormatBytes(result.OutputBytes)}  −{percentage:0.#}%";
                    }
                    else
                    {
                        var larger = result.OutputBytes - item.OriginalBytes;
                        var percentage = item.OriginalBytes == 0 ? 0 : larger * 100d / item.OriginalBytes;
                        item.Result = $"{CompressionItem.FormatBytes(result.OutputBytes)}  +{percentage:0.#}%";
                    }
                    completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    item.Result = Condense(ex.Message);
                    failed++;
                }
            }

            SetFooter(
                failed == 0 ? "DONE" : "PARTIAL",
                failed == 0
                    ? $"{completed} completed · saved {CompressionItem.FormatBytes(totalSaved)}"
                    : $"{completed} completed · {failed} failed · saved {CompressionItem.FormatBytes(totalSaved)}");
        }
        catch (OperationCanceledException)
        {
            foreach (var item in Items.Where(i => i.Status is not "Done" and not "Failed"))
            {
                item.Status = "Cancelled";
                item.Result = "—";
            }
            SetFooter("CANCELLED", "Compression stopped.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _isBusy = false;
            SetBusyVisuals(false);
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var output = OutputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output)) return;
        try
        {
            Directory.CreateDirectory(output);
            Process.Start(new ProcessStartInfo { FileName = output, UseShellExecute = true });
        }
        catch (Exception ex) { SetFooter("ERROR", Condense(ex.Message)); }
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var known = Items.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path) || !known.Add(path)) continue;
                var info = new FileInfo(path);
                Items.Add(new CompressionItem
                {
                    Path = info.FullName,
                    Name = info.Name,
                    Kind = CompressionService.GetKind(info.FullName),
                    OriginalBytes = info.Length,
                    Status = "Ready",
                    Progress = 0,
                    Result = "—"
                });
                added++;
            }
            catch { }
        }

        UpdateQueueUi();
        SetFooter(added > 0 ? "READY" : "NOTICE", added > 0 ? $"Added {added} file{(added == 1 ? string.Empty : "s")}." : "No new files were added.");
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files) yield return file;
        }
    }

    private void UpdateQueueUi()
    {
        QueueCountText.Text = $"QUEUE  ·  {Items.Count} FILE{(Items.Count == 1 ? string.Empty : "S")}";
        QueueBytesText.Text = $"  ·  {CompressionItem.FormatBytes(Items.Sum(x => x.OriginalBytes))}";
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CompressButton.IsEnabled = Items.Count > 0 && !_isBusy;
    }

    private void SetBusyVisuals(bool busy)
    {
        CompressButton.IsEnabled = !busy && Items.Count > 0;
        CompressButton.Content = busy ? "Compressing…" : "Compress";
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
        TargetSizePanel.IsEnabled = !_isBusy;
        QualityCombo.IsEnabled = !targetMode && !_isBusy;

        var targetOpacity = _isBusy ? 0.55 : targetMode ? 1.0 : 0.82;
        var qualityOpacity = _isBusy ? 0.55 : targetMode ? 0.5 : 1.0;

        if (!IsLoaded)
        {
            TargetSizePanel.BeginAnimation(OpacityProperty, null);
            QualityCombo.BeginAnimation(OpacityProperty, null);
            TargetSizePanel.Opacity = targetOpacity;
            QualityCombo.Opacity = qualityOpacity;
            return;
        }

        try
        {
            TargetSizePanel.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
      To = targetOpacity,
      Duration = TimeSpan.FromMilliseconds(160),
      EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

            QualityCombo.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
      To = qualityOpacity,
      Duration = TimeSpan.FromMilliseconds(160),
      EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
        catch
        {
            TargetSizePanel.BeginAnimation(OpacityProperty, null);
            QualityCombo.BeginAnimation(OpacityProperty, null);
            TargetSizePanel.Opacity = targetOpacity;
            QualityCombo.Opacity = qualityOpacity;
        }
    }

    private void TargetPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: string value }) return;
        CompressionModeCombo.SelectedIndex = 1;
        TargetSizeTextBox.Text = value;
        SetFooter("TARGET", $"Per-file target set to {value} MB.");
    }

    private void TargetSizeMinus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var current = TryGetTargetMegabytes(out var value) ? value : 20;
        CompressionModeCombo.SelectedIndex = 1;
        TargetSizeTextBox.Text = Math.Max(0.1, current - 1).ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void TargetSizePlus_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var current = TryGetTargetMegabytes(out var value) ? value : 20;
        CompressionModeCombo.SelectedIndex = 1;
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

        if (FooterPill is not null && IsLoaded)
        {
            FooterPill.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.5,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private static string SelectedText(ComboBox combo)
    {
        return combo.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : combo.Text;
    }

    private static string Condense(string value)
    {
        var single = Regex.Replace(value, @"\s+", " ").Trim();
        return single.Length <= 110 ? single : single[..107] + "…";
    }
}
