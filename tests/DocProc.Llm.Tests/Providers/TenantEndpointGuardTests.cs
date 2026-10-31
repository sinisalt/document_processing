using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DocProc.Llm.Anthropic;
using DocProc.Llm.OpenAi;
using DocProc.Llm.Providers;

namespace DocProc.Llm.Tests.Providers;

/// <summary>
/// The SSRF guard of tenant provider configurations (docs/08 "Endpoint restrictions"): the URL check at creation and
/// the connect-time check that also holds when a name resolves to an internal address later (DNS rebinding). The
/// "internal service" here is a TCP listener on loopback that counts the connections it gets.
/// </summary>
public sealed class TenantEndpointGuardTests : IAsyncDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly RecordingListener _internal = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _internal.DisposeAsync();

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.1.2.3")]
    [InlineData("100.100.100.200")] // CGNAT, also Alibaba's metadata address
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")] // Azure's platform virtual IP (DNS, health probes, WireServer), reachable from every VNet
    [InlineData("::ffff:168.63.129.16")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.170")]
    [InlineData("192.168.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")] // IPv4-compatible
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:0001::1")] // 6to4 of 127.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2001:db8::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("fd00:ec2::254")] // AWS's IPv6 metadata address
    public void Internal_and_reserved_addresses_are_blocked(string address) =>
        TenantEndpointGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeTrue();

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("162.159.140.245")]
    [InlineData("168.63.129.15")]
    [InlineData("168.63.129.17")]
    [InlineData("168.63.128.16")]
    [InlineData("::ffff:1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2002:0101:0101::1")] // 6to4 of a public address
    public void Public_addresses_are_allowed(string address) =>
        TenantEndpointGuard.IsBlocked(IPAddress.Parse(address)).ShouldBeFalse();

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://contoso.openai.azure.com", true)]
    [InlineData("https://api.anthropic.com", true)]
    [InlineData("http://api.openai.com/v1", false)]
    [InlineData("https://127.0.0.1/v1", false)]
    [InlineData("https://2130706433/", false)]
    [InlineData("https://017700000001/", false)]
    [InlineData("https://0x7f000001/", false)]
    [InlineData("https://0x7f.1/", false)]
    [InlineData("https://127.1/", false)]
    [InlineData("https://168.63.129.16/", false)]
    [InlineData("https://[::ffff:a83f:8110]/", false)]
    [InlineData("https://[::ffff:7f00:1]/", false)]
    [InlineData("https://LOCALHOST./", false)]
    [InlineData("https://ollama.local/", false)]
    [InlineData("https://metadata.google.internal/", false)]
    [InlineData("https://ollama/", false)]
    [InlineData("https://u:p@api.openai.com/", false)]
    [InlineData("ftp://api.openai.com/", false)]
    [InlineData("not a url", false)]
    public void Endpoints_are_validated_at_creation(string endpoint, bool allowed) =>
        (TenantEndpointGuard.Validate(endpoint) is null).ShouldBe(allowed, TenantEndpointGuard.Validate(endpoint));

    [Fact]
    public async Task A_name_that_resolves_to_an_internal_address_is_refused_at_connect_time()
    {
        // The name passed the creation check; at call time it resolves to the platform's own host (DNS rebinding).
        using var client = TenantEndpointGuard.CreateClient(Resolving("api.rebind.example", IPAddress.Loopback));

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri($"https://api.rebind.example:{_internal.Port}/v1/models"), Ct));

        TenantEndpointGuard.FindBlocked(error).ShouldNotBeNull();
        error.ToString().ShouldNotContain("127.0.0.1", Case.Insensitive, "the tenant does not learn what the name resolved to");
        await _internal.ShouldHaveNoConnectionsAsync();
    }

    [Fact]
    public async Task A_name_with_one_internal_address_among_public_ones_is_refused()
    {
        using var client = TenantEndpointGuard.CreateClient(Resolving("api.mixed.example", IPAddress.Parse("1.1.1.1"), IPAddress.Loopback));

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri($"https://api.mixed.example:{_internal.Port}/"), Ct));

        TenantEndpointGuard.FindBlocked(error).ShouldNotBeNull();
        await _internal.ShouldHaveNoConnectionsAsync();
    }

    [Fact]
    public async Task The_shared_client_refuses_localhost_and_address_literals_with_the_system_resolver()
    {
        foreach (var uri in new[] { $"https://localhost:{_internal.Port}/", $"https://127.0.0.1:{_internal.Port}/", $"https://[::ffff:127.0.0.1]:{_internal.Port}/" })
        {
            var error = await Should.ThrowAsync<HttpRequestException>(() => TenantEndpointGuard.HttpClient.GetAsync(new Uri(uri), Ct));
            TenantEndpointGuard.FindBlocked(error).ShouldNotBeNull(uri);
        }

        await _internal.ShouldHaveNoConnectionsAsync();
    }

    [Fact]
    public async Task Plain_http_is_refused_before_anything_is_resolved()
    {
        var resolved = 0;
        using var client = TenantEndpointGuard.CreateClient((_, _) =>
        {
            Interlocked.Increment(ref resolved);
            return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") });
        });

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://api.openai.example:{_internal.Port}/"), Ct));

        TenantEndpointGuard.FindBlocked(error).ShouldNotBeNull();
        resolved.ShouldBe(0);
    }

    [Theory]
    [InlineData(LlmProviderKind.OpenAi)]
    [InlineData(LlmProviderKind.OpenAiCompatible)]
    public async Task A_tenant_openai_configuration_never_reaches_an_internal_host_and_reports_no_upstream_text(LlmProviderKind kind)
    {
        var factory = new OpenAiCompatibleProviderFactory { TenantHttpClient = TenantEndpointGuard.CreateClient(Resolving("llm.tenant.example", IPAddress.Loopback)) };
        var provider = factory.Create(Config(kind, ConfigScope.Tenant, new Uri($"https://llm.tenant.example:{_internal.Port}/v1")), new LlmCredential("sk-test-byok-1234567890"));

        var error = await Should.ThrowAsync<LlmException>(() => provider.ValidateCredentialAsync(Ct));

        error.Kind.ShouldBe(LlmErrorKind.Unavailable);
        error.IsRetryable.ShouldBeFalse("a blocked endpoint stays blocked; retrying only probes again");
        error.Message.ShouldBe(TenantEndpointGuard.BlockedMessage);
        await _internal.ShouldHaveNoConnectionsAsync();
    }

    [Fact]
    public async Task A_tenant_anthropic_configuration_never_reaches_an_internal_host()
    {
        var factory = new AnthropicProviderFactory { TenantHttpClient = TenantEndpointGuard.CreateClient(Resolving("llm.tenant.example", IPAddress.Loopback)) };
        var config = Config(LlmProviderKind.Anthropic, ConfigScope.Tenant, new Uri($"https://llm.tenant.example:{_internal.Port}"));

        // Twice: the shared client must survive providers built one after the other.
        for (var i = 0; i < 2; i++)
        {
            var error = await Should.ThrowAsync<LlmException>(() => factory.Create(config, new LlmCredential("sk-ant-test-byok-1234567890")).ValidateCredentialAsync(Ct));
            error.Kind.ShouldBe(LlmErrorKind.Unavailable);
            error.Message.ShouldBe(TenantEndpointGuard.BlockedMessage);
        }

        await _internal.ShouldHaveNoConnectionsAsync();
    }

    [Theory]
    [InlineData(LlmProviderKind.OpenAiCompatible)]
    [InlineData(LlmProviderKind.Anthropic)]
    public void A_tenant_row_with_a_refused_endpoint_from_before_the_check_cannot_build_a_provider(LlmProviderKind kind)
    {
        var config = Config(kind, ConfigScope.Tenant, new Uri("http://127.0.0.1:5394/v1"));
        ILlmProviderFactory factory = kind == LlmProviderKind.Anthropic ? new AnthropicProviderFactory() : new OpenAiCompatibleProviderFactory();

        var error = Should.Throw<LlmException>(() => factory.Create(config, new LlmCredential("sk-test-byok-1234567890")));

        error.Kind.ShouldBe(LlmErrorKind.Unavailable);
        error.Message.ShouldBe(TenantEndpointGuard.BlockedMessage);
    }

    [Fact]
    public async Task A_platform_configuration_may_call_a_self_hosted_model_on_the_private_network()
    {
        var factory = new OpenAiCompatibleProviderFactory { TenantHttpClient = TenantEndpointGuard.CreateClient(Resolving("unused.example", IPAddress.Loopback)) };
        var provider = factory.Create(Config(LlmProviderKind.OpenAiCompatible, ConfigScope.Platform, new Uri($"http://127.0.0.1:{_internal.Port}/v1")), new LlmCredential("sk-platform-1234567890"));

        await Should.ThrowAsync<LlmException>(() => provider.ValidateCredentialAsync(Ct)); // the listener answers 401

        (await _internal.FirstRequestLineAsync()).ShouldStartWith("POST /v1/chat/completions");
    }

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolving(string name, params IPAddress[] addresses) =>
        (host, _) => string.Equals(host, name, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult(addresses)
            : throw new SocketException((int)SocketError.HostNotFound);

    private static LlmProviderConfig Config(LlmProviderKind kind, ConfigScope scope, Uri endpoint) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        TenantId = scope == ConfigScope.Tenant ? TenantId : null,
        Kind = kind,
        Endpoint = endpoint,
        Auth = new LlmAuth(LlmAuthType.ApiKey, SecretRef: "tenant/x/y"),
        Models = new Dictionary<LlmStage, string> { [LlmStage.Classify] = "model-small", [LlmStage.Extract] = "model-large" }.ToFrozenDictionary(),
    };

    /// <summary>A stand-in internal service: accepts connections on loopback, records the first request line and answers 401.</summary>
    private sealed class RecordingListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _connections;

        public RecordingListener()
        {
            _listener.Start();
            _loop = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task ShouldHaveNoConnectionsAsync()
        {
            await Task.Delay(100, Ct); // a connection the client made would have been accepted by now
            Volatile.Read(ref _connections).ShouldBe(0, "nothing may reach the internal service");
        }

        public Task<string> FirstRequestLineAsync() => _firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                await using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token) ?? string.Empty;
                _firstRequest.TrySetResult(line);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
                {
                    // headers
                }

                var body = "{\"error\":{\"message\":\"nope\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}";
                var response = $"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
    }
}
