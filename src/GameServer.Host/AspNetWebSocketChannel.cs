using System.Net.WebSockets;
using System.Text;
using GameServer.Transport;

namespace GameServer.Host;

/// <summary>
/// Wraps an ASP.NET Core <see cref="WebSocket"/> as an <see cref="IWebSocketChannel"/>
/// of UTF-8 text frames. Sends are serialized (the tick fan-out and a connection's
/// own replies can race), and a closed/aborted socket surfaces as a null receive or
/// a throwing send so the kernel can drop the connection.
/// </summary>
public sealed class AspNetWebSocketChannel : IWebSocketChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[8 * 1024];

    public AspNetWebSocketChannel(WebSocket socket) => _socket = socket;

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(_receiveBuffer), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(_receiveBuffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            return null; // treat an aborted/broken socket as a clean close
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
