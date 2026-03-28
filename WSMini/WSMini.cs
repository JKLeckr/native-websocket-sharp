using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace wsmini
{
    internal static class Program
    {
        private const string DefaultUrl = "http://127.0.0.1:18765";
        private const string WsPath = "/ws";
        private const string BurstCommandPrefix = "#burst#:";
        private const string BurstMessagePrefix = "Burst:";

        public static async Task Main(string[] args)
        {
            string url = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
                ? args[0]
                : DefaultUrl;

            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls(url);

            var app = builder.Build();
            app.UseWebSockets();

            app.Map(WsPath, async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("websocket upgrade required");
                    return;
                }

                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                string endpoint = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                Console.WriteLine("client connected: " + endpoint);

                byte[] receiveBuffer = new byte[8192];
                using MemoryStream messageBuffer = new MemoryStream();

                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = await socket.ReceiveAsync(
                            new ArraySegment<byte>(receiveBuffer),
                            context.RequestAborted);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "bye",
                                context.RequestAborted);
                            break;
                        }

                        messageBuffer.Write(receiveBuffer, 0, result.Count);
                        if (!result.EndOfMessage)
                        {
                            continue;
                        }

                        byte[] payload = messageBuffer.ToArray();
                        messageBuffer.SetLength(0);

                        if (result.MessageType == WebSocketMessageType.Text &&
                            await TryHandleBurstCommandAsync(socket, payload, context.RequestAborted))
                        {
                            continue;
                        }

                        await socket.SendAsync(
                            new ArraySegment<byte>(payload),
                            result.MessageType,
                            true,
                            context.RequestAborted);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine("websocket error (" + endpoint + "): " + ex.Message);
                }
                finally
                {
                    Console.WriteLine("client disconnected: " + endpoint);
                }
            });

            app.MapGet("/", () => "ok");

            Console.WriteLine("mini websocket server listening on " + url + WsPath);
            Console.WriteLine("press Ctrl+C to stop");
            await app.RunAsync();
        }

        private static async Task<bool> TryHandleBurstCommandAsync(
            WebSocket socket,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            string text = System.Text.Encoding.UTF8.GetString(payload);
            if (!text.StartsWith(BurstCommandPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string countText = text.Substring(BurstCommandPrefix.Length);
            if (!int.TryParse(countText, out int count) || count < 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                byte[] burstPayload = System.Text.Encoding.UTF8.GetBytes(
                    BurstMessagePrefix + i.ToString());
                await socket.SendAsync(
                    new ArraySegment<byte>(burstPayload),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }

            return true;
        }
    }
}
