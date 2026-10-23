# S7-200 SMART integration plan

## Confirmed scope

The target PLC family is Siemens S7-200 SMART. Use STEP 7-Micro/WIN SMART for
engineering its program. WPF remains the Windows application's interface.

Hardware development and debugging are planned for another Windows laptop.
Prepare that machine using the setup instructions in the repository README, and
confirm its Siemens software version and network connection at that time.

S7-PLCSIM V14 supports S7-1200, S7-1500 and ET 200SP simulation, not S7-200 SMART.
The installed simulator therefore cannot validate an S7-200 SMART program or
prove this application's communication with that family. The application's own
in-memory simulator remains available for desktop development.

## Information required before connection

- Exact CPU model/order number and firmware; do not assume every SMART CPU has
  Ethernet. For example, the CR20s/CR30s/CR40s/CR60s models documented in the
  Siemens system manual have no Ethernet port.
- Physical connection and CPU IP address for Ethernet-capable hardware, or the
  appropriate serial interface for a model without Ethernet.
- Micro/WIN project or a reviewed signal list with addresses and data types.
- Pump count and meanings of Running, Ready, Fault and RemoteMode.
- StartRequest/StopRequest semantics, including acknowledgement, reset and
  feedback timeout. Do not assume requests are maintained bits or pulses.

Store actual site addresses and connection settings in ignored local files.
`config/pump-signals.example.csv` is an inventory template with blank addresses;
the application does not yet load it.

## First hardware test

1. Confirm the exact CPU and supported communication interface.
2. For Ethernet hardware, verify the supplied CPU endpoint is reachable.
   Siemens documents S7 communication over ISO-on-TCP, port 102. An open port
   alone does not prove a successful S7 session or access to pump data.
3. Confirm the existing Micro/WIN project matches the connected CPU, then read
   an agreed status signal without changing the CPU program or operating mode.
4. Implement an `IPumpClient` driver with validated addressing, timeouts and
   connection-loss handling; compare its readings with Micro/WIN monitoring.
5. Test command handling only after the address map and handshake are reviewed
   and the user authorizes the specific equipment operation.

No hardware connection or Siemens driver has been validated yet. Do not treat a
successful in-memory simulator check as a hardware communication test.

## Siemens references

- [S7-200 SMART system manual](https://cache.industry.siemens.com/dl/files/610/109745610/att_1010424/v1/s7-200_SMART_system_manual_en-US.pdf):
  CPU models, Ethernet communication and ISO-on-TCP.
- [S7-PLCSIM V14 online help](https://cache.industry.siemens.com/dl/files/755/109741755/att_897166/v1/S7-PLCSIMenUS_en-US.pdf):
  supported CPU families and simulation startup requirements.
