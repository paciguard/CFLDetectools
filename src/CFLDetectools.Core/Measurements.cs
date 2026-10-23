namespace CFLDetectools.Core;

public enum SignalQuality { Good, Offline, Missing, Stale, DeviceFault, Invalid, OutOfRange }

public sealed record ChannelDefinition(string Id, string Name, string Route, string Unit,
    double Minimum, double Maximum);

public sealed record ChannelSample(double? Value, DateTimeOffset SampledAt,
    bool Connected = true, bool DeviceFault = false);

public sealed record EvaluatedChannel(ChannelDefinition Definition, ChannelSample? Sample,
    SignalQuality Quality)
{
    public double? ValidValue => Quality == SignalQuality.Good ? Sample?.Value : null;
}

public static class MeasurementQuality
{
    public static EvaluatedChannel Evaluate(ChannelDefinition definition, ChannelSample? sample,
        DateTimeOffset now, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        var quality = sample switch
        {
            null => SignalQuality.Missing,
            { Connected: false } => SignalQuality.Offline,
            { DeviceFault: true } => SignalQuality.DeviceFault,
            { Value: null } => SignalQuality.Missing,
            _ when !double.IsFinite(sample.Value!.Value) => SignalQuality.Invalid,
            _ when sample.SampledAt > now.AddMilliseconds(200) => SignalQuality.Invalid,
            _ when now - sample.SampledAt > maximumAge => SignalQuality.Stale,
            _ when sample.Value < definition.Minimum || sample.Value > definition.Maximum => SignalQuality.OutOfRange,
            _ => SignalQuality.Good
        };
        return new(definition, sample, quality);
    }

    // For configured linear 4–20 mA channels only. Never clamp failed signals to a plausible value.
    public static double ScaleCurrent(double currentMa, double minimum, double maximum)
    {
        if (!double.IsFinite(currentMa) || currentMa < 4 || currentMa > 20)
            throw new ArgumentOutOfRangeException(nameof(currentMa), "Current is outside the nominal 4–20 mA range.");
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            throw new ArgumentException("Engineering range must be finite and increasing.");
        var scaled = minimum + (currentMa - 4) / 16 * (maximum - minimum);
        if (!double.IsFinite(scaled)) throw new ArgumentException("Scaling range exceeds the supported numeric range.");
        return scaled;
    }
}

public sealed record VelocityWaveform(double[] SamplesMmPerSecond, double SampleRateHz,
    DateTimeOffset SampledAt);

public sealed record InstrumentFrame(IReadOnlyDictionary<string, ChannelSample> Samples,
    VelocityWaveform? DriveEndWaveform);

public interface IInstrumentClient
{
    Task<InstrumentFrame> ReadAsync(string pumpId, bool running,
        CancellationToken cancellationToken = default);
}
