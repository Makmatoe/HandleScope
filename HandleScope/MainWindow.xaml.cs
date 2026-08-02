using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HandleScope.Compatibility;
using HandleScope.Models;
using HandleScope.Services;

namespace HandleScope;

public partial class MainWindow : Window
{
    private readonly HandleService _handleService = new();
    private readonly ProcessIdentityService _identityService = new();
    private readonly ProcessService _processService;
    private readonly ObservableCollection<ProcessRow> _processes = [];
    private readonly ProcessIdentity _currentIdentity;
    private readonly ApiCompatibilityPreferenceStore _compatibilityStore = new();
    private CancellationTokenSource? _scanCancellation;
    private bool _isBusy;
    private bool _loadingCompatibilityPreference;

    public MainWindow()
    {
        _processService = new ProcessService(_identityService);
        _currentIdentity = _identityService.GetIdentity(Environment.ProcessId);
        InitializeComponent();
        ProcessesView = CollectionViewSource.GetDefaultView(_processes);
        Handles = [];
        DataContext = this;
    }

    public ICollectionView ProcessesView { get; }

    public ObservableCollection<HandleEntry> Handles { get; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_currentIdentity.IsElevated || _currentIdentity.WindowsSessionId == 0)
        {
            MessageBox.Show(
                this,
                "HandleScope is designed to run with your normal Windows permissions. Close this copy and start it from a non-administrator desktop session.",
                "Standard-user session required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
            return;
        }

        LoadCompatibilityPreference();
        RuntimeVersionText.Text =
            $"HandleScope {GetType().Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
        await RefreshProcessesAsync();
        StatusText.Text = "Ready · same-user, same-session processes only";
    }

    private void LoadCompatibilityPreference()
    {
        _loadingCompatibilityPreference = true;
        try
        {
            var result = _compatibilityStore.Read();
            var tag = ApiCompatibilityPolicy.ToStorageValue(result.Mode);
            ApiCompatibilityComboBox.SelectedItem =
                ApiCompatibilityComboBox.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => item.Tag?.ToString() == tag);
            CompatibilityStatusText.Text = result.IsValid
                ? $"Preferred {ApiCompatibilityPolicy.Resolve(result.Mode)}; v1 and v2 remain available"
                : "Invalid preference ignored; automatic mode is active";
        }
        finally
        {
            _loadingCompatibilityPreference = false;
        }
    }

    private void ApiCompatibilityComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingCompatibilityPreference ||
            ApiCompatibilityComboBox.SelectedItem is not ComboBoxItem item ||
            !ApiCompatibilityPolicy.TryParseStorageValue(
                item.Tag?.ToString(),
                out var mode))
        {
            return;
        }

        try
        {
            _compatibilityStore.Write(mode);
            CompatibilityStatusText.Text =
                $"Preferred {ApiCompatibilityPolicy.Resolve(mode)} after the API restarts; v1 and v2 remain available";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException or
                ArgumentException)
        {
            CompatibilityStatusText.Text = "Compatibility preference was not changed";
            ShowError(
                "Could not save the SessionDock API compatibility preference.",
                exception);
            LoadCompatibilityPreference();
        }
    }

    private async void RefreshProcesses_Click(object sender, RoutedEventArgs e) =>
        await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true, "Refreshing processes…", indeterminate: true);

        try
        {
            var rows = await Task.Run(GetAllowedProcessRows);
            var selectedPid = (ProcessesGrid.SelectedItem as ProcessRow)?.ProcessId;

            _processes.Clear();
            foreach (var row in rows)
            {
                _processes.Add(row);
            }

            ProcessesView.Refresh();

            if (selectedPid is int pid)
            {
                ProcessesGrid.SelectedItem = _processes.FirstOrDefault(row => row.ProcessId == pid);
            }

            StatusText.Text = $"{_processes.Count:N0} processes loaded";
        }
        catch (Exception ex)
        {
            ShowError("Could not refresh the process list.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ProcessSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = ProcessSearchTextBox.Text.Trim();
        ProcessesView.Filter = item =>
        {
            if (item is not ProcessRow process)
            {
                return false;
            }

            return string.IsNullOrEmpty(query) ||
                   process.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
        };
    }

    private void ProcessesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessesGrid.SelectedItem is ProcessRow process)
        {
            SelectedProcessText.Text =
                $"{process.Name} · PID {process.ProcessId} · {process.HandleCountDisplay} handles · {process.MemoryDisplay}";
        }
        else
        {
            SelectedProcessText.Text = "Select a process on the left";
        }

        Handles.Clear();
        HandlesGrid.SelectedItem = null;
        ResultSummaryText.Text = "No scan results yet";
        CopyAutomationButton.IsEnabled = false;
        CloseHandleButton.IsEnabled = false;
    }

    private async void ScanHandles_Click(object sender, RoutedEventArgs e) =>
        await ScanHandlesAsync();

    private async void HandleNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isBusy)
        {
            e.Handled = true;
            await ScanHandlesAsync();
        }
    }

    private async Task ScanHandlesAsync()
    {
        if (ProcessesGrid.SelectedItem is not ProcessRow process)
        {
            MessageBox.Show(
                this,
                "Select a process before scanning for handles.",
                "Choose a process",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!TryRevalidateProcess(
                process.ProcessId,
                process.ProcessCreationTimeUtcFileTime,
                out _))
        {
            await RefreshProcessesAsync();
            MessageBox.Show(
                this,
                "That process exited or no longer matches the safe local-user boundary.",
                "Process changed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var matchMode = (MatchModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Exact"
            ? HandleMatchMode.Exact
            : HandleMatchMode.Contains;
        var handleName = HandleNameTextBox.Text;

        Handles.Clear();
        CopyAutomationButton.IsEnabled = false;
        CloseHandleButton.IsEnabled = false;
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;

        var progress = new Progress<ScanProgress>(value =>
        {
            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Maximum = Math.Max(1, value.Total);
            ScanProgressBar.Value = value.Completed;
            StatusText.Text = $"Scanning PID {process.ProcessId} · {value.Completed:N0}/{value.Total:N0} handles";
        });

        SetBusy(true, $"Reading handles from {process.Name}…", indeterminate: true, canCancel: true);

        try
        {
            var results = await Task.Run(
                () => _handleService.FindHandles(
                    process.ProcessId,
                    process.ProcessCreationTimeUtcFileTime,
                    handleName,
                    matchMode,
                    progress,
                    cancellationToken),
                cancellationToken);

            foreach (var result in results)
            {
                Handles.Add(result);
            }

            ResultSummaryText.Text = results.Count == 1
                ? "1 matching named handle"
                : $"{results.Count:N0} matching named handles";
            StatusText.Text = $"Scan complete · {results.Count:N0} matches";
        }
        catch (OperationCanceledException)
        {
            ResultSummaryText.Text = "Scan canceled";
            StatusText.Text = "Scan canceled";
        }
        catch (Exception ex)
        {
            ResultSummaryText.Text = "Scan failed";
            ShowError($"Could not scan handles for {process.Name} (PID {process.ProcessId}).", ex);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            SetBusy(false);
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Canceling scan…";
        _scanCancellation?.Cancel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) =>
        _scanCancellation?.Cancel();

    private void HandlesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CopyAutomationButton.IsEnabled = CanCopyAutomationCommand() && !_isBusy;
        CloseHandleButton.IsEnabled = HandlesGrid.SelectedItem is HandleEntry && !_isBusy;
    }

    private void CopyAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (HandlesGrid.SelectedItem is not HandleEntry entry ||
            ProcessesGrid.SelectedItem is not ProcessRow process)
        {
            return;
        }

        try
        {
            var command = AutomationCommandBuilder.BuildRecurringCloseCommand(
                process.Name,
                entry);
            Clipboard.SetText(command);
            StatusText.Text =
                $"Recurring all-process command copied for {process.Name} · {entry.ObjectType} · {entry.Name}";
        }
        catch (Exception ex)
        {
            ShowError("Could not copy the recurring command.", ex);
        }
    }

    private async void CloseHandle_Click(object sender, RoutedEventArgs e)
    {
        if (HandlesGrid.SelectedItem is not HandleEntry entry ||
            ProcessesGrid.SelectedItem is not ProcessRow process)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Close {entry.HandleDisplay} in {process.Name} (PID {entry.ProcessId})?\n\n" +
            $"Type: {entry.ObjectType}\n" +
            $"Name: {entry.Name}\n\n" +
            "The target application may crash or lose data. This cannot be undone.",
            "Confirm handle close",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryRevalidateProcess(
                entry.ProcessId,
                entry.ProcessCreationTimeUtcFileTime,
                out _))
        {
            ShowError(
                "The target process changed. Refresh and scan again.",
                new InvalidOperationException("The stored process identity is stale."));
            return;
        }

        SetBusy(true, $"Closing {entry.HandleDisplay}…", indeterminate: true);

        try
        {
            await Task.Run(() => _handleService.CloseHandle(entry));
            Handles.Remove(entry);
            ResultSummaryText.Text = Handles.Count == 1
                ? "1 matching named handle remains"
                : $"{Handles.Count:N0} matching named handles remain";
            StatusText.Text = $"Closed {entry.HandleDisplay} in PID {entry.ProcessId}";
        }
        catch (Exception ex)
        {
            ShowError($"Could not close {entry.HandleDisplay}.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(
        bool isBusy,
        string? status = null,
        bool indeterminate = false,
        bool canCancel = false)
    {
        _isBusy = isBusy;
        ProcessesGrid.IsEnabled = !isBusy;
        ProcessSearchTextBox.IsEnabled = !isBusy;
        HandleNameTextBox.IsEnabled = !isBusy;
        MatchModeComboBox.IsEnabled = !isBusy;
        ScanButton.IsEnabled = !isBusy;
        CopyAutomationButton.IsEnabled = !isBusy && CanCopyAutomationCommand();
        CloseHandleButton.IsEnabled = !isBusy && HandlesGrid.SelectedItem is HandleEntry;
        CancelButton.Visibility = isBusy && canCancel ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        ScanProgressBar.IsIndeterminate = isBusy && indeterminate;

        if (!isBusy)
        {
            ScanProgressBar.Value = 0;
            ScanProgressBar.IsIndeterminate = false;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    private void ShowError(string message, Exception exception)
    {
        StatusText.Text = "Operation failed";
        MessageBox.Show(
            this,
            $"{message}\n\n{exception.Message}",
            "HandleScope",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private bool TryRevalidateProcess(
        int processId,
        long expectedCreationTimeUtcFileTime,
        out ProcessIdentity identity)
    {
        try
        {
            identity = _identityService.GetIdentity(processId);
            return IsAllowedProcess(identity) &&
                   expectedCreationTimeUtcFileTime > 0 &&
                   expectedCreationTimeUtcFileTime == identity.CreationTimeUtcFileTime;
        }
        catch
        {
            identity = null!;
            return false;
        }
    }

    private bool IsAllowedProcess(ProcessIdentity identity) =>
        !identity.IsElevated &&
        identity.WindowsSessionId == _currentIdentity.WindowsSessionId &&
        string.Equals(
            identity.OwnerSid,
            _currentIdentity.OwnerSid,
            StringComparison.Ordinal);

    private IReadOnlyList<ProcessRow> GetAllowedProcessRows()
    {
        var allowed = new List<ProcessRow>();
        foreach (var snapshot in _processService.GetProcessSnapshots())
        {
            if (IsAllowedProcess(snapshot.Identity))
            {
                allowed.Add(snapshot.Row);
            }
        }

        return allowed
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProcessId)
            .ToArray();
    }

    private bool CanCopyAutomationCommand() =>
        ProcessesGrid.SelectedItem is ProcessRow process &&
        HandlesGrid.SelectedItem is HandleEntry handle &&
        RobloxAutomationRecipe.IsSupported(process.Name, handle);
}
