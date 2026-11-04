using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ProcessGuardian.Services.Logging;

namespace ProcessGuardian.ViewModels;

public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private readonly FileLogService _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _searchText = string.Empty;
    private string _selectedLevel = "All";
    private string? _viewError;
    private IReadOnlyList<LogEntry> _allEntries = [];
    public DiagnosticsViewModel(FileLogService logger, Action<string>? openFolder = null, Func<bool>? confirmClear = null)
    {
        _logger = logger;
        OpenFolderCommand = new RelayCommand(_ =>
        {
            try { openFolder?.Invoke(logger.LogDirectory); _viewError = null; }
            catch (Exception exception) { _viewError = "Could not open log folder: " + exception.Message; }
            OnPropertyChanged(nameof(LoggingWarning));
        });
        ClearCommand = new AsyncRelayCommand(async () =>
        {
            if (confirmClear?.Invoke() != true) return;
            try { await logger.ClearAsync(); _viewError = null; await RefreshAsync(); }
            catch (Exception exception) { _viewError = "Could not clear logs: " + exception.Message; OnPropertyChanged(nameof(LoggingWarning)); }
        });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _ = RefreshAsync();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<LogEntry> Entries { get; } = new();
    public string LogDirectory => _logger.LogDirectory;
    public string? LoggingWarning => _viewError ?? (_logger.LastWriteException is { } error ? "Log storage or reading problem: " + error.Message : null);
    public string SearchText { get => _searchText; set { if (_searchText == value) return; _searchText = value; OnPropertyChanged(); ApplyFilter(); } }
    public string SelectedLevel { get => _selectedLevel; set { if (_selectedLevel == value) return; _selectedLevel = value; OnPropertyChanged(); ApplyFilter(); } }
    public IReadOnlyList<string> Levels { get; } = ["All", .. Enum.GetNames<LogLevel>()];
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand ClearCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            _allEntries = await _logger.ReadRecentAsync(2000);
            ApplyFilter();
            OnPropertyChanged(nameof(LoggingWarning));
        }
        finally { _refreshGate.Release(); }
    }
    private void ApplyFilter()
    {
        var filtered = _allEntries.Where(entry => (SelectedLevel == "All" || entry.Level.ToString() == SelectedLevel)
            && (string.IsNullOrWhiteSpace(SearchText) || entry.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || entry.Module.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || (entry.ApplicationName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.ProcessId?.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.Exception?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))).Take(1000).ToArray();
        Entries.Clear();
        foreach (var entry in filtered) Entries.Add(entry);
        OnPropertyChanged(nameof(Entries));
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
