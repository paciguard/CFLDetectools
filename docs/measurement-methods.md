# Pump test measurement and analysis

## Current implementation and input routes

The Windows dashboard currently runs entirely with generated data. It supports
separate sample quality for PLC analog channels, direct instruments and waveform
DAQ channels. The user confirmed that the eventual test bench uses a mixture of
PLC inputs and direct instruments. Actual transport drivers are not implemented.

Each sample carries its acquisition timestamp and connection/device status.
Offline, missing, device-fault, nonfinite, future-dated, stale and out-of-range
values are excluded from calculations. The demo freshness limit is three seconds;
real channel limits must follow their acquisition rates. A steady or zero reading
does not by itself imply disconnection. Connection detection for the real bench
must combine transport response, device/module diagnostics and sample freshness.

The nominal linear 4–20 mA helper converts a configured engineering range without
clamping out-of-range currents. It is not a universal wire-break detector or a
device-specific NE43 implementation. Verify raw-count scaling, fault thresholds,
sensor type, calibration, register type and byte order against each instrument.

## Pump performance

All calculations use a consistent pressure reference (both gauge or both absolute),
fluid density rho in kg/m³ and g = 9.80665 m/s². The current measurement columns
are gauge pressure in kPa and flow in m³/h. At the pressure taps:

```text
Q [m³/s] = flow [m³/h] / 3600
A [m²] = pi * (internal diameter [mm] / 1000)^2 / 4
v [m/s] = Q / A
H [m] = (p_discharge - p_suction) * 1000 / (rho * g)
        + (v_discharge² - v_suction²) / (2*g)
        + (z_discharge - z_suction)
P_hydraulic [kW] = rho * g * Q * H / 1000
P_shaft [kW] = torque [N·m] * 2*pi * speed [rpm] / 60 / 1000
Pump efficiency [%] = 100 * P_hydraulic / P_shaft
Electrical-input efficiency [%] = 100 * P_hydraulic / P_electrical
```

This is incompressible, forward-flow analysis with unit kinetic-energy correction
factors. Pressure-tap geometry must represent the chosen pump measurement sections;
remote taps may require additional documented loss corrections. Density is an
explicit session input, not automatically inferred from water temperature. A
power meter's electrical input is not pump shaft power. Missing or zero power
produces unavailable efficiency, not a zero or fabricated pump-efficiency value.
Negative developed head and efficiencies over 100% are exposed for investigation;
implausible points cannot be captured as valid demo performance points.

Captured session points retain the pump, scenario, time, raw hydraulic/power inputs
and rig settings. Q–H and Q–efficiency plots show the selected pump and matching rig
settings. Connecting lines are not a fitted model. CSV marks all records as
Simulation and uses invariant numeric formatting. Export the in-memory session
before closing; database storage and automated steady-state capture are future work.

Reference: [KSB Head](https://www.ksb.com/en-global/centrifugal-pump-lexicon/article/head-1115612)
and [KSB Efficiency](https://www.ksb.com/en-global/centrifugal-pump-lexicon/article/efficiency-1117172).

## Bearing vibration

The implemented waveform path accepts calibrated, uniformly sampled **velocity in
mm/s**. It removes the mean and calculates RMS, absolute peak, peak-to-peak and
crest factor (peak/RMS). A zero signal has no crest factor or dominant frequency.
The radix-2 FFT uses a Hann window and coherent-gain correction; the displayed
single-sided spectrum is peak amplitude in mm/s, not a PSD or an RMS spectrum.
The dominant value is an FFT bin, without sub-bin peak interpolation.

Demo acquisition uses 1024 samples at 4096 samples/s: record length 0.25 s,
frequency-bin spacing 4 Hz, Nyquist frequency 2048 Hz. The dashboard shows the
first 128 waveform samples and the 0–512 Hz portion of the spectrum. Neither is a
statement about the bandwidth or accuracy of the eventual sensors.

A 4–20 mA overall-vibration transmitter can provide a scalar trend but cannot
reconstruct its original waveform. Bearing-fault frequency analysis needs suitable
waveform acquisition, calibration, sampling/anti-alias filtering, mounting/axis
information, operating speed and bearing geometry. Acceleration integration,
band-limited severity, envelope analysis, order tracking and ISO classification
are not implemented. Operator-configured RMS warnings remain advisory.

References: [NI vibration analysis](https://www.ni.com/en/shop/labview/vibration-analysis-and-signal-processing-in-labview.html)
and [NI peak detection and anti-aliasing](https://www.ni.com/en/support/documentation/supplemental/06/peak-detection-using-labview-and-measurement-studio.html).

## Bearing temperature

The dashboard shows drive-end and non-drive-end temperatures, a history of valid
drive-end readings, and optional operator-configured warning thresholds. Missing
values have no health grade; an unconfigured threshold is explicitly labeled.
Historical charts leave gaps across missing periods. There are no built-in
universal temperature trip limits. Rate-of-rise assessment, ambient compensation,
sensor-drift analysis, alarm persistence/hysteresis and PLC trip integration remain
future work once the machine and transmitter specifications are supplied.

## Next hardware information

Confirm the S7-200 SMART CPU/model, firmware and reviewed signal map; PLC analog
module types and scaling; each direct instrument's make/model, connection,
register/protocol and update rate; waveform sensor/DAQ hardware and sample rate;
power measurement basis; test-fluid properties and tap geometry; and the required
test/acceptance standard. Keep actual network and serial settings local.
