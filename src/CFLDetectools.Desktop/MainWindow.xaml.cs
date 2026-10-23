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

    public MainWindow()
    {
        InitializeComponent();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        TestPointsGrid.ItemsSource = _points;
        ScenarioBox.DisplayMemberPath = "Name";
        ScenarioBox.SelectedValuePath = "Value";
        ScenarioBox.ItemsSource = new[]
        {
            new { Name = "Normal", Value = DemoScenario.Normal },
            new { Name = "PLC disconnected", Value = DemoScenario.PlcOffline },
            new { Name = "Direct instruments offline", Value = DemoScenario.InstrumentOffline },
            new { Name = "Stale discharge pressure", Value = DemoScenario.StalePressure },
            new { Name = "Waveform DAQ offline", Value = DemoScenario.DaqOffline },
            new { Name = "Flow outside range", Value = DemoScenario.OutOfRange },
            new { Name = "Hot bearing", Value = DemoScenario.HotBearing },
            new { Name = "High vibration", Value = DemoScenario.HighVibration }
        };
        ScenarioBox.SelectedIndex = 0;
        WaveformPlot.XLabel = "Time (ms)"; WaveformPlot.YLabel = "Velocity (mm/s)";
        SpectrumPlot.XLabel = "Frequency (Hz)"; SpectrumPlot.YLabel = "Peak amplitude (mm/s)";
        TemperaturePlot.XLabel = "Session time (s)"; TemperaturePlot.YLabel = "Temperature (°C)";
        TemperaturePlot.MaximumGapX = 3;
        HeadCurve.XLabel = "Flow (m³/h)"; HeadCurve.YLabel = "Head (m)";
        EfficiencyCurve.XLabel = "Flow (m³/h)"; EfficiencyCurve.YLabel = "Pump efficiency (%)";
        _timer.Tick += async (_, _) => await UpdateAsync();
        _ready = true;
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
                MessageText.Text = result.Message;
            }
            var pumps = await _client.ReadAsync(_lifetime.Token);
            var selected = pumps.Single(pump => pump.Id == pumpId);
            var frame = await _instruments.ReadAsync(pumpId, selected.Running, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            _selectedPump = PlcAvailable ? selected : null;
            _rendering = true;
            try
            {
                Pumps.ItemsSource = PlcAvailable ? pumps : null;
                Pumps.SelectedItem = PlcAvailable ? selected : null;
            }
            finally { _rendering = false; }
            _lastUpdate = DateTimeOffset.UtcNow;
            _channels = SimulatedInstrumentClient.Channels.ToDictionary(def => def.Id, def =>
                MeasurementQuality.Evaluate(def, frame.Samples.GetValueOrDefault(def.Id), _lastUpdate, TimeSpan.FromSeconds(3)));
            RenderMeasurements(frame);
            RenderCurves();
            ConnectionText.Text = PlcAvailable ? "Demo station · 1 s refresh" : "Demo PLC unavailable";
            PermissiveText.Text = _selectedPump is null ? "PLC signals unavailable"
                : $"Ready: {_selectedPump.Ready} · Remote: {_selectedPump.RemoteMode} · Fault: {_selectedPump.Fault}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowDisconnected(error); }
        finally { _busy = false; UpdateButtons(); }
    }

    private void RenderMeasurements(InstrumentFrame frame)
    {
        ChannelsGrid.ItemsSource = _channels.Values.Select(channel => new
        {
            Name = channel.Definition.Name, channel.Definition.Route, channel.Definition.Unit,
            Value = F(channel.ValidValue), Quality = channel.Quality.ToString(),
            Age = channel.Sample is null ? "—" : F((_lastUpdate - channel.Sample.SampledAt).TotalSeconds, "F1")
        }).ToArray();
        RouteText.Text = string.Join("\n", _channels.Values.GroupBy(c => c.Definition.Route)
            .Select(group => $"{group.Key}: {group.Count(c => c.Quality == SignalQuality.Good)}/{group.Count()} Good"));
        _performance = null;
        FlowValue.Text = F(Value("Flow"));
        HeadValue.Text = EfficiencyValue.Text = HydraulicValue.Text = "—";
        if (Value("Flow") is double q && Value("Suction") is double ps && Value("Discharge") is double pd)
        {
            _performance = PumpPerformance.Calculate(_rig, q, ps, pd, Value("Torque"), Value("Speed"), Value("ElectricalPower"));
            HeadValue.Text = F(_performance.HeadM);
            HydraulicValue.Text = F(_performance.HydraulicPowerKw);
            EfficiencyValue.Text = F(_performance.PumpEfficiencyPercent);
            PerformanceText.Text = $"Shaft power: {F(_performance.ShaftPowerKw)} kW · Electrical-input efficiency: {F(_performance.WireToWaterEfficiencyPercent)} %";
            if (_performance.PumpEfficiencyPercent > 100 || _performance.WireToWaterEfficiencyPercent > 100 || _performance.HeadM < 0)
                PerformanceText.Text += " · Check measurement basis / calibration: result outside normal pump-test domain.";
        }
        else PerformanceText.Text = "Hydraulic calculation unavailable: flow and both pressure samples must be Good and fresh.";

        BearingText.Text = $"DE temperature: {F(Value("TemperatureDE"))} °C — {BearingAssessment.Assess(Value("TemperatureDE"), _temperatureWarning)}\n"
            + $"NDE temperature: {F(Value("TemperatureNDE"))} °C — {BearingAssessment.Assess(Value("TemperatureNDE"), _temperatureWarning)}\n"
            + $"DE vibration: {F(Value("VibrationDE"))} mm/s RMS — {BearingAssessment.Assess(Value("VibrationDE"), _vibrationWarning)}\n"
            + $"NDE vibration: {F(Value("VibrationNDE"))} mm/s RMS — {BearingAssessment.Assess(Value("VibrationNDE"), _vibrationWarning)}";
        if (!_temperatureHistory.TryGetValue(_selectedPumpId, out var history))
            _temperatureHistory[_selectedPumpId] = history = new();
        if (Value("TemperatureDE") is double temperature)
            history.Add(new((_lastUpdate - _sessionStart).TotalSeconds, temperature));
        if (history.Count > 120) history.RemoveRange(0, history.Count - 120);
        TemperaturePlot.SetPoints(history);
        var waveform = frame.DriveEndWaveform;
        if (Value("VibrationDE").HasValue && waveform is not null
            && _lastUpdate - waveform.SampledAt <= TimeSpan.FromSeconds(3)
            && waveform.SampledAt <= _lastUpdate.AddMilliseconds(200))
        {
            var vibration = VibrationAnalysis.AnalyzeVelocity(waveform.SamplesMmPerSecond, waveform.SampleRateHz);
            VibrationText.Text = $"DE waveform: RMS {F(vibration.RmsMmS)} mm/s · Peak {F(vibration.PeakMmS)} mm/s · Peak-to-peak {F(vibration.PeakToPeakMmS)} mm/s\n"
                + $"Crest factor {F(vibration.CrestFactor)} · Dominant FFT bin {F(vibration.DominantFrequencyHz)} Hz · {waveform.SampleRateHz:F0} samples/s · {waveform.SamplesMmPerSecond.Length} samples · Δf {waveform.SampleRateHz / waveform.SamplesMmPerSecond.Length:F1} Hz";
            var mean = waveform.SamplesMmPerSecond.Average();
            WaveformPlot.SetPoints(waveform.SamplesMmPerSecond.Take(128).Select((v, i) => new PlotPoint(i / waveform.SampleRateHz * 1000, v - mean)));
            SpectrumPlot.SetPoints(vibration.Spectrum.Where(bin => bin.FrequencyHz <= 512).Select(bin => new PlotPoint(bin.FrequencyHz, bin.PeakAmplitudeMmS)));
        }
        else
        {
            VibrationText.Text = "Velocity waveform unavailable — no FFT or waveform metrics are calculated.";
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
        _channels.Clear();
        FlowValue.Text = HeadValue.Text = HydraulicValue.Text = EfficiencyValue.Text = "—";
        ChannelsGrid.ItemsSource = null;
        BearingText.Text = "Awaiting fresh bearing measurements";
        VibrationText.Text = "Awaiting fresh waveform";
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
        ConnectionText.Text = "Simulator disconnected";
        RouteText.Text = "All samples unavailable";
        PermissiveText.Text = "PLC signals unavailable";
        PerformanceText.Text = "Measurements unavailable";
        MessageText.Text = error.Message;
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
        if (!_ready || _rendering || Pumps.SelectedItem is not PumpSnapshot selected) return;
        _selectedPumpId = selected.Id;
        _selectedPump = selected;
        InvalidateMeasurements();
        RenderCurves();
        await UpdateAsync();
    }

    private async void OnScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ScenarioBox.SelectedValue is not DemoScenario scenario) return;
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
            MessageText.Text = "Session settings applied. Demo limits are advisory; density and geometry are included in CSV exports.";
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        { MessageText.Text = $"Settings unchanged: {error.Message}"; }
    }

    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        await UpdateAsync();
        UpdateButtons();
        if (!CaptureButton.IsEnabled || _performance is null) { MessageText.Text = "Cannot capture: a running pump and valid, plausible performance measurements are required."; return; }
        _points.Add(new(_lastUpdate, _selectedPumpId, _instruments.Scenario.ToString(), _performance, _rig,
            Value("Suction")!.Value, Value("Discharge")!.Value, Value("Torque"), Value("Speed"), Value("ElectricalPower")));
        RenderCurves();
        MessageText.Text = $"Captured simulated point {_points.Count}. Export CSV to preserve this session before closing.";
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) { MessageText.Text = "Capture at least one test point before exporting."; return; }
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = $"CFL-demo-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, TestSessionCsv.ExportSimulation(_points), new UTF8Encoding(true));
            MessageText.Text = $"Exported {_points.Count} simulated points to {dialog.FileName}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { MessageText.Text = $"Export failed: {error.Message}"; }
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
