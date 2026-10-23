# CFLDetectools

Windows desktop application starter for Siemens PLC signals and pump running status
monitoring and control. The desktop interface uses C# and WPF.

The confirmed PLC family is **S7-200 SMART**, programmed using **STEP 7-Micro/WIN
SMART**. See the [S7-200 SMART integration plan](plc/S7-200-SMART.md).

## Current implementation

- WPF test station with simulated pump control and a transmitter/instrument table.
- Chinese/English UI switch in the upper right (Chinese by default), including
  live statuses, channel names, charts and operator messages. Switching preserves
  the current session; numeric values, units and CSV column names remain stable.
- Separate PLC analog, direct-instrument and waveform-DAQ sample quality.
- Offline, stale, device-fault and out-of-range signal handling.
- Total head, hydraulic/shaft power, pump efficiency and electrical-input efficiency.
- Bearing temperatures, configurable advisory limits, velocity waveform metrics and FFT.
- Captured Q–H/efficiency test points, plots and CSV session export.
- Shared acquisition contracts, an in-memory simulator and executable behavior checks.

**The current application runs only in simulation mode.** No Siemens communication
driver is installed, and no command reaches equipment. Live transmitter diagnostics,
device protocols, persistent history, equipment alarms, user permissions and
deployment remain future work. See [measurement methods and limits](docs/measurement-methods.md)
and the [reference review](docs/reference-review.md).

## Run locally

Use a Windows PC with the .NET 8 SDK and Windows Desktop targeting pack, or open
`CFLDetectools.sln` in Visual Studio with the .NET desktop development workload.

```powershell
dotnet build CFLDetectools.sln
dotnet run --project src/CFLDetectools.Desktop
```

Select P-101 or P-102 and use **Start demo**. Inspect the live measurements and
bearing plots, then try the fault scenarios. Change the simulated flow setting and
capture several points in **Test points and curves**, then export CSV. Configure
fluid/geometry and optional advisory warnings in **Rig and warning settings**.
Settings and captured points are session-only; export before closing. No PLC
connection settings or Siemens software are needed for this demo.

Use the **中文 / English** selector in the upper right to change the interface
language immediately. Each new application session starts in Chinese. Native
Windows file-dialog buttons follow the Windows display language.

## Develop on another Windows laptop

Install Git and the .NET 8 SDK with Windows Desktop build support. If using Visual
Studio, install its .NET desktop development workload. Install a compatible
STEP 7-Micro/WIN SMART version separately when preparing for hardware debugging;
Siemens software is not bundled in this repository. The in-memory simulator
does not require Siemens software.

Clone the repository into any writable local folder:

```powershell
git clone https://github.com/paciguard/CFLDetectools.git
cd CFLDetectools
./scripts/verify.ps1
dotnet run --project src/CFLDetectools.Desktop
```

Commit and push completed work before switching computers. On the other computer,
start from a clean working tree and run `git pull --ff-only` before editing. Local
changes must be committed or otherwise preserved before pulling; do not overwrite
them. The project uses relative paths and has no dependency on the original PC's
drive letters or Siemens installation paths. CPU addresses and network settings
must be configured locally when hardware integration is added.

Run the dependency-free behavior checks explicitly (`dotnet test` does not run them):

```powershell
dotnet run --project tests/CFLDetectools.Checks
```

## Project layout

| Folder | Purpose |
| --- | --- |
| `src/CFLDetectools.Desktop` | WPF window and polling |
| `src/CFLDetectools.Core` | Pump status, commands and communication contract |
| `src/CFLDetectools.Simulator` | In-memory implementation |
| `tests/CFLDetectools.Checks` | Simulator command and lifecycle checks |
| `config` | Example signal inventory; not loaded by the simulator |
| `plc` | Siemens PLC integration requirements and future source exports |
| `docs` | Analysis methods, limitations and reference-review findings |

## Next integration inputs

1. Exact S7-200 SMART CPU model, firmware and Micro/WIN SMART version.
2. Available communication protocol and connection parameters.
3. Pump count, signal list, addresses and data types.
4. Start/Stop request handshake, interlocks and feedback timing.
5. Direct instrument/DAQ models, protocols, calibration and sample rates; see
   `config/instruments.example.csv` for the inventory template (not loaded by the demo).

Implement a separate `IPumpClient` driver after those details are confirmed.
The simulator changes feedback immediately; a live driver must read real feedback
and handle stale samples, write acknowledgements and command timeouts. Equipment
protection and interlocks belong in the PLC program.

## GitHub

Repository: [paciguard/CFLDetectools](https://github.com/paciguard/CFLDetectools)

Completed updates are validated locally, committed and pushed to the GitHub
repository. GitHub Actions then independently builds the Windows solution and runs
the same behavior checks on every push and pull request. The workflow also supports
manual runs from the [Actions page](https://github.com/paciguard/CFLDetectools/actions).

Run the shared local/CI verification command from the project folder:

```powershell
./scripts/verify.ps1
```

The script builds in Release configuration and exits with a failure if the build
or any behavior check fails. This validates compilation and simulator behavior;
it does not exercise a real PLC or visually test the desktop interface.

Changes are synchronized at completed updates through commits and pushes. Editing
a local file alone does not publish it. Keep local credentials and site-specific
connection settings out of source control.
