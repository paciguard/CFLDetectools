using System.Globalization;
using CFLDetectools.Core;
using CFLDetectools.Simulator;

internal static class AnalysisChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        void Near(double actual, double expected, double tolerance, string name) => check(Math.Abs(actual - expected) <= tolerance, name);
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { check(true, name); return; }
            throw new InvalidOperationException(name);
        }
        var now = DateTimeOffset.UtcNow;
        var definition = new ChannelDefinition("test", "test", "test", "unit", -10, 100);
        EvaluatedChannel Evaluate(ChannelSample? sample) => MeasurementQuality.Evaluate(definition, sample, now, TimeSpan.FromSeconds(3));
        check(Evaluate(new(0, now)).Quality == SignalQuality.Good, "Zero reading is valid, not evidence of disconnection");
        check(Evaluate(new(10, now, Connected: false)).Quality == SignalQuality.Offline, "Offline route invalidates numeric reading");
        check(Evaluate(null).Quality == SignalQuality.Missing, "Absent measurement is missing");
        check(Evaluate(new(null, now)).Quality == SignalQuality.Missing, "Null value is missing");
        check(Evaluate(new(10, now, DeviceFault: true)).Quality == SignalQuality.DeviceFault, "Device diagnostic fault is retained");
        check(Evaluate(new(10, now.AddSeconds(-4))).Quality == SignalQuality.Stale, "Old samples are stale");
        check(Evaluate(new(10, now.AddSeconds(-3))).Quality == SignalQuality.Good, "Sample at freshness boundary is usable");
        check(Evaluate(new(10, now.AddSeconds(2))).Quality == SignalQuality.Invalid, "Future timestamp is invalid");
        check(Evaluate(new(double.NaN, now)).Quality == SignalQuality.Invalid, "NaN measurement is invalid");
        check(Evaluate(new(double.PositiveInfinity, now)).Quality == SignalQuality.Invalid, "Infinite measurement is invalid");
        check(Evaluate(new(101, now)).Quality == SignalQuality.OutOfRange, "Engineering range violation is detected");
        check(Evaluate(new(10, now.AddSeconds(-4))).ValidValue is null, "Stale value cannot feed calculations");
        Near(MeasurementQuality.ScaleCurrent(4, -100, 100), -100, 1e-12, "4 mA maps to lower engineering endpoint");
        Near(MeasurementQuality.ScaleCurrent(12, -100, 100), 0, 1e-12, "12 mA maps to engineering midpoint");
        Near(MeasurementQuality.ScaleCurrent(20, -100, 100), 100, 1e-12, "20 mA maps to upper engineering endpoint");
        Reject(() => MeasurementQuality.ScaleCurrent(3.5, 0, 100), "Out-of-range loop current is rejected, not clamped");
        Reject(() => MeasurementQuality.ScaleCurrent(12, 100, 0), "Invalid scaling range is rejected");
        Reject(() => MeasurementQuality.ScaleCurrent(12, -1e308, 1e308), "Overflowing scaling range is rejected");

        var rig = new RigSettings(1000, 100, 100, 0);
        // Independent dimensional example: Q=.01 m³/s, Δp=98.0665 kPa gives H=10 m and Ph=.980665 kW.
        var torque = 2 * 1000 * 60 / (2 * Math.PI * 1500);
        var result = PumpPerformance.Calculate(rig, 36, -10, 88.0665, torque, 1500, 2.5);
        Near(result.HeadM, 10, 1e-10, "Pressure difference produces expected 10 m total head");
        Near(result.HydraulicPowerKw, 0.980665, 1e-10, "Flow unit conversion gives expected hydraulic power");
        Near(result.ShaftPowerKw!.Value, 2, 1e-10, "Torque and rpm produce shaft power in kW");
        Near(result.PumpEfficiencyPercent!.Value, 49.03325, 1e-8, "Pump efficiency uses shaft power");
        Near(result.WireToWaterEfficiencyPercent!.Value, 39.2266, 1e-8, "Electrical-input efficiency remains distinct");
        var offset = PumpPerformance.Calculate(rig, 36, 90, 188.0665);
        Near(offset.HeadM, result.HeadM, 1e-10, "Common pressure-reference offset cancels");
        var corrected = PumpPerformance.Calculate(new(1000, 200, 100, 2), 36, 0, 98.0665);
        var v1 = 0.01 / (Math.PI * 0.2 * 0.2 / 4);
        var v2 = 0.01 / (Math.PI * 0.1 * 0.1 / 4);
        Near(corrected.HeadM, 12 + (v2 * v2 - v1 * v1) / (2 * 9.80665), 1e-10, "Head includes velocity and tap-elevation corrections");
        check(offset.PumpEfficiencyPercent is null, "Missing shaft measurements do not imply zero efficiency");
        check(PumpPerformance.Calculate(rig, 0, 0, 0, 0, 0, 0).PumpEfficiencyPercent is null, "Zero shaft power cannot divide efficiency");
        check(PumpPerformance.Calculate(rig, 36, 100, 0, torque, 1500).PumpEfficiencyPercent is null, "Negative developed head has no pump efficiency grade");
        check(PumpPerformance.Calculate(rig, 0, 100, 0, torque, 1500, 2.5).PumpEfficiencyPercent is null, "Zero flow cannot conceal a negative-head condition");
        check(PumpPerformance.Calculate(rig, 36, 0, 98.0665, torque / 10, 1500).PumpEfficiencyPercent > 100, "Implausible efficiency remains visible instead of being capped");
        Reject(() => PumpPerformance.Calculate(new(0, 100, 100, 0), 36, 0, 100), "Nonpositive density is rejected");
        Reject(() => PumpPerformance.Calculate(new(1000, 0, 100, 0), 36, 0, 100), "Zero pipe diameter is rejected");
        Reject(() => PumpPerformance.Calculate(rig, -1, 0, 100), "Reverse flow is outside this calculation domain");
        Reject(() => PumpPerformance.Calculate(rig, 36, double.NaN, 100), "Nonfinite pressure is rejected");
        Reject(() => PumpPerformance.Calculate(rig, 36, 0, 100, -1, 1500), "Negative shaft torque is rejected");

        var sine = Enumerable.Range(0, 1024).Select(i => 5 + 3 * Math.Sin(2 * Math.PI * 64 * i / 1024)).ToArray();
        var vibration = VibrationAnalysis.AnalyzeVelocity(sine, 1024);
        Near(vibration.RmsMmS, 3 / Math.Sqrt(2), 1e-10, "Velocity RMS removes DC offset");
        Near(vibration.PeakMmS, 3, 1e-10, "Centered sine peak has calibrated amplitude");
        Near(vibration.PeakToPeakMmS, 6, 1e-10, "Peak-to-peak velocity is correct");
        Near(vibration.CrestFactor!.Value, Math.Sqrt(2), 1e-10, "Sine crest factor is sqrt(2)");
        Near(vibration.DominantFrequencyHz!.Value, 64, 1e-10, "FFT identifies a known 64 Hz tone");
        Near(vibration.Spectrum[64].PeakAmplitudeMmS, 3, 0.001, "Hann window gain is corrected in amplitude spectrum");
        var dc = VibrationAnalysis.AnalyzeVelocity(Enumerable.Repeat(12.0, 1024).ToArray(), 1024);
        check(dc.RmsMmS == 0 && dc.CrestFactor is null && dc.DominantFrequencyHz is null, "DC-only waveform has no vibration or dominant frequency");
        Reject(() => VibrationAnalysis.AnalyzeVelocity(new double[17], 1000), "Non-power-of-two FFT length is rejected");
        Reject(() => VibrationAnalysis.AnalyzeVelocity(sine, 0), "Invalid sample rate is rejected");
        var corrupt = sine.ToArray(); corrupt[10] = double.NaN;
        Reject(() => VibrationAnalysis.AnalyzeVelocity(corrupt, 1024), "Corrupt waveform samples are rejected");
        check(BearingAssessment.Assess(55, null) == "Limit not configured", "No universal bearing temperature limit is invented");
        check(BearingAssessment.Assess(null, 70) == "Signal unavailable", "Missing temperature cannot be graded healthy");
        check(BearingAssessment.Assess(70, 70) == "Above configured warning", "Warning threshold boundary is inclusive");

        var instruments = new SimulatedInstrumentClient();
        var stopped = await instruments.ReadAsync("P-101", false);
        check(stopped.Samples["Flow"].Value == 0 && stopped.Samples["Speed"].Value == 0, "Stopped demo has no flow or rotation");
        var running = await instruments.ReadAsync("P-101", true);
        check(running.Samples["Flow"].Value > 0 && running.DriveEndWaveform is not null, "Running demo supplies mixed-route measurements and waveform");
        instruments.Scenario = DemoScenario.PlcOffline;
        var offline = await instruments.ReadAsync("P-101", true);
        check(!offline.Samples["Flow"].Connected && offline.Samples["ElectricalPower"].Connected, "PLC outage does not claim direct instruments are offline");
        instruments.Scenario = DemoScenario.InstrumentOffline;
        var directOffline = await instruments.ReadAsync("P-101", true);
        check(directOffline.Samples["Flow"].Connected && !directOffline.Samples["Torque"].Connected, "Direct instrument outage leaves PLC route separate");
        instruments.Scenario = DemoScenario.DaqOffline;
        check((await instruments.ReadAsync("P-101", true)).DriveEndWaveform is null, "DAQ outage removes waveform");
        instruments.Scenario = DemoScenario.StalePressure;
        var stale = await instruments.ReadAsync("P-101", true);
        check(now - stale.Samples["Discharge"].SampledAt > TimeSpan.FromSeconds(3), "Stale scenario keeps an old sample timestamp");
        instruments.Scenario = DemoScenario.OutOfRange;
        check((await instruments.ReadAsync("P-101", true)).Samples["Flow"].Value > 200, "Range scenario can exercise invalid transmitter data");
        var point = new PumpTestPoint(now, "P-101", "Normal", result, rig, -10, 88.0665, torque, 1500, 2.5);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var csv = TestSessionCsv.ExportSimulation(new[] { point });
            check(csv.Contains("Simulation,\"P-101\",\"Normal\",36,"), "CSV preserves simulation provenance and invariant decimal convention");
            check(csv.Contains("density_kg_m3") && csv.Contains("electrical_kw"), "CSV includes rig settings and input-power basis");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }
}
