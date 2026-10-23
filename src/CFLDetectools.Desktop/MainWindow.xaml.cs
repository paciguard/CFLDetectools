using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CFLDetectools.Core;
using CFLDetectools.Simulator;

namespace CFLDetectools.Desktop;

public partial class MainWindow : Window
{
    private readonly IPumpClient _client = new SimulatedPumpClient();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _connected;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await UpdateAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _client.ConnectAsync(_lifetime.Token);
            _connected = true;
            await UpdateAsync();
            if (_connected) _timer.Start();
        }
        catch (Exception error) { ShowDisconnected(error); }
    }

    private async Task UpdateAsync(PumpCommand? command = null)
    {
        if (_busy || !_connected) return;
        var selectedId = (Pumps.SelectedItem as PumpSnapshot)?.Id;
        _busy = true;
        UpdateButtons();
        try
        {
            if (command.HasValue && selectedId is not null)
            {
                var result = await _client.SendAsync(selectedId, command.Value, _lifetime.Token);
                MessageText.Text = result.Message;
            }
            var readings = await _client.ReadAsync(_lifetime.Token);
            Pumps.ItemsSource = readings;
            Pumps.SelectedItem = readings.FirstOrDefault(pump => pump.Id == selectedId)
                ?? readings.FirstOrDefault();
            ConnectionText.Text = "Simulator connected · Refresh every 1 second";
        }
        catch (Exception error) { ShowDisconnected(error); }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void ShowDisconnected(Exception error)
    {
        _connected = false;
        _timer.Stop();
        Pumps.ItemsSource = null;
        ConnectionText.Text = "Disconnected · Signals unavailable";
        MessageText.Text = error.Message;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var pump = Pumps.SelectedItem as PumpSnapshot;
        StartButton.IsEnabled = _connected && !_busy && pump?.CanStart == true;
        StopButton.IsEnabled = _connected && !_busy && pump?.CanStop == true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private async void OnStart(object sender, RoutedEventArgs e) => await UpdateAsync(PumpCommand.Start);
    private async void OnStop(object sender, RoutedEventArgs e) => await UpdateAsync(PumpCommand.Stop);

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _connected = false;
        _lifetime.Cancel();
        _client.Dispose();
        _lifetime.Dispose();
    }
}
