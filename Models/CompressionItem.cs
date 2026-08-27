using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsCompressor.Models;

public sealed class CompressionItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private double _progress;
    private string _result = "—";

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required long OriginalBytes { get; init; }

    public string OriginalSize => FormatBytes(OriginalBytes);

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public string Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1)
        {
            size /= 1024;
            index++;
        }
        return $"{size:0.##} {units[index]}";
    }
}
