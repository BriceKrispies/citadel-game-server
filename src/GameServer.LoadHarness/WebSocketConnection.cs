using System.Net.WebSockets;

namespace GameServer.LoadHarness;

public enum ConnectionFrameKind
{
    Binary,
    Closed,
}

/// <summary>An inbound frame: binary payload, or a close signal.</summary>
public readonly record struct ConnectionFrame(ConnectionFrameKind Kind, byte[] Payload)
{
    public static readonly ConnectionFrame Closed = new(ConnectionFrameKind.Closed, Array.Empty<byte>());
}

/// <summary>
/// Binary WebSocket connection seam. Production wraps a real
/// <see cref="ClientWebSocket"/>; tests use an in-memory fake so virtual-client
/// logic is exercised without real sockets.
/// </summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken);

    Task<ConnectionFrame> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

/// <summary>Real binary WebSocket connection over <see cref="ClientWebSocket"/>.</summary>
public sealed class WebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);

    public async Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken) =>
        await _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ConnectionFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        try
        {
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return ConnectionFrame.Closed;
                }

                message.Write(_receiveBuffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return ConnectionFrame.Closed;
        }

        return new ConnectionFrame(ConnectionFrameKind.Binary, message.ToArray());
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client done", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // best-effort
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
