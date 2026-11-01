using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Web;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>
/// <see cref="AzureBlobStore"/> without Azurite. SAS generation needs no network: signing is local once the client
/// holds a key. The account-key path uses the well-known development-storage key; the user-delegation path uses a
/// client that returns a fabricated delegation key, which exercises the same HMAC signing the real key would. Only
/// the key fetch itself needs a live service, and that is covered by the Azure dev environment, not here. The
/// not-found mapping is proven against a loopback listener that answers 404 like the service does.
/// </summary>
public sealed class AzureBlobReadUrlTests
{
    private const string DevelopmentStorageKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly string Sha = new('c', 64);
    private static readonly BlobRef Reference = new(BlobContainers.Raw, $"{Tenant:D}/{Sha}", Sha, 12, "application/pdf");
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Account_key_connection_string_yields_a_read_only_sas_with_the_requested_expiry()
    {
        var clock = new FakeTimeProvider(Now);
        var store = new AzureBlobStore(new BlobServiceClient("UseDevelopmentStorage=true"), clock, TimeSpan.FromMinutes(15));

        var url = await store.GetReadUrlAsync(Reference, TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);

        url.AbsolutePath.ShouldBe($"/devstoreaccount1/raw/{Tenant:D}/{Sha}");
        var query = HttpUtility.ParseQueryString(url.Query);
        query["sp"].ShouldBe("r");
        query["sr"].ShouldBe("b");
        query["sig"].ShouldNotBeNullOrEmpty();
        query["se"].ShouldBe("2026-10-30T12:10:00Z");
        query["st"].ShouldBe("2026-10-30T11:55:00Z");
    }

    [Fact]
    public async Task Requested_lifetime_is_clamped_to_the_configured_maximum()
    {
        var clock = new FakeTimeProvider(Now);
        var store = new AzureBlobStore(new BlobServiceClient("UseDevelopmentStorage=true"), clock, TimeSpan.FromMinutes(15));

        var url = await store.GetReadUrlAsync(Reference, TimeSpan.FromHours(8), TestContext.Current.CancellationToken);

        HttpUtility.ParseQueryString(url.Query)["se"].ShouldBe("2026-10-30T12:15:00Z");
    }

    [Fact]
    public async Task Non_positive_lifetime_and_hostile_references_are_rejected()
    {
        var store = new AzureBlobStore(new BlobServiceClient("UseDevelopmentStorage=true"), new FakeTimeProvider(Now), TimeSpan.FromMinutes(15));
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => store.GetReadUrlAsync(Reference, TimeSpan.Zero, ct));
        await Should.ThrowAsync<ArgumentException>(() => store.GetReadUrlAsync(Reference with { Key = "../secret" }, TimeSpan.FromMinutes(1), ct));
        await Should.ThrowAsync<ArgumentException>(() => store.GetReadUrlAsync(Reference with { Container = "Raw" }, TimeSpan.FromMinutes(1), ct));
    }

    [Fact]
    public async Task Token_credential_client_signs_with_a_user_delegation_key_and_caches_it()
    {
        var clock = new FakeTimeProvider(Now);
        var client = new FakeDelegationClient(new Uri("https://docprocdev.blob.core.windows.net"));
        var store = new AzureBlobStore(client, clock, TimeSpan.FromMinutes(15));
        var ct = TestContext.Current.CancellationToken;

        var first = await store.GetReadUrlAsync(Reference, TimeSpan.FromMinutes(10), ct);

        first.Host.ShouldBe("docprocdev.blob.core.windows.net");
        first.AbsolutePath.ShouldBe($"/raw/{Tenant:D}/{Sha}");
        var query = HttpUtility.ParseQueryString(first.Query);
        query["sp"].ShouldBe("r");
        query["se"].ShouldBe("2026-10-30T12:10:00Z");
        query["skoid"].ShouldBe(FakeDelegationClient.ObjectId);
        query["sktid"].ShouldBe(FakeDelegationClient.TenantId);
        query["sig"].ShouldNotBeNullOrEmpty();
        client.KeyFetches.ShouldBe(1);

        // Within the key's validity the cached key is reused.
        clock.Advance(TimeSpan.FromMinutes(30));
        await store.GetReadUrlAsync(Reference, TimeSpan.FromMinutes(10), ct);
        client.KeyFetches.ShouldBe(1);

        // Once the key cannot cover the requested expiry any more, a fresh one is fetched.
        clock.Advance(TimeSpan.FromHours(1));
        await store.GetReadUrlAsync(Reference, TimeSpan.FromMinutes(10), ct);
        client.KeyFetches.ShouldBe(2);
    }

    [Fact]
    public async Task Delegation_key_lifetime_is_at_least_an_hour_and_never_beyond_the_seven_days_azure_allows()
    {
        var clock = new FakeTimeProvider(Now);
        var ct = TestContext.Current.CancellationToken;

        var shortLived = new FakeDelegationClient(new Uri("https://docprocdev.blob.core.windows.net"));
        await new AzureBlobStore(shortLived, clock, TimeSpan.FromMinutes(5)).GetReadUrlAsync(Reference, TimeSpan.FromMinutes(5), ct);
        shortLived.LastRequestedExpiry.ShouldBe(Now + TimeSpan.FromHours(1));

        // 4 x 7 days would be 28 days: the service refuses anything past 7 days with a 400, so the request is clamped.
        var longLived = new FakeDelegationClient(new Uri("https://docprocdev.blob.core.windows.net"));
        var url = await new AzureBlobStore(longLived, clock, TimeSpan.FromDays(7)).GetReadUrlAsync(Reference, TimeSpan.FromDays(30), ct);
        longLived.LastRequestedExpiry.ShouldBe(Now + TimeSpan.FromDays(7));
        HttpUtility.ParseQueryString(url.Query)["se"].ShouldBe("2026-11-06T12:00:00Z");
    }

    [Fact]
    public void A_maximum_read_url_lifetime_above_seven_days_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AzureBlobStore(new BlobServiceClient("UseDevelopmentStorage=true"), new FakeTimeProvider(Now), TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_404_from_the_service_surfaces_as_blob_not_found()
    {
        using var service = new NotFoundBlobService();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(new SocketsHttpHandler { UseProxy = false })) };
        options.Retry.MaxRetries = 0;
        var store = new AzureBlobStore(new BlobServiceClient(service.ConnectionString, options), new FakeTimeProvider(Now), TimeSpan.FromMinutes(15));

        var error = await Should.ThrowAsync<BlobNotFoundException>(() => store.OpenReadAsync(Reference, TestContext.Current.CancellationToken));

        error.Reference.ShouldBe(Reference);
        error.InnerException.ShouldBeOfType<RequestFailedException>().Status.ShouldBe(404);
        service.Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_spool_files_in_the_temp_folder_are_swept_when_a_store_is_constructed()
    {
        // The spool folder is shared per machine and other test classes construct stores concurrently with the
        // system clock, so the timestamps are relative to the real clock here.
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(AzureBlobStore.DefaultSpoolPath);
        var stale = Path.Combine(AzureBlobStore.DefaultSpoolPath, $"{Guid.NewGuid():N}.part");
        var fresh = Path.Combine(AzureBlobStore.DefaultSpoolPath, $"{Guid.NewGuid():N}.part");
        await File.WriteAllBytesAsync(stale, new byte[8], ct);
        await File.WriteAllBytesAsync(fresh, new byte[8], ct);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        try
        {
            var store = new AzureBlobStore(new BlobServiceClient("UseDevelopmentStorage=true"), TimeProvider.System, TimeSpan.FromMinutes(15));

            store.SpoolPath.ShouldBe(AzureBlobStore.DefaultSpoolPath);
            File.Exists(stale).ShouldBeFalse();
            File.Exists(fresh).ShouldBeTrue();
        }
        finally
        {
            File.Delete(fresh);
            File.Delete(stale);
        }
    }

    /// <summary>A loopback blob endpoint that answers every request with 404, the way Azure reports a missing blob.</summary>
    private sealed class NotFoundBlobService : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;

        public NotFoundBlobService()
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            ConnectionString = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={DevelopmentStorageKey};BlobEndpoint=http://127.0.0.1:{port}/devstoreaccount1;";
            _ = Task.Run(ServeAsync);
        }

        public string ConnectionString { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                context.Response.StatusCode = 404;
                context.Response.Headers["x-ms-error-code"] = "BlobNotFound";
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    /// <summary>A credential-authenticated client whose delegation key comes from memory instead of the service.</summary>
    private sealed class FakeDelegationClient(Uri serviceUri) : BlobServiceClient(serviceUri, new NeverCalledCredential())
    {
        public const string ObjectId = "11111111-2222-3333-4444-555555555555";
        public const string TenantId = "66666666-7777-8888-9999-000000000000";

        public int KeyFetches { get; private set; }

        public DateTimeOffset? LastRequestedExpiry { get; private set; }

        public override Task<Response<UserDelegationKey>> GetUserDelegationKeyAsync(DateTimeOffset? startsOn, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
        {
            KeyFetches++;
            LastRequestedExpiry = expiresOn;
            var key = BlobsModelFactory.UserDelegationKey(
                ObjectId,
                TenantId,
                startsOn ?? expiresOn - TimeSpan.FromHours(1),
                expiresOn,
                "b",
                "2025-01-05",
                Convert.ToBase64String(new byte[32]));
            return Task.FromResult(Response.FromValue(key, new EmptyResponse()));
        }
    }

    private sealed class NeverCalledCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test must not reach the network.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test must not reach the network.");
    }

    private sealed class EmptyResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get => null; set { } }
        public override string ClientRequestId { get => string.Empty; set { } }
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
