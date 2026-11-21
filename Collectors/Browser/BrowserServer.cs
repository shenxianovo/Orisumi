using System.Net;
using System.Net.WebSockets;
using Microsoft.Data.Sqlite;
using Orisumi.Platform;

namespace Orisumi.Collectors.Browser;

internal static class BrowserServer
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(2);

    internal static Task Run(Instance instance, CancellationToken stop)
    {
        var directory = AppContext.BaseDirectory;
        var checkout = Instance.Find(directory) is null ? Instance.FindCheckout(directory) : null;
        return Run(instance, instance.BrowserPort, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(20), stop, checkout);
    }

    internal static async Task Run(Instance instance, int port, TimeSpan snapshotInterval, TimeSpan heartbeatInterval,
        CancellationToken stop, string? checkout = null)
    {
        var extension = ChromeExtension.DirectoryFor(instance, checkout);
        var origin = $"chrome-extension://{ChromeExtension.Id(extension)}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
        try { listener.Start(); }
        catch (HttpListenerException error)
        {
            throw new IOException($"Cannot listen on browser port {port} for instance {instance.Name}: {error.Message}", error);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var connections = new HashSet<Task>();
        Task<HttpListenerContext>? accept = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                accept = listener.GetContextAsync();
                var context = await accept.WaitAsync(stop);
                accept = null;
                connections.RemoveWhere(task => task.IsCompletedSuccessfully);
                connections.Add(Serve(context));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(connections); }
            finally
            {
                listener.Close();
                if (accept is not null)
                {
                    try { (await accept).Response.Abort(); }
                    catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
                }
            }
        }

        async Task Serve(HttpListenerContext context)
        {
            try { await Connect(context, origin, instance.Store.Root, snapshotInterval, heartbeatInterval, lifetime.Token); }
            catch (Exception error) when (error is IOException or HttpListenerException or WebSocketException
                or InvalidOperationException or SqliteException)
            {
                Console.Error.WriteLine($"orisumi: {error}");
                context.Response.Abort();
            }
        }
    }

    private static async Task Connect(HttpListenerContext context, string origin, string store,
        TimeSpan snapshotInterval, TimeSpan heartbeatInterval, CancellationToken stop)
    {
        var request = context.Request;
        if (request.RemoteEndPoint is not { Address: var peer } || !IPAddress.IsLoopback(peer) ||
            request.Headers.GetValues("Origin") is not [var supplied] || supplied != origin)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            context.Response.Close();
            return;
        }
        if (!request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }

        WebSocket socket;
        try { socket = (await context.AcceptWebSocketAsync(subProtocol: null)).WebSocket; }
        catch (Exception error) when (error is HttpListenerException or WebSocketException or IOException or ArgumentException)
        {
            context.Response.Abort();
            return;
        }

        var buffer = new byte[16 * 1024];
        Task<ReadOnlyMemory<byte>?>? pending = null;
        try
        {
            await BrowserConnection.Run(store, () => pending = Receive(socket, buffer), Send, snapshotInterval, heartbeatInterval, stop);
        }
        finally
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    // Keep the receive pending until the close frame is sent. Cancelling it aborts the socket.
                    using var close = new CancellationTokenSource(SocketTimeout);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token);
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException) { }
            finally
            {
                socket.Dispose();
                if (pending is not null)
                {
                    try { await pending; }
                    catch (Exception error) when (error is WebSocketException or IOException or InvalidDataException
                        or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        }

        async Task Send(ReadOnlyMemory<byte> bytes)
        {
            using var timeout = new CancellationTokenSource(SocketTimeout);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
        }
    }

    private static async Task<ReadOnlyMemory<byte>?> Receive(WebSocket socket, byte[] buffer)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("expected a WebSocket text message");
            if (message.Length + frame.Count > MaxMessageBytes) throw new InvalidDataException("WebSocket message exceeds 64 MiB");
            message.Write(buffer.AsSpan(0, frame.Count));
            if (frame.EndOfMessage) return message.GetBuffer().AsMemory(0, (int)message.Length);
        }
    }
}
