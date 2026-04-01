using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WebSocketSharp;
using Xunit;

namespace WebSocketSharp.Tests;

public sealed class WebSocketClientTests
{
    [Fact]
    public void Connect_Send_IsAlive_And_Close_WorkThroughNativePoller()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer();
        using WebSocket socket = new WebSocket(server.Url.ToString());
        ManualResetEvent opened = new ManualResetEvent(false);
        ManualResetEvent closed = new ManualResetEvent(false);
        ManualResetEvent messageReceived = new ManualResetEvent(false);
        List<string> errors = new List<string>();
        string messageText = null;
        string closeReason = null;

        socket.OnOpen += delegate { opened.Set(); };
        socket.OnMessage += delegate(object sender, MessageEventArgs e)
        {
            messageText = e.Data;
            messageReceived.Set();
        };
        socket.OnError += delegate(object sender, ErrorEventArgs e)
        {
            lock (errors)
            {
                errors.Add(e.Message);
            }
        };
        socket.OnClose += delegate(object sender, CloseEventArgs e)
        {
            closeReason = e.Reason;
            closed.Set();
        };

        socket.Connect();

        Assert.True(opened.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal(WebSocketState.Open, socket.ReadyState);

        socket.Send("hello");

        Assert.True(messageReceived.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal("hello", messageText);
        Assert.True(socket.IsAlive);

        socket.Close();

        Assert.True(closed.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal(WebSocketState.Closed, socket.ReadyState);
        Assert.Contains(closeReason ?? string.Empty, new[] { string.Empty, "bye" });

        lock (errors)
        {
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void SecureSocket_ExposesSslConfiguration_AsCompatibilitySurface()
    {
        using WebSocket socket = new WebSocket("wss://example.com/ws");
        Assert.True(socket.IsSecure);
        Assert.NotNull(socket.SslConfiguration);
    }

    [Fact]
    public void ConstructorValidationFailures_DoNotCrashPendingFinalizers()
    {
        Assert.Throws<ArgumentNullException>(() => new WebSocket(null));
        Assert.Throws<ArgumentException>(() => new WebSocket(string.Empty));
        Assert.Throws<ArgumentException>(() => new WebSocket("http://example.com/ws"));
        Assert.Throws<ArgumentException>(() => new WebSocket("ws://example.com/ws", "bad protocol"));

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public void Dispose_BeforeConnect_DoesNotThrow()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer();
        WebSocket socket = new WebSocket(server.Url.ToString());

        socket.Dispose();
        socket.Dispose();

        Assert.Equal(WebSocketState.Closed, socket.ReadyState);
    }

    [Fact]
    public void CloseAsync_RaisesOnClose()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer();
        using WebSocket socket = new WebSocket(server.Url.ToString());
        ManualResetEvent closed = new ManualResetEvent(false);

        socket.OnClose += delegate { closed.Set(); };
        socket.Connect();
        socket.CloseAsync();

        Assert.True(closed.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.Equal(WebSocketState.Closed, socket.ReadyState);
    }

    [Fact]
    public void Close_WhenServerIgnoresClose_CompletesAfterWaitTimeWithUncleanClose()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer(ignoreClose: true);
        using WebSocket socket = new WebSocket(server.Url.ToString());
        ManualResetEvent closed = new ManualResetEvent(false);
        bool wasClean = true;

        socket.WaitTime = TimeSpan.FromMilliseconds(150);
        socket.OnClose += delegate(object sender, CloseEventArgs e)
        {
            wasClean = e.WasClean;
            closed.Set();
        };
        socket.Connect();

        Stopwatch stopwatch = Stopwatch.StartNew();
        socket.Close();
        stopwatch.Stop();

        Assert.True(closed.WaitOne(TimeSpan.FromSeconds(2)));
        Assert.False(wasClean);
        Assert.Equal(WebSocketState.Closed, socket.ReadyState);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void OnMessage_CanCallIsAlive_WithoutBlockingNativePoller()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer(initialMessage: "hello");
        using WebSocket socket = new WebSocket(server.Url.ToString());
        ManualResetEvent messageHandled = new ManualResetEvent(false);
        List<string> errors = new List<string>();
        bool aliveDuringMessage = false;

        socket.OnMessage += delegate
        {
            aliveDuringMessage = socket.IsAlive;
            messageHandled.Set();
        };
        socket.OnError += delegate(object sender, ErrorEventArgs e)
        {
            lock (errors)
            {
                errors.Add(e.Message);
            }
        };

        socket.Connect();

        Assert.True(messageHandled.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.True(aliveDuringMessage);
        Assert.Equal(WebSocketState.Open, socket.ReadyState);

        lock (errors)
        {
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void OnOpen_CanCallIsAlive_AndCompletesBeforeOnMessage()
    {
        using SimpleWebSocketServer server = new SimpleWebSocketServer(initialMessage: "hello");
        using WebSocket socket = new WebSocket(server.Url.ToString());
        ManualResetEvent messageHandled = new ManualResetEvent(false);
        List<string> errors = new List<string>();
        bool aliveDuringOpen = false;
        bool openCompleted = false;
        bool messageSawOpenCompleted = false;

        socket.OnOpen += delegate
        {
            aliveDuringOpen = socket.IsAlive;
            openCompleted = true;
        };
        socket.OnMessage += delegate
        {
            messageSawOpenCompleted = openCompleted;
            messageHandled.Set();
        };
        socket.OnError += delegate(object sender, ErrorEventArgs e)
        {
            lock (errors)
            {
                errors.Add(e.Message);
            }
        };

        socket.Connect();

        Assert.True(aliveDuringOpen);
        Assert.True(messageHandled.WaitOne(TimeSpan.FromSeconds(5)));
        Assert.True(messageSawOpenCompleted);
        Assert.Equal(WebSocketState.Open, socket.ReadyState);

        lock (errors)
        {
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void FailedConnect_DoesNotWaitForOpenDispatcher()
    {
        using PlainHttpFailureServer server = new PlainHttpFailureServer();
        using WebSocket socket = new WebSocket(string.Format("wss://127.0.0.1:{0}/ws", server.Url.Port));
        ManualResetEvent errorReceived = new ManualResetEvent(false);

        socket.OnError += delegate
        {
            errorReceived.Set();
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        socket.Connect();
        stopwatch.Stop();

        Assert.True(errorReceived.WaitOne(TimeSpan.FromSeconds(2)));
        Assert.Equal(WebSocketState.Closed, socket.ReadyState);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private sealed class PlainHttpFailureServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        public Uri Url { get; }

        public PlainHttpFailureServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            IPEndPoint endPoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = new Uri(string.Format("ws://127.0.0.1:{0}/ws", endPoint.Port));
            _serverTask = Task.Run(runAsync);
        }

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task runAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, 0, response.Length);
                await stream.FlushAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class SimpleWebSocketServer : IDisposable
    {
        private const string GuidValue = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly CancellationTokenSource _cancellation;
        private readonly bool _ignoreClose;
        private readonly string _initialMessage;
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        public Uri Url { get; }

        public SimpleWebSocketServer(bool ignoreClose = false, string initialMessage = null)
        {
            _cancellation = new CancellationTokenSource();
            _ignoreClose = ignoreClose;
            _initialMessage = initialMessage;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            IPEndPoint endPoint = (IPEndPoint)_listener.LocalEndpoint;
            Url = new Uri(string.Format("ws://127.0.0.1:{0}/ws", endPoint.Port));
            _serverTask = Task.Run(runAsync);
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _serverTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            _cancellation.Dispose();
        }

        private async Task runAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();

                string request = await ReadHttpRequestAsync(stream, _cancellation.Token);
                string key = GetHeader(request, "Sec-WebSocket-Key");
                await WriteHandshakeAsync(stream, key, _cancellation.Token);
                if (_initialMessage != null)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(_initialMessage);
                    await WriteFrameAsync(stream, 1, payload, _cancellation.Token);
                }

                while (!_cancellation.IsCancellationRequested)
                {
                    WebSocketFrame frame = await ReadFrameAsync(stream, _cancellation.Token);
                    switch (frame.Opcode)
                    {
                        case 1:
                            await WriteFrameAsync(stream, 1, frame.Payload, _cancellation.Token);
                            break;
                        case 8:
                            if (_ignoreClose)
                            {
                                break;
                            }

                            await WriteFrameAsync(stream, 8, BuildClosePayload(1000, "bye"), _cancellation.Token);
                            return;
                        case 9:
                            await WriteFrameAsync(stream, 10, frame.Payload, _cancellation.Token);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static byte[] BuildClosePayload(ushort code, string reason)
        {
            byte[] reasonBytes = Encoding.UTF8.GetBytes(reason);
            byte[] payload = new byte[2 + reasonBytes.Length];
            payload[0] = (byte)(code >> 8);
            payload[1] = (byte)(code & 0xff);
            Buffer.BlockCopy(reasonBytes, 0, payload, 2, reasonBytes.Length);
            return payload;
        }

        private static string GetHeader(string request, string name)
        {
            string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string prefix = name + ":";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }

            throw new InvalidOperationException("Required websocket header was missing.");
        }

        private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] chunk = new byte[256];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("The websocket client closed during the HTTP handshake.");
                    }

                    buffer.Write(chunk, 0, read);
                    if (buffer.Length >= 4)
                    {
                        byte[] bytes = buffer.ToArray();
                        int end = FindHeaderTerminator(bytes);
                        if (end >= 0)
                        {
                            return Encoding.ASCII.GetString(bytes, 0, end);
                        }
                    }
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private static int FindHeaderTerminator(byte[] bytes)
        {
            for (int i = 0; i <= bytes.Length - 4; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                {
                    return i;
                }
            }

            return -1;
        }

        private static async Task<WebSocketFrame> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] header = await ReadExactAsync(stream, 2, cancellationToken);
            bool masked = (header[1] & 0x80) != 0;
            ulong payloadLength = (ulong)(header[1] & 0x7f);
            if (payloadLength == 126)
            {
                byte[] lengthBytes = await ReadExactAsync(stream, 2, cancellationToken);
                payloadLength = (ulong)((lengthBytes[0] << 8) | lengthBytes[1]);
            }
            else if (payloadLength == 127)
            {
                byte[] lengthBytes = await ReadExactAsync(stream, 8, cancellationToken);
                payloadLength = 0;
                for (int i = 0; i < lengthBytes.Length; i++)
                {
                    payloadLength = (payloadLength << 8) | lengthBytes[i];
                }
            }

            byte[] mask = masked ? await ReadExactAsync(stream, 4, cancellationToken) : new byte[0];
            byte[] payload = payloadLength == 0 ? new byte[0] : await ReadExactAsync(stream, checked((int)payloadLength), cancellationToken);
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(payload[i] ^ mask[i % 4]);
                }
            }

            return new WebSocketFrame
            {
                Opcode = (byte)(header[0] & 0x0f),
                Payload = payload
            };
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The websocket connection closed unexpectedly.");
                }

                offset += read;
            }

            return buffer;
        }

        private static async Task WriteFrameAsync(NetworkStream stream, byte opcode, byte[] payload, CancellationToken cancellationToken)
        {
            payload = payload ?? new byte[0];
            MemoryStream frame = new MemoryStream();
            try
            {
                frame.WriteByte((byte)(0x80 | opcode));
                if (payload.Length < 126)
                {
                    frame.WriteByte((byte)payload.Length);
                }
                else
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)(payload.Length >> 8));
                    frame.WriteByte((byte)(payload.Length & 0xff));
                }

                frame.Write(payload, 0, payload.Length);
                byte[] bytes = frame.ToArray();
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                frame.Dispose();
            }
        }

        private static async Task WriteHandshakeAsync(NetworkStream stream, string key, CancellationToken cancellationToken)
        {
            string accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.ASCII.GetBytes(key + GuidValue)));
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private sealed class WebSocketFrame
        {
            public byte Opcode { get; set; }

            public byte[] Payload { get; set; }
        }
    }
}
