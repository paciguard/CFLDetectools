namespace CFLDetectools.Core;

public sealed record RigSettings(double DensityKgM3, double SuctionDiameterMm,
    double DischargeDiameterMm, double DischargeMinusSuctionElevationM)
{
    public void Validate()
    {
        if (!double.IsFinite(DensityKgM3) || DensityKgM3 <= 0
            || !double.IsFinite(SuctionDiameterMm) || SuctionDiameterMm <= 0
            || !double.IsFinite(DischargeDiameterMm) || DischargeDiameterMm <= 0
            || !double.IsFinite(DischargeMinusSuctionElevationM))
            throw new ArgumentException("Density and pipe diameters must be positive; all rig settings must be finite.");
    }
}

public sealed record PerformanceResult(double FlowM3H, double HeadM, double HydraulicPowerKw,
    double? ShaftPowerKw, double? PumpEfficiencyPercent, double? WireToWaterEfficiencyPercent);

public static class PumpPerformance
{
    public const double Gravity = 9.80665;

    // Pressure inputs are kPa on the same reference basis at the two measurement taps.
    public static PerformanceResult Calculate(RigSettings rig, double flowM3H,
        double suctionKpa, double dischargeKpa, double? torqueNm = null,
        double? speedRpm = null, double? electricalPowerKw = null)
    {
        rig.Validate();
        if (!double.IsFinite(flowM3H) || flowM3H < 0 || !double.IsFinite(suctionKpa)
            || !double.IsFinite(dischargeKpa)) throw new ArgumentException("Invalid hydraulic measurements.");
        foreach (var optional in new[] { torqueNm, speedRpm, electricalPowerKw })
            if (optional.HasValue && (!double.IsFinite(optional.Value) || optional.Value < 0))
                throw new ArgumentException("Power, speed and torque must be finite and nonnegative.");

        var q = flowM3H / 3600;
        var suctionVelocity = q / (Math.PI * Math.Pow(rig.SuctionDiameterMm / 1000, 2) / 4);
        var dischargeVelocity = q / (Math.PI * Math.Pow(rig.DischargeDiameterMm / 1000, 2) / 4);
        var head = (dischargeKpa - suctionKpa) * 1000 / (rig.DensityKgM3 * Gravity)
            + (dischargeVelocity * dischargeVelocity - suctionVelocity * suctionVelocity) / (2 * Gravity)
            + rig.DischargeMinusSuctionElevationM;
        var hydraulic = rig.DensityKgM3 * Gravity * q * head / 1000;
        double? shaft = torqueNm.HasValue && speedRpm.HasValue
            ? torqueNm * 2 * Math.PI * speedRpm / 60 / 1000 : null;
        // Negative developed head is reported, but is outside this pump-efficiency calculation's domain.
        double? efficiency = shaft > 0 && head >= 0 ? 100 * hydraulic / shaft : null;
        double? wireToWater = electricalPowerKw > 0 && head >= 0 ? 100 * hydraulic / electricalPowerKw : null;
        if (!double.IsFinite(head) || !double.IsFinite(hydraulic)
            || (shaft.HasValue && !double.IsFinite(shaft.Value))
            || (efficiency.HasValue && !double.IsFinite(efficiency.Value))
            || (wireToWater.HasValue && !double.IsFinite(wireToWater.Value)))
            throw new ArgumentException("Measurements exceed the supported numeric range.");
        return new(flowM3H, head, hydraulic, shaft, efficiency, wireToWater);
    }
}
