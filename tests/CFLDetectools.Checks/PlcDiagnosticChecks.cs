using System.Net;
using System.Net.Sockets;
using CFLDetectools.Core;

internal static class PlcDiagnosticChecks
{
    // Protocol fixtures: COTP CC and S7 Ack_Data for Setup Communication (PDU 240).
    private static readonly byte[] TransportConfirm = Convert.FromHexString("0300001611D00001000100C1020100C2020300C0010A");
    private static readonly byte[] SessionConfirm = Convert.FromHexString("0300001B02F080320300000001000800000000F0000001000100F0");

    public static async Task RunAsync(Action<bool, string> check)
    {
        check(PlcConnectionDiagnostic.TryParseAddress(" 192.168.10.20 ", out _), "Diagnostic accepts a unicast IPv4 endpoint");
        foreach (var text in new[] { "", "plc.local", "192.168.1", "192.168.1.999", "0.0.0.0", "255.255.255.255", "224.0.0.1", "::1" })
            check(!PlcConnectionDiagnostic.TryParseAddress(text, out _), $"Diagnostic rejects invalid endpoint '{text}'");

        var success = await ExchangeAsync(async (stream, token) =>
        {
            await ExpectRequestAsync(stream, "0300001611E00000000100C1020100C2020300C0010A", token);
            await FragmentAsync(stream, TransportConfirm, token);
            await ExpectRequestAsync(stream, "0300001902F08032010000000100080000F0000001000101E0", token);
            await FragmentAsync(stream, SessionConfirm, token);
            var extra = new byte[1];
            check(await stream.ReadAsync(extra, token) == 0, "Diagnostic closes the connection without variable reads or writes");
        });
        check(success.SessionConfirmed && success.TcpReached && success.IsoConfirmed && success.PduLength == 240,
            "Fragmented transport and S7 replies verify an actual session");
        check(success.CheckedAt.Offset == TimeSpan.Zero && success.Elapsed >= TimeSpan.Zero,
            "Diagnostic results include a UTC timestamp and elapsed duration");

        await WithServerAsync(async (stream, token) =>
        {
            await ExpectRequestAsync(stream, "0300001611E00000000100C1020100C2020300C0010A", token);
            await stream.WriteAsync(TransportConfirm, token);
            await ExpectRequestAsync(stream, "0300001902F08032010000000100080000F0000001000101E0", token);
            await stream.WriteAsync(SessionConfirm, token);
            check(await stream.ReadAsync(new byte[1], token) == 0, "Persistent diagnostic sends no variable operations before disconnect");
        }, async port =>
        {
            var connection = await PlcConnectionDiagnostic.ConnectAsync(IPAddress.Loopback, port: port);
            check(connection.Result.SessionConfirmed && connection.Session is not null, "Persistent diagnostic returns an open session");
            using var session = connection.Session!;
            var watch = session.WaitForDisconnectAsync();
            await Task.Delay(100);
            check(!watch.IsCompleted, "Persistent diagnostic remains open after S7 negotiation");
            session.Dispose();
            try { await watch; }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        });

        await WithServerAsync(async (stream, token) =>
        {
            await ReadRequestAsync(stream, 22, token);
            await stream.WriteAsync(TransportConfirm, token);
            await ReadRequestAsync(stream, 25, token);
            await stream.WriteAsync(SessionConfirm, token);
            await Task.Delay(100, token);
        }, async port =>
        {
            var connection = await PlcConnectionDiagnostic.ConnectAsync(IPAddress.Loopback, port: port);
            using var session = connection.Session ?? throw new InvalidOperationException("Session was not established.");
            await session.WaitForDisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            check(true, "Persistent diagnostic detects an orderly peer disconnect");
        });

        var invalidTransport = await ExchangeAsync(async (stream, token) =>
        {
            await ReadRequestAsync(stream, 22, token);
            var invalid = (byte[])TransportConfirm.Clone();
            invalid[7] = 2;
            await stream.WriteAsync(invalid, token);
        });
        check(invalidTransport.TcpReached && !invalidTransport.IsoConfirmed && !invalidTransport.SessionConfirmed
            && invalidTransport.Failure == PlcDiagnosticFailure.InvalidResponse,
            "An open TCP socket with an unrelated transport reply is not an S7 connection");

        var oversized = await ExchangeAsync(async (stream, token) =>
        {
            await ReadRequestAsync(stream, 22, token);
            await stream.WriteAsync(new byte[] { 3, 0, 255, 255 }, token);
        });
        check(oversized.Failure == PlcDiagnosticFailure.InvalidResponse, "Oversized packet lengths are rejected before allocating a payload");

        foreach (var variant in new[] { "S7 rejected", "Wrong reference", "Wrong parameter length", "Invalid PDU size", "Wrong function" })
        {
            var result = await ExchangeAsync(async (stream, token) =>
            {
                await ReadRequestAsync(stream, 22, token);
                await stream.WriteAsync(TransportConfirm, token);
                await ReadRequestAsync(stream, 25, token);
                var reply = (byte[])SessionConfirm.Clone();
                switch (variant)
                {
                    case "S7 rejected": reply[17] = 0x81; break;
                    case "Wrong reference": reply[12] = 2; break;
                    case "Wrong parameter length": reply[14] = 7; break;
                    case "Invalid PDU size": reply[25] = 0; reply[26] = 0; break;
                    case "Wrong function": reply[19] = 4; break;
                }
                await stream.WriteAsync(reply, token);
            });
            check(result.IsoConfirmed && !result.SessionConfirmed && result.Stage == PlcDiagnosticStage.S7Session
                && result.Failure == (variant == "S7 rejected" ? PlcDiagnosticFailure.Rejected : PlcDiagnosticFailure.InvalidResponse),
                $"S7 diagnostic rejects: {variant}");
        }

        var closed = await ExchangeAsync(async (stream, token) => await ReadRequestAsync(stream, 22, token));
        check(closed.Failure == PlcDiagnosticFailure.PeerClosed, "Premature EOF is reported without claiming a connection");
        var silent = await ExchangeAsync(async (stream, token) =>
        {
            await ReadRequestAsync(stream, 22, token);
            await Task.Delay(250, token);
        }, TimeSpan.FromMilliseconds(100));
        check(silent.Failure == PlcDiagnosticFailure.Timeout && silent.Stage == PlcDiagnosticStage.IsoTransport,
            "A silent peer times out at the correct diagnostic stage");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await PlcConnectionDiagnostic.TestAsync(IPAddress.Loopback, cancelled.Token);
            check(false, "Cancelled diagnostic must not connect");
        }
        catch (OperationCanceledException) { check(true, "Cancelled diagnostic is rejected before connecting"); }

        using var cancellation = new CancellationTokenSource();
        await WithServerAsync(async (stream, token) =>
        {
            await ReadRequestAsync(stream, 22, token);
            cancellation.Cancel();
            await Task.Delay(100, token);
        }, async port =>
        {
            try
            {
                await PlcConnectionDiagnostic.TestAsync(IPAddress.Loopback, cancellation.Token, port);
                check(false, "In-flight cancellation must stop the diagnostic");
            }
            catch (OperationCanceledException) { check(true, "In-flight cancellation interrupts a pending reply"); }
        });
    }

    private static async Task<PlcDiagnosticResult> ExchangeAsync(Func<NetworkStream, CancellationToken, Task> handler, TimeSpan? timeout = null)
    {
        PlcDiagnosticResult? result = null;
        await WithServerAsync(handler, async port => result = await PlcConnectionDiagnostic.TestAsync(IPAddress.Loopback,
            port: port, timeout: timeout));
        return result!;
    }

    private static async Task WithServerAsync(Func<NetworkStream, CancellationToken, Task> handler, Func<int, Task> run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = ServeAsync();
        await run(((IPEndPoint)listener.LocalEndpoint).Port);
        await serve;

        async Task ServeAsync()
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await handler(peer.GetStream(), deadline.Token);
        }
    }

    private static async Task ReadRequestAsync(NetworkStream stream, int size, CancellationToken token)
        => await stream.ReadExactlyAsync(new byte[size], token);

    private static async Task ExpectRequestAsync(NetworkStream stream, string expected, CancellationToken token)
    {
        var bytes = Convert.FromHexString(expected);
        var actual = new byte[bytes.Length];
        await stream.ReadExactlyAsync(actual, token);
        if (!actual.SequenceEqual(bytes)) throw new InvalidOperationException("Unexpected diagnostic request; only connection setup is allowed.");
    }

    private static async Task FragmentAsync(NetworkStream stream, byte[] packet, CancellationToken token)
    {
        foreach (var value in packet) await stream.WriteAsync(new[] { value }, token);
    }
}
