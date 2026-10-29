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

public enum PlcRunResult { AlreadyRunning, AcknowledgedRunIndicated, AcknowledgedModeUnknown }

/// <summary>A PLC session with a bench-specific CPU RUN operation, separate from pump control.</summary>
public sealed class PlcDiagnosticSession : IDisposable
{
    private readonly TcpClient _client;
    private readonly Socket _socket;
    private readonly SemaphoreSlim _operation = new(1, 1);
    internal PlcDiagnosticSession(TcpClient client) { _client = client; _socket = client.Client; }

    // Poll without reading so command replies have exactly one consumer.
    public async Task WaitForDisconnectAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    // The mode indication was observed on a specific SR30 bench, not documented by Siemens.
    // Fail closed on any unrecognized frame or value. Never retry a mode change.
    public async Task<PlcRunResult> RequestCpuRunAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var token = timeout.Token;
            var mode = await ReadModeAsync(2, token).ConfigureAwait(false);
            if (mode == 1) return PlcRunResult.AlreadyRunning;
            var request = Convert.FromHexString("0300002502F080320100000C000014000028000000000000FD000009505F50524F4752414D");
            var stream = _client.GetStream();
            if (_socket.Available != 0) throw new InvalidDataException("Unexpected pending PLC data.");
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            var ack = await PlcConnectionDiagnostic.ReadPacketAsync(stream, token).ConfigureAwait(false);
            if (!ack.SequenceEqual(Convert.FromHexString("0300001402F080320300000C0000010000000028")))
                throw new InvalidDataException("Unrecognized CPU RUN acknowledgement; outcome unknown.");
            var after = await ReadModeAsync(3, token).ConfigureAwait(false);
            return after == 1 ? PlcRunResult.AcknowledgedRunIndicated : PlcRunResult.AcknowledgedModeUnknown;
        }
        catch { _client.Dispose(); throw; }
        finally { _operation.Release(); }
    }

    private async Task<byte> ReadModeAsync(byte reference, CancellationToken token)
    {
        var request = Convert.FromHexString("0300001F02F080320100000002000E00000401120A10020001000003000F18");
        request[12] = reference;
        var stream = _client.GetStream();
        if (_socket.Available != 0) throw new InvalidDataException("Unexpected pending PLC data.");
        await stream.WriteAsync(request, token).ConfigureAwait(false);
        var reply = await PlcConnectionDiagnostic.ReadPacketAsync(stream, token).ConfigureAwait(false);
        var expected = Convert.FromHexString("0300001A02F0803203000000020002000500000401FF04000800");
        expected[12] = reference;
        if (reply.Length != expected.Length || !reply.AsSpan(0, 25).SequenceEqual(expected.AsSpan(0, 25)) || reply[25] > 1)
            throw new InvalidDataException("Unrecognized CPU mode indication.");
        return reply[25];
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// S7-200 SMART connection diagnostic, not an IPumpClient driver.
/// Connection setup itself sends only COTP and S7 Setup Communication. The returned
/// session separately exposes an operator-requested, bench-specific CPU RUN operation.
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

    internal static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken token)
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
