using CFLDetectools.Core;

namespace CFLDetectools.Simulator;

// Entirely in memory: this client does not open a network connection or write a PLC.
public sealed class SimulatedPumpClient : IPumpClient
{
    private readonly Dictionary<string, PumpSnapshot> _pumps = new(StringComparer.Ordinal)
    {
        ["P-101"] = new("P-101", false, true, false, true, DateTimeOffset.UtcNow),
        ["P-102"] = new("P-102", false, true, false, true, DateTimeOffset.UtcNow)
    };
    private bool _connected;
    private bool _disposed;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connected = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PumpSnapshot>> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<PumpSnapshot> result = _pumps.Values
            .Select(pump => pump with { ObservedAt = now }).ToArray();
        return Task.FromResult(result);
    }

    public Task<CommandResult> SendAsync(string pumpId, PumpCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected(cancellationToken);
        if (!_pumps.TryGetValue(pumpId, out var pump))
            return Task.FromResult(new CommandResult(false, "Unknown pump."));

        if (command != PumpCommand.Start && command != PumpCommand.Stop)
            return Task.FromResult(new CommandResult(false, "Unknown command."));

        if (command == PumpCommand.Start && !pump.CanStart)
            return Task.FromResult(new CommandResult(false,
                "Start rejected: pump must be remote, ready, stopped and free of faults."));

        // A fault or loss of ready must not prevent a remote stop of a running pump.
        if (command == PumpCommand.Stop && !pump.CanStop)
            return Task.FromResult(new CommandResult(false,
                "Stop rejected: pump must be running in remote mode."));

        _pumps[pumpId] = pump with
        {
            Running = command == PumpCommand.Start,
            ObservedAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult(new CommandResult(true, $"SIMULATION: {pumpId} {command} accepted."));
    }

    // Test/scenario input injection; deliberately unavailable through IPumpClient.
    public void SetInputs(string pumpId, bool ready, bool fault, bool remoteMode)
    {
        EnsureConnected(CancellationToken.None);
        var pump = _pumps[pumpId];
        _pumps[pumpId] = pump with { Ready = ready, Fault = fault, RemoteMode = remoteMode };
    }

    public void Dispose()
    {
        _connected = false;
        _disposed = true;
    }

    private void EnsureConnected(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected) throw new InvalidOperationException("Client is disconnected.");
    }
}
