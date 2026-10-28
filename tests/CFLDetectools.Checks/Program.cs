using CFLDetectools.Core;
using CFLDetectools.Simulator;

// Explicit manual hardware diagnostic. Normal verification never contacts a PLC.
if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--probe-plc" || !PlcConnectionDiagnostic.TryParseAddress(args[1], out var endpoint))
        throw new ArgumentException("Usage: --probe-plc <PLC_IPv4>");
    var probe = await PlcConnectionDiagnostic.TestAsync(endpoint);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(probe));
    Environment.ExitCode = probe.SessionConfirmed ? 0 : 1;
    return;
}

using var client = new SimulatedPumpClient();
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
    Console.WriteLine($"PASS: {description}");
}

try
{
    await client.ReadAsync();
    throw new Exception("Disconnected read should fail.");
}
catch (InvalidOperationException) { Check(true, "Disconnected read is rejected"); }
try
{
    await client.SendAsync("P-101", PumpCommand.Start);
    throw new Exception("Disconnected command should fail.");
}
catch (InvalidOperationException) { Check(true, "Disconnected command is rejected"); }
await client.ConnectAsync();
Check((await client.ReadAsync()).Count == 2, "Two simulated pumps available");
Check((await client.SendAsync("missing", PumpCommand.Start)).Accepted == false, "Unknown pump is rejected");
Check(!(await client.SendAsync("P-101", (PumpCommand)999)).Accepted, "Unknown command is rejected");
Check((await client.SendAsync("P-101", PumpCommand.Start)).Accepted, "Ready remote pump starts");
Check((await client.ReadAsync())[0].Running, "Running feedback follows simulated start");
Check(!(await client.ReadAsync())[1].Running, "Other pump remains unchanged");
Check(!(await client.SendAsync("P-101", PumpCommand.Start)).Accepted, "Duplicate start rejected");
client.SetInputs("P-101", ready: false, fault: true, remoteMode: true);
Check((await client.SendAsync("P-101", PumpCommand.Stop)).Accepted, "Fault and loss of ready do not block remote stop");
Check(!(await client.SendAsync("P-101", PumpCommand.Start)).Accepted, "Faulted pump cannot start");
Check((await client.ReadAsync())[0].State == PumpState.Faulted, "Fault takes precedence over stopped state");
client.SetInputs("P-101", ready: false, fault: false, remoteMode: true);
Check(!(await client.SendAsync("P-101", PumpCommand.Start)).Accepted, "Not-ready pump cannot start");
client.SetInputs("P-101", ready: true, fault: false, remoteMode: false);
Check(!(await client.SendAsync("P-101", PumpCommand.Start)).Accepted, "Local mode blocks remote start");
client.SetInputs("P-101", ready: true, fault: false, remoteMode: true);
await client.SendAsync("P-101", PumpCommand.Start);
client.SetInputs("P-101", ready: true, fault: false, remoteMode: false);
Check(!(await client.SendAsync("P-101", PumpCommand.Stop)).Accepted, "Local mode blocks remote stop");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await client.ReadAsync(cancelled.Token);
    throw new Exception("Cancelled read should fail.");
}
catch (OperationCanceledException) { Check(true, "Cancelled read is rejected"); }
client.Dispose();
try
{
    await client.ConnectAsync();
    throw new Exception("Disposed client should fail.");
}
catch (ObjectDisposedException) { Check(true, "Disposed client cannot reconnect"); }
await AnalysisChecks.RunAsync(Check);
await PlcDiagnosticChecks.RunAsync(Check);
Console.WriteLine($"{checks} checks passed.");
