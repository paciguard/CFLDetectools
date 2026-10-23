using CFLDetectools.Core;

namespace CFLDetectools.Simulator;

public enum DemoScenario { Normal, PlcOffline, InstrumentOffline, StalePressure, DaqOffline, OutOfRange, HotBearing, HighVibration }

public sealed class SimulatedInstrumentClient : IInstrumentClient
{
    // These are demonstration ranges and routes, not the site's actual channel configuration.
    public static IReadOnlyList<ChannelDefinition> Channels { get; } = new ChannelDefinition[]
    {
        new("Flow", "Flow transmitter", "PLC analog", "m³/h", 0, 200),
        new("Suction", "Suction pressure", "PLC analog", "kPa(g)", -100, 1000),
        new("Discharge", "Discharge pressure", "PLC analog", "kPa(g)", -100, 2000),
        new("Speed", "Speed instrument", "Direct instrument", "rpm", 0, 4000),
        new("Torque", "Shaft torque", "Direct instrument", "N·m", 0, 1000),
        new("ElectricalPower", "Electrical input power", "Direct instrument", "kW", 0, 300),
        new("TemperatureDE", "Drive-end bearing temperature", "PLC analog", "°C", -50, 250),
        new("TemperatureNDE", "Non-drive-end bearing temperature", "PLC analog", "°C", -50, 250),
        new("VibrationDE", "Drive-end velocity RMS", "Waveform DAQ", "mm/s", 0, 100),
        new("VibrationNDE", "Non-drive-end velocity RMS", "Waveform DAQ", "mm/s", 0, 100)
    };

    public DemoScenario Scenario { get; set; }
    public double FlowSettingM3H { get; set; } = 100;

    public Task<InstrumentFrame> ReadAsync(string pumpId, bool running, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pumpId is not ("P-101" or "P-102")) throw new ArgumentException("Unknown demo pump.");
        var now = DateTimeOffset.UtcNow;
        var phase = now.ToUnixTimeMilliseconds() / 1000.0;
        var q = running ? FlowSettingM3H + 0.15 * Math.Sin(phase) : 0;
        var head = running ? 50 - 0.001 * q * q : 0;
        var suction = 20.0;
        var discharge = suction + head * 998.2 * PumpPerformance.Gravity / 1000;
        var hydraulic = 998.2 * PumpPerformance.Gravity * q / 3600 * head / 1000;
        var shaft = running ? hydraulic / 0.76 + 0.3 : 0;
        var speed = running ? 1480.0 : 0;
        var torque = running ? shaft * 1000 * 60 / (2 * Math.PI * speed) : 0;
        var amplitude = running ? Scenario == DemoScenario.HighVibration ? 9.0 : 2.2 : 0;
        var waveform = Enumerable.Range(0, 1024).Select(i =>
            amplitude * Math.Sin(2 * Math.PI * 24 * i / 4096)
            + amplitude * 0.18 * Math.Sin(2 * Math.PI * 120 * i / 4096)).ToArray();
        var rms = Math.Sqrt(waveform.Average(v => v * v));
        var values = new Dictionary<string, double>
        {
            ["Flow"] = q, ["Suction"] = suction, ["Discharge"] = discharge,
            ["Speed"] = speed, ["Torque"] = torque, ["ElectricalPower"] = shaft / 0.91,
            ["TemperatureDE"] = Scenario == DemoScenario.HotBearing ? 95 : running ? 56 + Math.Sin(phase / 10) : 28,
            ["TemperatureNDE"] = running ? 51 + 0.5 * Math.Sin(phase / 10) : 27,
            ["VibrationDE"] = rms, ["VibrationNDE"] = rms * 0.8
        };
        var samples = Channels.ToDictionary(channel => channel.Id, channel => new ChannelSample(
            values[channel.Id], now,
            !(Scenario == DemoScenario.PlcOffline && channel.Route == "PLC analog"
              || Scenario == DemoScenario.InstrumentOffline && channel.Route == "Direct instrument"
              || Scenario == DemoScenario.DaqOffline && channel.Route == "Waveform DAQ")));
        if (Scenario == DemoScenario.StalePressure) samples["Discharge"] = samples["Discharge"] with { SampledAt = now.AddSeconds(-10) };
        if (Scenario == DemoScenario.OutOfRange) samples["Flow"] = samples["Flow"] with { Value = 250 };
        return Task.FromResult(new InstrumentFrame(samples,
            Scenario == DemoScenario.DaqOffline ? null : new VelocityWaveform(waveform, 4096, now)));
    }
}
