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

## Connection diagnostic scope

The desktop **PLC diagnostics** tab establishes a TCP / COTP / S7 Setup
Communication session and holds it until the operator disconnects or exits. It uses the S7-200 SMART rack-0/slot-0
preset: local TSAP `01.00`, remote TSAP `03.00`, TCP port 102. It negotiates a PDU
of at most 480 bytes. A successful result proves session establishment at the
displayed connection time; it does not verify CPU identity, pump addresses, I/O access,
signal quality, application-level S7 responsiveness or command handling. TCP keepalive
and a pending socket read detect transport closure, potentially after a delay. They do
not guarantee the PLC program is operating or that its data is accessible.

The diagnostic has no variable-read, variable-write, program-transfer, CPU-control
or clock-setting operations. It is deliberately separate from `IPumpClient` and
does not consume the reference project's signal map. All displayed pump data and
demo commands remain simulated regardless of this diagnostic's result. A live
signal driver still requires the prerequisites above.

Automated checks use loopback protocol fixtures, including fragmented replies,
invalid packets, S7 rejection, premature EOF, timeout and cancellation. Hardware
checks must be requested explicitly and reported separately from these checks.

## CPU RUN research

The Siemens Web API documented for firmware V2.7 and later can return CPU
RUN/STOP, system and force status in a read response, including an empty read.
It requires the PLC Web server, TLS configuration and an authorized Web user.
The documented API methods do not include an operation to change CPU mode;
the Web server's own RUN button should not be confused with a documented API
command. Do not bypass certificate validation or commit credentials.

The [Siemens Web API development manual](https://cache.industry.siemens.com/dl/files/447/109813447/att_1115298/v1/S7-200_Smart_Web_API_development_manual_US.pdf)
describes those requirements and methods. A C# [HslCommunication source copy](https://github.com/Hadisi234/HslCommunication_SourceCode_9.2.6/blob/8b97dff0a13c18f75cc64fc33a8d5cdeedc7f21f/HslCommunication.Profinet.Siemens/SiemensS7Net.cs)
describes its hot-start operation as applicable to S7-200 SMART. This is a
third-party implementation, not evidence that a particular CPU, firmware,
access level or response handling has been verified. Its licensing and binary
provenance also require review before use. The diagnostic session above cannot
send that command and must not claim to read the CPU mode.

Before implementing CPU RUN, establish an independently verified current mode,
identify a supported and correctly acknowledged command for the exact CPU, and
verify the resulting mode after a single operator-requested transition. A lost
response makes the outcome unknown, not a reason to retry automatically. Keep
CPU mode control distinct from simulated pump commands and do not stop a running
CPU merely to test a start transition.

## Siemens references

- [S7-200 SMART system manual](https://cache.industry.siemens.com/dl/files/610/109745610/att_1010424/v1/s7-200_SMART_system_manual_en-US.pdf):
  CPU models, Ethernet communication and ISO-on-TCP.
- [S7-PLCSIM V14 online help](https://cache.industry.siemens.com/dl/files/755/109741755/att_897166/v1/S7-PLCSIMenUS_en-US.pdf):
  supported CPU families and simulation startup requirements.
- [RFC 1006](https://www.rfc-editor.org/rfc/rfc1006): TPKT framing over TCP.
- [S7NetPlus SMART TSAP presets](https://github.com/S7NetPlus/s7netplus/blob/main/S7.Net/Protocol/TsapPair.cs)
  and [connection establishment](https://github.com/S7NetPlus/s7netplus/blob/main/S7.Net/PlcAsynchronous.cs):
  primary implementation references for COTP connection and S7 setup negotiation.
