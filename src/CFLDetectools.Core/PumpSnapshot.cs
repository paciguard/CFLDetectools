namespace CFLDetectools.Core;

public enum PumpState { Stopped, Running, Faulted }
public enum PumpCommand { Start, Stop }

public sealed record PumpSnapshot(
    string Id,
    bool Running,
    bool Ready,
    bool Fault,
    bool RemoteMode,
    DateTimeOffset ObservedAt)
{
    public PumpState State => Fault ? PumpState.Faulted
        : Running ? PumpState.Running : PumpState.Stopped;

    public bool CanStart => RemoteMode && Ready && !Fault && !Running;
    public bool CanStop => RemoteMode && Running;
}

public sealed record CommandResult(bool Accepted, string Message);
