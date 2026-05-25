using System.Net.Http.Json;
using System.Net.WebSockets;
using GameServer.Protocol.Realtime;
using GameServer.Protocol.Realtime.V1;
using GameServer.Transport;

namespace GameServer.SecurityTests;

/// <summary>
/// What the server did in response to a handshake/command sequence: it rejected (a typed
/// ServerError), granted access (a ServerSnapshot for the room), or closed the connection. For a
/// security assertion the decisive fact is <see cref="AccessWasGranted"/> — a rejection's exact
/// wire code is secondary (see FINDINGS.md on the Unauthorized→InternalServerError mapping gap).
/// </summary>
public readonly record struct WireOutcome(bool WasRejected, bool AccessWasGranted, ErrorCode? ErrorCode)
{
    public static readonly WireOutcome Closed = new(false, false, null);
    public static readonly WireOutcome AccessGranted = new(false, true, null);
    public static WireOutcome Rejected(ErrorCode code) => new(true, false, code);
}

/// <summary>
/// A black-box realtime client: connects to the container's <c>/realtime/v1/connect</c> WebSocket
/// and exchanges canonical protobuf frames using the REAL <see cref="RealtimeProtobufCodec"/>. It
/// also speaks raw bytes (for the fuzz/oversize cases). The kernel's own
/// <see cref="RealtimeEnvelopeMapper"/> is intentionally NOT used here — we hand-build wire
/// envelopes so a test can lie about identity/version and observe how the server reacts.
/// </summary>
public sealed class RealtimeWireClient : IAsyncDisposable
{
    private static readonly RealtimeProtobufCodec Codec = new();

    private readonly ClientWebSocket _socket;

    private RealtimeWireClient(ClientWebSocket socket) => _socket = socket;

    public WebSocketState State => _socket.State;

    /// <summary>
    /// Mints a real join token for <paramref name="roomId"/>/<paramref name="playerId"/> via the
    /// control plane (using <paramref name="apiKey"/>), then opens the realtime WebSocket carrying
    /// that token. The room must already exist (create it via <see cref="ControlPlane"/> first).
    /// </summary>
    public static async Task<RealtimeWireClient> ConnectWithMintedTokenAsync(
        SecurityTarget target, string apiKey, string roomId, string playerId, CancellationToken ct)
    {
        var token = await ControlPlane.MintJoinTokenAsync(target, apiKey, roomId, playerId, ct);
        return await ConnectWithTokenAsync(target, token, ct);
    }

    /// <summary>Opens the realtime WebSocket carrying an arbitrary (possibly forged) join token.</summary>
    public static async Task<RealtimeWireClient> ConnectWithTokenAsync(SecurityTarget target, string joinToken, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        var uri = target.WebSocketUri($"/realtime/v1/connect?joinToken={Uri.EscapeDataString(joinToken)}");
        await socket.ConnectAsync(uri, ct);
        return new RealtimeWireClient(socket);
    }

    /// <summary>Sends a fully-formed wire envelope as a binary frame.</summary>
    public Task SendAsync(RealtimeEnvelope envelope, CancellationToken ct) =>
        SendRawBinaryAsync(Codec.Encode(envelope), ct);

    /// <summary>Sends arbitrary bytes as a binary frame (for malformed/oversize fuzzing).</summary>
    public async Task SendRawBinaryAsync(byte[] payload, CancellationToken ct) =>
        await _socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);

    /// <summary>Sends a UTF-8 text frame on the binary-only channel (protocol violation).</summary>
    public async Task SendTextAsync(string text, CancellationToken ct) =>
        await _socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>
    /// Receives the next binary frame and decodes it with the real codec. Returns null if the
    /// connection closed first. Throws on timeout so a hung test fails fast instead of hanging.
    /// </summary>
    public async Task<RealtimeEnvelope?> ReceiveEnvelopeAsync(CancellationToken ct)
    {
        var raw = await ReceiveRawAsync(ct);
        return raw is null ? null : Codec.Decode(raw);
    }

    /// <summary>Receives the next message's raw bytes, or null if the socket closed.</summary>
    public async Task<byte[]?> ReceiveRawAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(chunk, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
                // The server dropped us: a clean close frame, an abortive RST (oversize-frame reap),
                // or our own read-deadline elapsing. From the client's point of view the connection
                // is no longer readable — model all three as "closed" (return null) so a caller can
                // distinguish a drop from a ServerError frame without hanging or faulting.
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage)
            {
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// Drains frames until a <see cref="ServerError"/> arrives (returns its code), or the
    /// connection closes (returns null). Bounded by <paramref name="ct"/> so it cannot hang.
    /// </summary>
    public async Task<ErrorCode?> ReadUntilServerErrorOrCloseAsync(CancellationToken ct)
    {
        var outcome = await ReadUntilRejectedSnapshotOrCloseAsync(ct);
        return outcome.ErrorCode;
    }

    /// <summary>
    /// Drains frames until the server REJECTS (a ServerError), GRANTS ACCESS (a ServerSnapshot for
    /// the room), or CLOSES the connection. The decisive security signal is whether access was ever
    /// granted; the ServerError code (if any) is reported too. Bounded by <paramref name="ct"/>.
    /// </summary>
    public async Task<WireOutcome> ReadUntilRejectedSnapshotOrCloseAsync(CancellationToken ct)
    {
        while (true)
        {
            RealtimeEnvelope? env;
            try
            {
                env = await ReceiveEnvelopeAsync(ct);
            }
            catch (RealtimeProtocolException)
            {
                // A frame we could not decode is not a ServerError; keep reading.
                continue;
            }

            if (env is null)
            {
                return WireOutcome.Closed; // closed without a ServerError or a snapshot
            }

            switch (env.PayloadCase)
            {
                case RealtimeEnvelope.PayloadOneofCase.ServerError:
                    return WireOutcome.Rejected(env.ServerError.Code);
                case RealtimeEnvelope.PayloadOneofCase.ServerSnapshot:
                    return WireOutcome.AccessGranted;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            _socket.Dispose();
        }
    }
}
