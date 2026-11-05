using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DocProc.Pipeline.Tests.Delivery;

/// <summary>One request the fake receiver got: method, path and query, headers and body.</summary>
internal sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>What the fake receiver answers: status, body, headers.</summary>
internal sealed record ReceiverAnswer(int Status, string Body = "{}", IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// A receiving system on loopback for the delivery tests (docs/11 §22 "handler tests with a fake HTTP server"): an
/// <see cref="HttpListener"/> on a free port of 127.0.0.1 that records every request and answers from a queue of
/// scripted answers (then <see cref="Default"/>), or from <see cref="Respond"/> when set.
/// </summary>
internal sealed class FakeReceiver : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<ReceiverAnswer> _script = new();
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeReceiver()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public string Host => $"127.0.0.1:{Port}";

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public ReceiverAnswer Default { get; set; } = new(201, """{"erpId":"ERP-1"}""");

    public Func<ReceivedRequest, ReceiverAnswer>? Respond { get; set; }

    public IReadOnlyList<ReceivedRequest> Requests => [.. _requests];

    /// <summary>The next answers, in order; once used up, <see cref="Default"/> again.</summary>
    public void Script(params ReceiverAnswer[] answers)
    {
        foreach (var answer in answers)
        {
            _script.Enqueue(answer);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                using var buffer = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(buffer);
                var headers = context.Request.Headers.AllKeys.Where(k => k is not null)
                    .ToDictionary(k => k!, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                var request = new ReceivedRequest(context.Request.HttpMethod, context.Request.Url!.PathAndQuery, headers, buffer.ToArray());
                _requests.Enqueue(request);
                var answer = Respond?.Invoke(request) ?? (_script.TryDequeue(out var next) ? next : Default);
                context.Response.StatusCode = answer.Status;
                context.Response.ContentType = "application/json";
                foreach (var (name, value) in answer.Headers ?? new Dictionary<string, string>())
                {
                    context.Response.Headers[name] = value;
                }

                var body = Encoding.UTF8.GetBytes(answer.Body);
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
            {
                // the client went away
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
