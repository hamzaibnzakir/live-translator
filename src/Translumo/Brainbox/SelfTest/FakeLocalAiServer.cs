using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Brainbox.Desktop.SelfTest
{
    /// <summary>
    /// A tiny OpenAI-compatible server (what LM Studio/Ollama expose) on 127.0.0.1 for the self-test.
    /// Implemented on a raw TcpListener so it needs no URL ACL / admin rights. It can be stopped and
    /// restarted to simulate the user closing LM Studio.
    /// </summary>
    public sealed class FakeLocalAiServer : IDisposable
    {
        private readonly Dictionary<string, string> _dictionary;
        private TcpListener _listener;
        private CancellationTokenSource _cts;

        public FakeLocalAiServer(Dictionary<string, string> dictionary)
        {
            _dictionary = dictionary;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        public int Port { get; }
        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";
        public bool Running => _listener != null;
        public ConcurrentQueue<string> TranslatedSegments { get; } = new();
        public int ChatRequests => _chatRequests;
        public int ModelRequests => _modelRequests;
        private int _chatRequests;
        private int _modelRequests;

        public void Start()
        {
            if (_listener != null) return;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            var ct = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(ct);
                    }
                    catch
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleAsync(client, ct));
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }

        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var (path, body) = await ReadRequestAsync(stream, ct);
                    string json;
                    if (path.EndsWith("/models", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _modelRequests);
                        json = "{\"object\":\"list\",\"data\":[{\"id\":\"brainbox-selftest-model\",\"object\":\"model\"}]}";
                    }
                    else
                    {
                        Interlocked.Increment(ref _chatRequests);
                        json = Chat(body);
                    }

                    var bytes = Encoding.UTF8.GetBytes(json);
                    var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
                    await stream.WriteAsync(bytes, ct);
                }
                catch
                {
                    // test server: ignore broken connections
                }
            }
        }

        private string Chat(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
            var segments = new List<string>();
            var inSegments = false;
            foreach (var line in user.Split('\n'))
            {
                if (line.StartsWith("SEGMENTS", StringComparison.Ordinal))
                {
                    inSegments = true;
                    continue;
                }

                var m = Regex.Match(line, @"^\s*\d+\.\s(.*)$");
                if (inSegments && m.Success) segments.Add(m.Groups[1].Value.Trim());
            }

            var outputs = segments.Select(s =>
            {
                TranslatedSegments.Enqueue(s);
                return _dictionary.TryGetValue(s, out var t) ? t : "[EN] " + s;
            }).ToList();
            var content = JsonSerializer.Serialize(new { translations = outputs });
            return JsonSerializer.Serialize(new
            {
                id = "chatcmpl-selftest",
                @object = "chat.completion",
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
            });
        }

        private static async Task<(string Path, string Body)> ReadRequestAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int headerEnd = -1, contentLength = 0;
            while (true)
            {
                var n = await stream.ReadAsync(chunk, ct);
                if (n <= 0) break;
                buffer.Write(chunk, 0, n);
                var data = buffer.GetBuffer();
                if (headerEnd < 0)
                {
                    for (var i = 3; i < buffer.Length; i++)
                    {
                        if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
                        {
                            headerEnd = i + 1;
                            break;
                        }
                    }

                    if (headerEnd >= 0)
                    {
                        var headers = Encoding.ASCII.GetString(data, 0, headerEnd);
                        var m = Regex.Match(headers, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (m.Success) contentLength = int.Parse(m.Groups[1].Value);
                    }
                }

                if (headerEnd >= 0 && buffer.Length >= headerEnd + contentLength) break;
            }

            var all = buffer.ToArray();
            var head = Encoding.ASCII.GetString(all, 0, Math.Max(0, headerEnd));
            var path = head.Split(' ').Skip(1).FirstOrDefault() ?? "/";
            var body = headerEnd >= 0 ? Encoding.UTF8.GetString(all, headerEnd, all.Length - headerEnd) : "";
            return (path, body);
        }

        public void Dispose() => Stop();
    }
}
