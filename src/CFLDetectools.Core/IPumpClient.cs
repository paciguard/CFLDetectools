namespace CFLDetectools.Core;

// Live implementations must distinguish request acknowledgement from running feedback.
public interface IPumpClient : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PumpSnapshot>> ReadAsync(CancellationToken cancellationToken = default);
    Task<CommandResult> SendAsync(string pumpId, PumpCommand command,
        CancellationToken cancellationToken = default);
}
