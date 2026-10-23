# Reference review

Original examples under `Referrings/` are retained locally as supplied and are
excluded from source control. This review does not execute bundled utilities or
change the examples.

## Micro/WIN SMART and legacy operator interface

- A native `.smart` project and legacy operator-interface project files are present.
- `CFL.awl` is zero bytes, so it supplies no readable PLC source or address map.
- Native PLC logic and binary HMI files have not been decoded or validated. A
  readable source/symbol export is needed before adopting addresses or interlocks.

## LabVIEW example

The project XML lists a Modbus Master dependency. VI filenames identify modules
for PLC VD reads/scaling, IEEE-754 conversion, Qingzhi/power-meter serial and
Modbus acquisition, sensor management, test tables/curves, database storage,
water-property lookup, curve fitting, performance reports and cavitation tests.

These findings come from the XML and file inventory. The graphical VI block
diagrams and their actual arithmetic, addresses, protocol framing, calibration
and acceptance logic have not yet been inspected. Names alone do not verify how
those functions work.

## Applied design decisions

- Separate PLC, direct-instrument and waveform acquisition routes.
- Preserve units, quality and timestamp on samples.
- Separate measurement acquisition from numerical calculations.
- Keep shaft and electrical power/efficiency distinct.
- Support manually captured test points and CSV export before adding automation.
- Leave device-specific maps and acceptance grades unconfigured until reviewed.
