using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CFLDetectools.Core;
using CFLDetectools.Simulator;
using Microsoft.Win32;

namespace CFLDetectools.Desktop;

public partial class MainWindow : Window
{
    private readonly IPumpClient _client = new SimulatedPumpClient();
    private readonly SimulatedInstrumentClient _instruments = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<PumpTestPoint> _points = new();
    private readonly Dictionary<string, List<PlotPoint>> _temperatureHistory = new();
    private readonly DateTimeOffset _sessionStart = DateTimeOffset.UtcNow;
    private RigSettings _rig = new(998.2, 100, 100, 0);
    private double? _temperatureWarning;
    private double? _vibrationWarning;
    private string _selectedPumpId = "P-101";
    private PumpSnapshot? _selectedPump;
    private PerformanceResult? _performance;
    private Dictionary<string, EvaluatedChannel> _channels = new();
    private DateTimeOffset _lastUpdate;
    private bool _ready;
    private bool _busy;
    private bool _connected;
    private bool _rendering;
    private int _generation;
    private bool _updatingLanguage;
    private InstrumentFrame? _lastFrame;
    private IReadOnlyList<PumpSnapshot> _lastPumps = Array.Empty<PumpSnapshot>();
    private string? _failure;
    private string _messageKey = "Select a pump and start it to explore simulated test measurements.";
    private object[] _messageArgs = Array.Empty<object>();

    private sealed record PumpRow(PumpSnapshot Snapshot)
    {
        public string Id => Snapshot.Id;
        public string State => T(Snapshot.State.ToString());
    }

    private static string T(string key, params object[] args) => UiText.Shared.Format(key, args);

    public MainWindow()
    {
        InitializeComponent();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        TestPointsGrid.ItemsSource = _points;
        LanguageBox.SelectedValue = UiText.Shared.Language;
        TemperaturePlot.MaximumGapX = 3;
        _timer.Tick += async (_, _) => await UpdateAsync();
        _ready = true;
        ApplyLanguage();
    }

    private static string F(double? value, string format = "F2") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "—";
    private bool PlcAvailable => _connected && _instruments.Scenario != DemoScenario.PlcOffline;
    private double? Value(string id) => _channels.GetValueOrDefault(id)?.ValidValue;

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
        if (!_ready || _busy || !_connected) return;
        var pumpId = _selectedPumpId;
        var generation = _generation;
        _busy = true;
        UpdateButtons();
        try
        {
            if (command.HasValue && PlcAvailable)
            {
                var result = await _client.SendAsync(pumpId, command.Value, _lifetime.Token);
                if (result.Accepted) ShowMessage("SIMULATION: {0} {1} accepted.", pumpId, command.Value.ToString());
                else ShowMessage(result.Message);
            }
            var pumps = await _client.ReadAsync(_lifetime.Token);
            var selected = pumps.Single(pump => pump.Id == pumpId);
            var frame = await _instruments.ReadAsync(pumpId, selected.Running, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            _selectedPump = PlcAvailable ? selected : null;
            _lastPumps = pumps;
            RenderPumpRows();
            _lastUpdate = DateTimeOffset.UtcNow;
            _channels = SimulatedInstrumentClient.Channels.ToDictionary(def => def.Id, def =>
                MeasurementQuality.Evaluate(def, frame.Samples.GetValueOrDefault(def.Id), _lastUpdate, TimeSpan.FromSeconds(3)));
            _lastFrame = frame;
            RenderMeasurements(frame);
            RenderCurves();
            RenderStationStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowDisconnected(error); }
        finally { _busy = false; UpdateButtons(); }
    }

    private void RenderMeasurements(InstrumentFrame frame, bool recordHistory = true)
    {
        ChannelsGrid.ItemsSource = _channels.Values.Select(channel => new
        {
            Name = T(channel.Definition.Name), Route = T(channel.Definition.Route), channel.Definition.Unit,
            Value = F(channel.ValidValue), Quality = T(channel.Quality.ToString()),
            Age = channel.Sample is null ? "—" : F((_lastUpdate - channel.Sample.SampledAt).TotalSeconds, "F1")
        }).ToArray();
        RouteText.Text = string.Join("\n", _channels.Values.GroupBy(c => c.Definition.Route)
            .Select(group => T("{0}: {1}/{2} Good", group.Key, group.Count(c => c.Quality == SignalQuality.Good), group.Count())));
        _performance = null;
        FlowValue.Text = F(Value("Flow"));
        HeadValue.Text = EfficiencyValue.Text = HydraulicValue.Text = "—";
        if (Value("Flow") is double q && Value("Suction") is double ps && Value("Discharge") is double pd)
        {
            _performance = PumpPerformance.Calculate(_rig, q, ps, pd, Value("Torque"), Value("Speed"), Value("ElectricalPower"));
            HeadValue.Text = F(_performance.HeadM);
            HydraulicValue.Text = F(_performance.HydraulicPowerKw);
            EfficiencyValue.Text = F(_performance.PumpEfficiencyPercent);
            PerformanceText.Text = T("Shaft power: {0} kW · Electrical-input efficiency: {1} %", F(_performance.ShaftPowerKw), F(_performance.WireToWaterEfficiencyPercent));
            if (_performance.PumpEfficiencyPercent > 100 || _performance.WireToWaterEfficiencyPercent > 100 || _performance.HeadM < 0)
                PerformanceText.Text += T(" · Check measurement basis / calibration: result outside normal pump-test domain.");
        }
        else PerformanceText.Text = T("Hydraulic calculation unavailable: flow and both pressure samples must be Good and fresh.");

        BearingText.Text = string.Join("\n", new[]
        {
            T("DE temperature: {0} °C — {1}", F(Value("TemperatureDE")), BearingAssessment.Assess(Value("TemperatureDE"), _temperatureWarning)),
            T("NDE temperature: {0} °C — {1}", F(Value("TemperatureNDE")), BearingAssessment.Assess(Value("TemperatureNDE"), _temperatureWarning)),
            T("DE vibration: {0} mm/s RMS — {1}", F(Value("VibrationDE")), BearingAssessment.Assess(Value("VibrationDE"), _vibrationWarning)),
            T("NDE vibration: {0} mm/s RMS — {1}", F(Value("VibrationNDE")), BearingAssessment.Assess(Value("VibrationNDE"), _vibrationWarning))
        });
        if (!_temperatureHistory.TryGetValue(_selectedPumpId, out var history))
            _temperatureHistory[_selectedPumpId] = history = new();
        if (recordHistory && Value("TemperatureDE") is double temperature)
            history.Add(new((_lastUpdate - _sessionStart).TotalSeconds, temperature));
        if (history.Count > 120) history.RemoveRange(0, history.Count - 120);
        TemperaturePlot.SetPoints(history);
        var waveform = frame.DriveEndWaveform;
        if (Value("VibrationDE").HasValue && waveform is not null
            && _lastUpdate - waveform.SampledAt <= TimeSpan.FromSeconds(3)
            && waveform.SampledAt <= _lastUpdate.AddMilliseconds(200))
        {
            var vibration = VibrationAnalysis.AnalyzeVelocity(waveform.SamplesMmPerSecond, waveform.SampleRateHz);
            VibrationText.Text = T("DE waveform: RMS {0} mm/s · Peak {1} mm/s · Peak-to-peak {2} mm/s",
                F(vibration.RmsMmS), F(vibration.PeakMmS), F(vibration.PeakToPeakMmS)) + "\n"
                + T("Crest factor {0} · Dominant FFT bin {1} Hz · {2} samples/s · {3} samples · Δf {4} Hz",
                    F(vibration.CrestFactor), F(vibration.DominantFrequencyHz), F(waveform.SampleRateHz, "F0"),
                    waveform.SamplesMmPerSecond.Length, F(waveform.SampleRateHz / waveform.SamplesMmPerSecond.Length, "F1"));
            var mean = waveform.SamplesMmPerSecond.Average();
            WaveformPlot.SetPoints(waveform.SamplesMmPerSecond.Take(128).Select((v, i) => new PlotPoint(i / waveform.SampleRateHz * 1000, v - mean)));
            SpectrumPlot.SetPoints(vibration.Spectrum.Where(bin => bin.FrequencyHz <= 512).Select(bin => new PlotPoint(bin.FrequencyHz, bin.PeakAmplitudeMmS)));
        }
        else
        {
            VibrationText.Text = T("Velocity waveform unavailable — no FFT or waveform metrics are calculated.");
            WaveformPlot.SetPoints(Array.Empty<PlotPoint>());
            SpectrumPlot.SetPoints(Array.Empty<PlotPoint>());
        }
    }

    private void RenderCurves()
    {
        var selected = _points.Where(p => p.PumpId == _selectedPumpId && p.Rig == _rig).OrderBy(p => p.Result.FlowM3H).ToArray();
        HeadCurve.SetPoints(selected.Select(p => new PlotPoint(p.Result.FlowM3H, p.Result.HeadM)));
        EfficiencyCurve.SetPoints(selected.Where(p => p.Result.PumpEfficiencyPercent.HasValue)
            .Select(p => new PlotPoint(p.Result.FlowM3H, p.Result.PumpEfficiencyPercent!.Value)));
    }

    private void InvalidateMeasurements()
    {
        _generation++;
        _performance = null;
        _lastFrame = null;
        _channels.Clear();
        FlowValue.Text = HeadValue.Text = HydraulicValue.Text = EfficiencyValue.Text = "—";
        ChannelsGrid.ItemsSource = null;
        BearingText.Text = T("Awaiting fresh bearing measurements");
        VibrationText.Text = T("Awaiting fresh waveform");
        WaveformPlot.SetPoints(Array.Empty<PlotPoint>());
        SpectrumPlot.SetPoints(Array.Empty<PlotPoint>());
        TemperaturePlot.SetPoints(Array.Empty<PlotPoint>());
        UpdateButtons();
    }

    private void ShowDisconnected(Exception error)
    {
        _connected = false;
        _timer.Stop();
        InvalidateMeasurements();
        Pumps.ItemsSource = null;
        ConnectionText.Text = T("Simulator disconnected");
        RouteText.Text = T("All samples unavailable");
        PermissiveText.Text = T("PLC signals unavailable");
        PerformanceText.Text = T("Measurements unavailable");
        _failure = error.Message;
        ShowMessage("Error: {0}", error.Message);
    }

    private void UpdateButtons()
    {
        if (!_ready) return;
        StartButton.IsEnabled = PlcAvailable && !_busy && _selectedPump?.CanStart == true;
        StopButton.IsEnabled = PlcAvailable && !_busy && _selectedPump?.CanStop == true;
        CaptureButton.IsEnabled = PlcAvailable && !_busy && _selectedPump?.Running == true
            && _performance?.PumpEfficiencyPercent is > 0 and <= 100
            && (_performance.WireToWaterEfficiencyPercent is null or >= 0 and <= 100)
            && _performance.HeadM >= 0 && DateTimeOffset.UtcNow - _lastUpdate <= TimeSpan.FromSeconds(3);
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _rendering || Pumps.SelectedItem is not PumpRow row) return;
        _selectedPumpId = row.Snapshot.Id;
        _selectedPump = row.Snapshot;
        InvalidateMeasurements();
        RenderCurves();
        await UpdateAsync();
    }

    private async void OnScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingLanguage || ScenarioBox.SelectedValue is not DemoScenario scenario) return;
        _instruments.Scenario = scenario;
        InvalidateMeasurements();
        await UpdateAsync();
    }

    private async void OnFlowChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _instruments.FlowSettingM3H = e.NewValue;
        FlowSettingText.Text = $"{e.NewValue:F0} m³/h";
        InvalidateMeasurements();
        await UpdateAsync();
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await UpdateAsync(PumpCommand.Start);
    private async void OnStop(object sender, RoutedEventArgs e) => await UpdateAsync(PumpCommand.Stop);

    private async void OnApplySettings(object sender, RoutedEventArgs e)
    {
        try
        {
            double Parse(TextBox box) => double.Parse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            double? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : Parse(box);
            var rig = new RigSettings(Parse(DensityInput), Parse(SuctionDiameterInput), Parse(DischargeDiameterInput), Parse(ElevationInput));
            rig.Validate();
            var temperatureWarning = Optional(TemperatureWarningInput);
            var vibrationWarning = Optional(VibrationWarningInput);
            if (temperatureWarning.HasValue && !double.IsFinite(temperatureWarning.Value)
                || vibrationWarning.HasValue && (!double.IsFinite(vibrationWarning.Value) || vibrationWarning <= 0))
                throw new ArgumentException("Warnings must be finite; vibration warning must be positive.");
            _rig = rig;
            _temperatureWarning = temperatureWarning;
            _vibrationWarning = vibrationWarning;
            InvalidateMeasurements();
            await UpdateAsync();
            ShowMessage("Session settings applied. Demo limits are advisory; density and geometry are included in CSV exports.");
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        { ShowMessage("Settings unchanged: {0}", error is FormatException or OverflowException ? "Enter valid decimal numbers." : error.Message); }
    }

    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        await UpdateAsync();
        UpdateButtons();
        if (!CaptureButton.IsEnabled || _performance is null) { ShowMessage("Cannot capture: a running pump and valid, plausible performance measurements are required."); return; }
        _points.Add(new(_lastUpdate, _selectedPumpId, _instruments.Scenario.ToString(), _performance, _rig,
            Value("Suction")!.Value, Value("Discharge")!.Value, Value("Torque"), Value("Speed"), Value("ElectricalPower")));
        RenderCurves();
        ShowMessage("Captured simulated point {0}. Export CSV to preserve this session before closing.", _points.Count);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) { ShowMessage("Capture at least one test point before exporting."); return; }
        var dialog = new SaveFileDialog { Title = T("Export session CSV"), Filter = T("CSV files (*.csv)|*.csv"), FileName = $"CFL-demo-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, TestSessionCsv.ExportSimulation(_points), new UTF8Encoding(true));
            ShowMessage("Exported {0} simulated points to {1}", _points.Count, dialog.FileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowMessage("Export failed: {0}", error.Message); }
    }


    private void ShowMessage(string key, params object[] args)
    {
        _messageKey = key;
        _messageArgs = args;
        MessageText.Text = T(key, args);
    }

    private void RenderPumpRows()
    {
        _rendering = true;
        try
        {
            var rows = PlcAvailable ? _lastPumps.Select(pump => new PumpRow(pump)).ToArray() : Array.Empty<PumpRow>();
            Pumps.ItemsSource = rows;
            Pumps.SelectedItem = rows.FirstOrDefault(row => row.Id == _selectedPumpId);
        }
        finally { _rendering = false; }
    }

    private void RenderStationStatus()
    {
        ConnectionText.Text = T(_failure is not null ? "Simulator disconnected"
            : !_connected ? "Initializing simulator" : PlcAvailable ? "Demo station · 1 s refresh" : "Demo PLC unavailable");
        PermissiveText.Text = _selectedPump is null ? T("PLC signals unavailable")
            : T("Ready: {0} · Remote: {1} · Fault: {2}",
                _selectedPump.Ready ? "Yes" : "No", _selectedPump.RemoteMode ? "Yes" : "No", _selectedPump.Fault ? "Yes" : "No");
    }

    private void ApplyLanguage()
    {
        _updatingLanguage = true;
        try
        {
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(UiText.Shared.Language);
            var scenarioNames = new[]
            {
                "Normal", "PLC disconnected", "Direct instruments offline", "Stale discharge pressure",
                "Waveform DAQ offline", "Flow outside range", "Hot bearing", "High vibration"
            };
            ScenarioBox.DisplayMemberPath = "Name";
            ScenarioBox.SelectedValuePath = "Value";
            ScenarioBox.ItemsSource = Enum.GetValues<DemoScenario>().Select((value, index) => new { Name = T(scenarioNames[index]), Value = value }).ToArray();
            ScenarioBox.SelectedValue = _instruments.Scenario;
            WaveformPlot.XLabel = T("Time (ms)"); WaveformPlot.YLabel = T("Velocity (mm/s)");
            SpectrumPlot.XLabel = T("Frequency (Hz)"); SpectrumPlot.YLabel = T("Peak amplitude (mm/s)");
            TemperaturePlot.XLabel = T("Session time (s)"); TemperaturePlot.YLabel = T("Temperature (°C)");
            HeadCurve.XLabel = T("Flow (m³/h)"); HeadCurve.YLabel = T("Head (m)");
            EfficiencyCurve.XLabel = T("Flow (m³/h)"); EfficiencyCurve.YLabel = T("Pump efficiency (%)");
            foreach (var plot in new[] { WaveformPlot, SpectrumPlot, TemperaturePlot, HeadCurve, EfficiencyCurve }) plot.InvalidateVisual();
            RenderPumpRows();
            RenderStationStatus();
            if (_lastFrame is not null) RenderMeasurements(_lastFrame, recordHistory: false);
            else
            {
                RouteText.Text = T(_failure is null ? "No samples" : "All samples unavailable");
                PerformanceText.Text = T(_failure is null ? "Waiting for valid measurements" : "Measurements unavailable");
            }
            MessageText.Text = T(_messageKey, _messageArgs);
        }
        finally { _updatingLanguage = false; }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingLanguage || LanguageBox.SelectedValue is not string language) return;
        UiText.Shared.SetLanguage(language);
        ApplyLanguage();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _connected = false;
        _lifetime.Cancel();
        _client.Dispose();
        _lifetime.Dispose();
    }
}
