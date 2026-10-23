using System.Numerics;

namespace CFLDetectools.Core;

public sealed record SpectrumBin(double FrequencyHz, double PeakAmplitudeMmS);
public sealed record VibrationResult(double RmsMmS, double PeakMmS, double PeakToPeakMmS,
    double? CrestFactor, double? DominantFrequencyHz, IReadOnlyList<SpectrumBin> Spectrum);

public static class VibrationAnalysis
{
    // Calibrated velocity samples in mm/s, uniformly spaced. Remove mean before metrics and FFT.
    public static VibrationResult AnalyzeVelocity(IReadOnlyList<double> samples, double sampleRateHz)
    {
        var n = samples.Count;
        if (n < 16 || n > 65536 || (n & (n - 1)) != 0)
            throw new ArgumentException("Waveform length must be a power of two from 16 to 65536.");
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0 || samples.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("Samples and sample rate must be finite; sample rate must be positive.");
        var mean = samples.Average();
        var centered = samples.Select(value => value - mean).ToArray();
        var rms = Math.Sqrt(centered.Average(value => value * value));
        var peak = centered.Max(Math.Abs);
        var fft = new Complex[n];
        double windowSum = 0;
        for (var i = 0; i < n; i++)
        {
            var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            windowSum += window;
            fft[i] = new Complex(centered[i] * window, 0);
        }
        // Iterative radix-2 FFT with bit-reversed input.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (fft[i], fft[j]) = (fft[j], fft[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var start = 0; start < n; start += length)
            {
                var rotation = Complex.One;
                for (var j = 0; j < length / 2; j++)
                {
                    var even = fft[start + j];
                    var odd = fft[start + j + length / 2] * rotation;
                    fft[start + j] = even + odd;
                    fft[start + j + length / 2] = even - odd;
                    rotation *= step;
                }
            }
        }
        var spectrum = Enumerable.Range(0, n / 2 + 1).Select(i => new SpectrumBin(
            i * sampleRateHz / n, fft[i].Magnitude / windowSum * (i == 0 || i == n / 2 ? 1 : 2))).ToArray();
        if (!double.IsFinite(rms) || !double.IsFinite(peak) || spectrum.Any(bin => !double.IsFinite(bin.PeakAmplitudeMmS)))
            throw new ArgumentException("Waveform exceeds the supported numeric range.");
        var dominant = spectrum.Skip(1).MaxBy(bin => bin.PeakAmplitudeMmS)!;
        return new(rms, peak, centered.Max() - centered.Min(), rms > 0 ? peak / rms : null,
            rms > 0 ? dominant.FrequencyHz : null, spectrum);
    }
}

public static class BearingAssessment
{
    // User-configured advisory thresholds, not equipment trips or a universal severity standard.
    public static string Assess(double? value, double? warningLimit)
    {
        if (!value.HasValue || !double.IsFinite(value.Value)) return "Signal unavailable";
        if (!warningLimit.HasValue) return "Limit not configured";
        if (!double.IsFinite(warningLimit.Value)) throw new ArgumentException("Limit must be finite.");
        return value >= warningLimit ? "Above configured warning" : "Below configured warning";
    }
}
