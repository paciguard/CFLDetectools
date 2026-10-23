# Siemens PLC engineering

Place reviewed PLC source exports and signal documentation here when available.
No live PLC program or hardware configuration is included in this starter.

Before implementing a driver, record the CPU model, firmware, engineering software
version, communication protocol, pump count and signal mapping. Define start/stop
handshake, request reset, feedback timeout and connection-loss behavior with the PLC
program. Addresses in `config/pump-signals.example.csv` are intentionally blank.

The application will request operation; the PLC must own equipment permissives,
interlocks and protection. A successful communication write alone is not running
feedback. WPF is the PC interface; Siemens PLC logic is a separate engineering task.
