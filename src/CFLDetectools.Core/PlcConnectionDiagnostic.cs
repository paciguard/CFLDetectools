using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CFLDetectools.Core;

public enum PlcDiagnosticStage { Tcp, IsoTransport, S7Session, Complete }
public enum PlcDiagnosticFailure { None, Timeout, Network, PeerClosed, InvalidResponse, Rejected }

public sealed record PlcDiagnosticResult(PlcDiagnosticStage Stage, PlcDiagnosticFailure Failure,
    DateTimeOffset CheckedAt, TimeSpan Elapsed, ushort? PduLength = null)
{
    public bool TcpReached => Stage > PlcDiagnosticStage.Tcp;
    public bool IsoConfirmed => Stage > PlcDiagnosticStage.IsoTransport;
    public bool SessionConfirmed => Stage == PlcDiagnosticStage.Complete;
}

public sealed record PlcDiagnosticConnection(PlcDiagnosticResult Result, PlcDiagnosticSession? Session);

/// <summary>A diagnostic-only session. It has no operations for reading or writing PLC variables.</summary>
public sealed class PlcDiagnosticSession : IDisposable
{
    private readonly TcpClient _client;
    internal PlcDiagnosticSession(TcpClient client) => _client = client;

    // A zero-length read means orderly remote close. Unexpected application data is
    // also treated as a lost session because this diagnostic never requests it.
    // TCP keepalive detects a broken transport, but does not verify S7 data access.
    public async Task WaitForDisconnectAsync(CancellationToken cancellationToken = default)
        => _ = await _client.GetStream().ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false);

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// S7-200 SMART connection diagnostic, not an IPumpClient driver.
/// Only COTP connection and S7 Setup Communication are sent. No variable access,
/// equipment commands, program transfer, clock writes or CPU mode changes exist here.
/// TestAsync closes its session; ConnectAsync transfers the live session to its caller.
/// </summary>
public static class PlcConnectionDiagnostic
{
    // SMART rack 0 / slot 0: local TSAP 01.00, remote TSAP 03.00.
    // See plc/S7-200-SMART.md for protocol references and the deliberately limited scope.
    private static readonly byte[] TransportRequest =
    [
        0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
        0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x03, 0x00, 0xC0, 0x01, 0x0A
    ];
    private static readonly byte[] SessionRequest =
    [
        0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80,
        0x32, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00,
        0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0
    ];

    public static bool TryParseAddress(string text, out IPAddress address)
    {
        address = IPAddress.None;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || part.Length > 3
            || part.Any(c => c is < '0' or > '9') || !byte.TryParse(part, out _))) return false;
        // Construct decimal octets explicitly: IPAddress.TryParse also accepts legacy octal forms.
        var parsed = new IPAddress(parts.Select(part => byte.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        var bytes = parsed.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224) return false;
        address = parsed;
        return true;
    }

    public static async Task<PlcDiagnosticResult> TestAsync(IPAddress address,
        CancellationToken cancellationToken = default, int port = 102, TimeSpan? timeout = null)
    {
        var connection = await ConnectAsync(address, cancellationToken, port, timeout).ConfigureAwait(false);
        connection.Session?.Dispose();
        return connection.Result;
    }

    public static async Task<PlcDiagnosticConnection> ConnectAsync(IPAddress address,
        CancellationToken cancellationToken = default, int port = 102, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!TryParseAddress(address.ToString(), out _)) throw new ArgumentException("A unicast IPv4 address is required.", nameof(address));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var duration = timeout ?? TimeSpan.FromSeconds(5);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        var handedOff = false;
        var watch = Stopwatch.StartNew();
        var stage = PlcDiagnosticStage.Tcp;
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            stage = PlcDiagnosticStage.IsoTransport;
            var stream = client.GetStream();
            await stream.WriteAsync(TransportRequest, deadline.Token).ConfigureAwait(false);
            var transport = await ReadPacketAsync(stream, deadline.Token).ConfigureAwait(false);
            if (transport.Length < 11 || transport[4] < 6 || transport[4] + 5 != transport.Length
                || transport[5] != 0xD0 || transport[6] != 0 || transport[7] != 1 || transport[10] != 0)
                return Result(PlcDiagnosticFailure.InvalidResponse);

            stage = PlcDiagnosticStage.S7Session;
            await stream.WriteAsync(SessionRequest, deadline.Token).ConfigureAwait(false);
            var reply = await ReadPacketAsync(stream, deadline.Token).ConfigureAwait(false);
            if (reply.Length < 19 || reply[4] != 2 || reply[5] != 0xF0 || reply[6] != 0x80
                || reply[7] != 0x32 || reply[8] != 3 || reply[9] != 0 || reply[10] != 0
                || reply[11] != 0 || reply[12] != 1)
                return Result(PlcDiagnosticFailure.InvalidResponse);
            if (reply[17] != 0 || reply[18] != 0) return Result(PlcDiagnosticFailure.Rejected);
            if (reply.Length != 27 || reply[13] != 0 || reply[14] != 8 || reply[15] != 0 || reply[16] != 0
                || reply[19] != 0xF0 || reply[20] != 0
                || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(21, 2)) != 1
                || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(23, 2)) != 1)
                return Result(PlcDiagnosticFailure.InvalidResponse);
            var pdu = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(25, 2));
            if (pdu is < 240 or > 480) return Result(PlcDiagnosticFailure.InvalidResponse);
            stage = PlcDiagnosticStage.Complete;
            var established = new PlcDiagnosticConnection(new(stage, PlcDiagnosticFailure.None, DateTimeOffset.UtcNow, watch.Elapsed, pdu),
                new PlcDiagnosticSession(client));
            handedOff = true;
            return established;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Result(PlcDiagnosticFailure.Timeout); }
        catch (SocketException) { return Result(PlcDiagnosticFailure.Network); }
        catch (EndOfStreamException) { return Result(PlcDiagnosticFailure.PeerClosed); }
        catch (InvalidDataException) { return Result(PlcDiagnosticFailure.InvalidResponse); }
        catch (IOException) { return Result(PlcDiagnosticFailure.Network); }
        finally { if (!handedOff) client.Dispose(); }

        PlcDiagnosticConnection Result(PlcDiagnosticFailure failure) => new(new(stage, failure, DateTimeOffset.UtcNow, watch.Elapsed), null);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
        // The diagnostic only needs small setup replies. Reject unbounded or malformed frames.
        if (header[0] != 3 || header[1] != 0 || length is < 7 or > 1024) throw new InvalidDataException();
        var packet = new byte[length];
        header.CopyTo(packet, 0);
        await stream.ReadExactlyAsync(packet.AsMemory(4), token).ConfigureAwait(false);
        return packet;
    }
}
