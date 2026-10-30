using System.Security.Cryptography;
using Azure.Storage.Blobs;
using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Blobs;
using Testcontainers.Azurite;

namespace DocProc.Integration.Tests.Blobs;

/// <summary>
/// <see cref="AzureBlobStore"/> against Azurite in a container (docs/09 "Blob storage"), the same SDK calls that
/// reach Azure Blob Storage in production. Skipped without Docker.
/// </summary>
public sealed class AzureBlobStoreAzuriteTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private AzuriteContainer? _container;
    private AzureBlobStore? _store;

    public async ValueTask InitializeAsync()
    {
        if (!DockerFacts.IsAvailable)
        {
            return;
        }

        _container = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0").Build();
        await _container.StartAsync(TestContext.Current.CancellationToken);

        var client = new BlobServiceClient(_container.GetConnectionString());
        _store = new AzureBlobStore(client, TimeProvider.System, TimeSpan.FromMinutes(15));
        await _store.EnsureContainersAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Put_get_exists_and_find_round_trip_through_azurite()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var bytes = "%PDF-1.7 azurite"u8.ToArray();
        var expectedSha = ContentHash.Sha256Hex(bytes);

        var reference = await _store!.PutAsync(Tenant, new MemoryStream(bytes), "application/pdf", ct);

        reference.ShouldBe(new BlobRef(BlobContainers.Raw, $"{Tenant:D}/{expectedSha}", expectedSha, bytes.Length, "application/pdf"));
        (await _store.ExistsAsync(reference, ct)).ShouldBeTrue();
        (await _store.FindAsync(reference.Container, reference.Key, ct)).ShouldBe(reference);

        await using var read = await _store.OpenReadAsync(reference, ct);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, ct);
        copy.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task Second_put_of_identical_content_skips_the_upload_and_keeps_the_stored_content_type()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[64 * 1024];
        Random.Shared.NextBytes(bytes);

        var first = await _store!.PutAsync(Tenant, new MemoryStream(bytes), "image/tiff", ct);
        var second = await _store.PutAsync(Tenant, new MemoryStream(bytes), "application/octet-stream", ct);

        second.ShouldBe(first);
    }

    [Fact]
    public async Task Concurrent_uploads_of_the_same_bytes_all_succeed()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[256 * 1024];
        Random.Shared.NextBytes(bytes);

        var references = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _store!.PutAsync(Tenant, new MemoryStream(bytes), "application/pdf", ct)));

        references.Distinct().Count().ShouldBe(1);
        references[0].Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public async Task Non_seekable_stream_is_spooled_and_uploaded_with_its_content_type()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var bytes = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        var reference = await _store!.PutAsync(BlobContainers.Derived, Tenant, new ForwardOnlyStream(new MemoryStream(bytes)), "image/png", ct);

        reference.Container.ShouldBe(BlobContainers.Derived);
        reference.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        reference.SizeBytes.ShouldBe(bytes.LongLength);
        (await _store.FindAsync(reference.Container, reference.Key, ct))!.ContentType.ShouldBe("image/png");
    }

    [Fact]
    public async Task Read_url_serves_the_bytes_with_the_stored_content_type_and_only_the_bytes()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var bytes = "viewer bytes"u8.ToArray();
        var reference = await _store!.PutAsync(Tenant, new MemoryStream(bytes), "text/plain", ct);

        var url = await _store.GetReadUrlAsync(reference, TimeSpan.FromMinutes(5), ct);

        using var http = new HttpClient();
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsByteArrayAsync(ct)).ShouldBe(bytes);

        // The token is read-only: an overwrite through it is refused.
        using var put = await http.PutAsync(url, new ByteArrayContent("tampered"u8.ToArray()), ct);
        put.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task Unknown_reference_does_not_exist()
    {
        DockerFacts.SkipUnlessAvailable();
        var ct = TestContext.Current.CancellationToken;
        var reference = new BlobRef(BlobContainers.Exports, $"{Tenant:D}/{new string('0', 64)}", new string('0', 64), 0, "text/csv");

        (await _store!.ExistsAsync(reference, ct)).ShouldBeFalse();
        (await _store.FindAsync(reference.Container, reference.Key, ct)).ShouldBeNull();
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
