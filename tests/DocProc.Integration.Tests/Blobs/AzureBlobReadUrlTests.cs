using System.Diagnostics.CodeAnalysis;
using System.Web;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Microsoft.Extensions.Time.Testing;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>
/// SAS generation without a network: signing is local once the client holds a key. The account-key path uses the
/// well-known development-storage key; the user-delegation path uses a client that returns a fabricated delegation
/// key, which exercises the same HMAC signing the real key would. Only the key fetch itself needs a live service,
/// and that is covered by the Azure dev environment, not here.
/// </summary>
public sealed class AzureBlobReadUrlTests
{
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

    /// <summary>A credential-authenticated client whose delegation key comes from memory instead of the service.</summary>
    private sealed class FakeDelegationClient(Uri serviceUri) : BlobServiceClient(serviceUri, new NeverCalledCredential())
    {
        public const string ObjectId = "11111111-2222-3333-4444-555555555555";
        public const string TenantId = "66666666-7777-8888-9999-000000000000";

        public int KeyFetches { get; private set; }

        public override Task<Response<UserDelegationKey>> GetUserDelegationKeyAsync(DateTimeOffset? startsOn, DateTimeOffset expiresOn, CancellationToken cancellationToken = default)
        {
            KeyFetches++;
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
