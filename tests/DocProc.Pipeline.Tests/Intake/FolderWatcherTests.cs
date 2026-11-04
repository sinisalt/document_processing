using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Infrastructure.Blobs;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Tests.Pdf;
using DocProc.Pipeline.Tests.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests.Intake;

/// <summary>
/// The folder watcher of docs/11 §7.1: stable-size check, the folder path recorded relative to the channel root, the
/// move to processed/, once per file, the tenant's own root only, and no polling without <c>sourceRouting</c>.
/// </summary>
public sealed class FolderWatcherTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task A_stable_file_is_ingested_once_with_its_folder_path_moved_to_processed_and_routed_by_its_folder()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var supplier = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"scans","stableSeconds":5}""");
        await SourceRoutingPipelineTests.AddRuleAsync(host, supplier.Id, r =>
        {
            r.ChannelId = channel;
            r.FolderPrefix = "lieferanten/nordwind/";
            r.PinTypeKey = "Statement";
        });
        var folder = Directory.CreateDirectory(Path.Combine(root, "tenant-a", "scans", "lieferanten", "nordwind")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(folder, "auszug.pdf"), TestPdfs.GermanInvoice(), Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "auszug.pdf.part"), "still copying", Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, ".hidden.pdf"), "hidden", Ct);
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0, "the first scan only sees the file");
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1);
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0, "the file was moved");

        File.Exists(Path.Combine(folder, "auszug.pdf")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "tenant-a", "scans", "processed", "lieferanten", "nordwind", "auszug.pdf")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "auszug.pdf.part")).ShouldBeTrue("a partial file is left alone");

        var ingestion = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.ChannelId == channel));
        ingestion.SourceFolderPath.ShouldBe("lieferanten/nordwind/");
        ingestion.SourceFileName.ShouldBe("auszug.pdf");
        ingestion.SourceMessageId.ShouldNotBeNull().ShouldStartWith("file:");
        ingestion.SourceSender.ShouldBeNull();

        await host.RunUntilIdleAsync();
        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.SupplierId.ShouldBe(supplier.Id);
        document.Type.ShouldBe(DocumentTypeKey.Statement);
        document.TypeSource.ShouldBe(TypeSource.Supplier);
    }

    [Fact]
    public async Task A_file_that_keeps_changing_waits_and_the_same_file_is_not_ingested_twice()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"","stableSeconds":5}""");
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "tenant-a")).FullName, "rechnung.pdf");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        await File.WriteAllBytesAsync(file, TestPdfs.GermanInvoice(), Ct);
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0);
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        await File.AppendAllTextAsync(file, "\n%more bytes", Ct);
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0, "it grew since the last scan");
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1);

        // A second host that saw the same file before it was moved records nothing twice: the message id is unique per channel.
        var ingestion = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.ChannelId == channel));
        var twin = ingestion.SourceMessageId!;
        await host.WithDbAsync(async db =>
        {
            var copy = new Ingestion { Id = Guid.CreateVersion7(), TenantId = PipelineTestHost.TenantA, ChannelId = channel, ReceivedAt = host.Clock.GetUtcNow(), SourceMessageId = twin, RawBlob = ingestion.RawBlob, UpdatedAt = host.Clock.GetUtcNow() };
            db.Ingestions.Add(copy);
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Links_are_skipped_and_a_tenant_without_sourceRouting_is_not_polled()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"","stableSeconds":0}""");
        var tenantB = await AddChannelAsync(host, DevSeed.TenantB, """{"path":"","stableSeconds":0}""");
        var tenantRoot = Directory.CreateDirectory(Path.Combine(root, "tenant-a")).FullName;
        var outside = Path.Combine(root, "secret.pdf");
        await File.WriteAllBytesAsync(outside, TestPdfs.GermanInvoice(), Ct);
        File.CreateSymbolicLink(Path.Combine(tenantRoot, "link.pdf"), outside);
        await File.WriteAllBytesAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "tenant-b")).FullName, "b.pdf"), TestPdfs.GermanInvoice(), Ct);
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollOnceAsync(Ct)).ShouldBe(0);
        File.Exists(outside).ShouldBeTrue();
        File.Exists(Path.Combine(root, "tenant-b", "b.pdf")).ShouldBeTrue("tenant B has sourceRouting off and is not polled");
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().CountAsync(i => i.ChannelId == channel || i.ChannelId == tenantB))).ShouldBe(0);
    }

    [Fact]
    public async Task A_channel_folder_that_is_a_link_into_another_tenants_root_is_not_read()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var secret = Directory.CreateDirectory(Path.Combine(root, "tenant-b", "secret")).FullName;
        var file = Path.Combine(secret, "b.pdf");
        await File.WriteAllBytesAsync(file, TestPdfs.GermanInvoice(), Ct);
        var tenantRoot = Directory.CreateDirectory(Path.Combine(root, "tenant-a")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(tenantRoot, "scans"), secret);
        var direct = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"scans","stableSeconds":0}""");

        // A real folder of the tenant's whose way to it passes the link: ...tenant-a/via/inner, via being a link.
        var outer = Directory.CreateDirectory(Path.Combine(root, "tenant-b", "outer", "inner")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(outer, "c.pdf"), TestPdfs.GermanInvoice(), Ct);
        Directory.CreateSymbolicLink(Path.Combine(tenantRoot, "via"), Path.Combine(root, "tenant-b", "outer"));
        var nested = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"via/inner","stableSeconds":0}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, direct, Ct)).ShouldBe(0);
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, nested, Ct)).ShouldBe(0);

        File.Exists(file).ShouldBeTrue("tenant B's file stays where it is");
        Directory.Exists(Path.Combine(secret, "processed")).ShouldBeFalse();
        File.Exists(Path.Combine(outer, "c.pdf")).ShouldBeTrue();
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().CountAsync(i => i.ChannelId == direct || i.ChannelId == nested))).ShouldBe(0);
        var channels = await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().Where(c => c.Id == direct || c.Id == nested).ToListAsync());
        channels.Single(c => c.Id == direct).LastError.ShouldNotBeNull().ShouldContain("scans is a link");
        channels.Single(c => c.Id == nested).LastError.ShouldNotBeNull().ShouldContain("via is a link");
    }

    [Fact]
    public async Task A_processed_folder_that_is_a_link_stops_the_channel_before_a_file_is_moved_through_it()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var elsewhere = Directory.CreateDirectory(Path.Combine(root, "tenant-b", "inbox")).FullName;
        var scans = Directory.CreateDirectory(Path.Combine(root, "tenant-a", "scans")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(scans, "processed"), elsewhere);
        var file = Path.Combine(scans, "rechnung.pdf");
        await File.WriteAllBytesAsync(file, TestPdfs.GermanInvoice(), Ct);
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"scans","stableSeconds":0}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0);

        File.Exists(file).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(elsewhere).ShouldBeEmpty();
        (await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().SingleAsync(c => c.Id == channel))).LastError.ShouldNotBeNull().ShouldContain("processed is a link");
    }

    [Fact]
    public async Task A_file_swapped_for_a_link_after_the_scan_is_not_read_through_it()
    {
        var root = NewRoot();
        var blobs = Path.Combine(root, ".blobs");
        var tenantRoot = Directory.CreateDirectory(Path.Combine(root, "tenant-a")).FullName;
        var secret = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "tenant-b", "private")).FullName, "secret.pdf");
        await File.WriteAllTextAsync(secret, "TENANT-B-SECRET", Ct);
        await File.WriteAllBytesAsync(Path.Combine(tenantRoot, "a.pdf"), TestPdfs.GermanInvoice(), Ct);
        await File.WriteAllTextAsync(Path.Combine(tenantRoot, "b.pdf"), "tenant A's own b", Ct);

        // The first blob written (a.pdf's, after the scan saw both files) swaps b.pdf for a link to tenant B's file.
        var swap = new SwapOnFirstPut(blobs, () =>
        {
            File.Delete(Path.Combine(tenantRoot, "b.pdf"));
            File.CreateSymbolicLink(Path.Combine(tenantRoot, "b.pdf"), secret);
        });
        await using var host = await StartAsync(root, blobs, swap);
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"","stableSeconds":0}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1, "a.pdf only");

        swap.Swapped.ShouldBeTrue();
        swap.Stored.ShouldNotContain(bytes => System.Text.Encoding.UTF8.GetString(bytes).Contains("TENANT-B-SECRET", StringComparison.Ordinal), "nothing was read through the link");
        File.ReadAllText(secret).ShouldBe("TENANT-B-SECRET");
        new FileInfo(Path.Combine(tenantRoot, "b.pdf")).LinkTarget.ShouldBe(secret, "the link is neither read nor moved");
        File.Exists(Path.Combine(tenantRoot, "processed", "b.pdf")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_after_the_scan_is_not_read_through_it()
    {
        var root = NewRoot();
        var blobs = Path.Combine(root, ".blobs");
        var tenantRoot = Directory.CreateDirectory(Path.Combine(root, "tenant-a")).FullName;
        var privateB = Directory.CreateDirectory(Path.Combine(root, "tenant-b", "private")).FullName;
        await File.WriteAllTextAsync(Path.Combine(privateB, "c.pdf"), "TENANT-B-SECRET", Ct);
        await File.WriteAllBytesAsync(Path.Combine(tenantRoot, "a.pdf"), TestPdfs.GermanInvoice(), Ct);
        var sub = Directory.CreateDirectory(Path.Combine(tenantRoot, "sub")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "c.pdf"), "tenant A's own c", Ct);
        var swap = new SwapOnFirstPut(blobs, () =>
        {
            Directory.Move(sub, sub + "-old");
            Directory.CreateSymbolicLink(sub, privateB);
        });
        await using var host = await StartAsync(root, blobs, swap);
        var channel = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"","stableSeconds":0}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1);

        swap.Swapped.ShouldBeTrue();
        swap.Stored.ShouldNotContain(bytes => System.Text.Encoding.UTF8.GetString(bytes).Contains("TENANT-B-SECRET", StringComparison.Ordinal));
        File.Exists(Path.Combine(privateB, "c.pdf")).ShouldBeTrue("tenant B's file is not moved through the link either");
        Directory.Exists(Path.Combine(privateB, "processed")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_is_read_from_a_handle_checked_against_the_real_path_of_the_tenants_root()
    {
        // The drop zone itself may be reached through a link (a mounted volume): that is the platform's, not the tenant's.
        var real = NewRoot();
        var linked = Path.Combine(NewRoot(), "zone");
        Directory.CreateSymbolicLink(linked, real);
        var tenantRoot = Directory.CreateDirectory(Path.Combine(linked, "tenant-a", "scans")).Parent!.FullName;
        var file = Path.Combine(tenantRoot, "scans", "a.pdf");
        await File.WriteAllTextAsync(file, "%PDF-1.4 a", Ct);

        ChannelPollerHostedService.RealPath(file).ShouldBe(Path.Combine(real, "tenant-a", "scans", "a.pdf"));
        ChannelPollerHostedService.RealPath(Path.Combine(linked, "tenant-a", "missing", "x")).ShouldBe(Path.Combine(real, "tenant-a", "missing", "x"));
        var (bytes, refusal) = await ChannelPollerHostedService.ReadInsideAsync(tenantRoot, file, 1024, Ct);
        refusal.ShouldBe(ChannelPollerHostedService.FolderReadRefusal.None);
        bytes.ShouldNotBeNull().Length.ShouldBe(10);
        (await ChannelPollerHostedService.ReadInsideAsync(tenantRoot, file, 5, Ct)).Refusal.ShouldBe(ChannelPollerHostedService.FolderReadRefusal.TooLarge);

        File.CreateSymbolicLink(Path.Combine(tenantRoot, "scans", "l.pdf"), file);
        (await ChannelPollerHostedService.ReadInsideAsync(tenantRoot, Path.Combine(tenantRoot, "scans", "l.pdf"), 1024, Ct)).Refusal.ShouldBe(ChannelPollerHostedService.FolderReadRefusal.Link);
    }

    [Fact]
    public async Task Two_channels_of_one_folder_are_never_both_read_and_a_key_is_backfilled_on_the_first_poll()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        Directory.CreateDirectory(Path.Combine(root, "tenant-a", "scans"));
        await File.WriteAllBytesAsync(Path.Combine(root, "tenant-a", "scans", "a.pdf"), TestPdfs.GermanInvoice(), Ct);

        // Rows written without a source key (before P4, or while sourceRouting was off), reading the same folder.
        var first = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"scans","stableSeconds":0}""");
        var second = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"scans","stableSeconds":0}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollOnceAsync(Ct)).ShouldBe(1);
        var channels = await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().Where(c => c.Id == first || c.Id == second).ToListAsync());
        channels.Single(c => c.Id == first).SourceKey.ShouldBe("folder:tenant-a/scans/");
        channels.Single(c => c.Id == first).Status.ShouldBe(ChannelStatus.Active);
        var loser = channels.Single(c => c.Id == second);
        loser.Status.ShouldBe(ChannelStatus.Error);
        loser.SourceKey.ShouldBeNull();
        loser.LastError.ShouldNotBeNull().ShouldContain("Another channel already reads");
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().CountAsync(i => i.ChannelId == second))).ShouldBe(0);

        // A key that no longer matches the config (changed outside the API) is recomputed, under the same check.
        await host.WithDbAsync(async db =>
        {
            var row = await db.IngestionChannels.SingleAsync(c => c.Id == first);
            row.ConfigJson = """{"path":"other","stableSeconds":0}""";
            await db.SaveChangesAsync();
        });
        Directory.CreateDirectory(Path.Combine(root, "tenant-a", "other"));
        await poller.PollChannelAsync(PipelineTestHost.TenantA, first, Ct);
        (await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().SingleAsync(c => c.Id == first))).SourceKey.ShouldBe("folder:tenant-a/other/");
    }

    [Fact]
    public void A_link_anywhere_on_the_way_is_found()
    {
        var root = NewRoot();
        var real = Directory.CreateDirectory(Path.Combine(root, "a", "b")).FullName;
        ChannelPollerHostedService.LinkOnPath(root, real).ShouldBeNull();
        ChannelPollerHostedService.LinkOnPath(root, root).ShouldBeNull();
        ChannelPollerHostedService.LinkOnPath(root, Path.Combine(root, "a", "missing", "deeper")).ShouldBeNull("a part that does not exist ends the walk");
        Directory.CreateSymbolicLink(Path.Combine(root, "l"), real);
        ChannelPollerHostedService.LinkOnPath(root, Path.Combine(root, "l", "x")).ShouldBe(Path.Combine(root, "l"));
        Directory.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "nowhere"));
        ChannelPollerHostedService.LinkOnPath(root, Path.Combine(root, "dangling")).ShouldBe(Path.Combine(root, "dangling"));
    }

    [Fact]
    public async Task A_missing_folder_or_an_invalid_config_is_the_channels_last_error()
    {
        var root = NewRoot();
        await using var host = await StartAsync(root);
        var missing = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"nicht-da"}""");
        var invalid = await AddChannelAsync(host, PipelineTestHost.TenantA, """{"path":"../tenant-b"}""");
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        await poller.PollOnceAsync(Ct);

        var channels = await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().Where(c => c.Id == missing || c.Id == invalid).ToListAsync());
        channels.Single(c => c.Id == missing).LastError.ShouldNotBeNull().ShouldContain("does not exist");
        channels.Single(c => c.Id == invalid).LastError.ShouldNotBeNull().ShouldContain("not valid");
    }

    private static string NewRoot() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "docproc-dropzone-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static async Task<PipelineTestHost> StartAsync(string root, string? blobRoot = null, IBlobStore? blobs = null)
    {
        var host = await PipelineTestHost.StartAsync(
            settings =>
            {
                settings["Intake:PollersEnabled"] = "false";
                settings["Intake:FolderRoot"] = root;
                if (blobRoot is not null)
                {
                    settings["Blobs:RootPath"] = blobRoot;
                }
            },
            services =>
            {
                if (blobs is not null)
                {
                    services.AddSingleton(blobs);
                }
            });
        await SourceRoutingPipelineTests.EnableRoutingAsync(host, PipelineTestHost.TenantA);
        return host;
    }

    /// <summary>A blob store that runs an action when the first blob is written: a deterministic window after the scan.</summary>
    private sealed class SwapOnFirstPut(string root, Action swap) : IBlobStore
    {
        private readonly FileSystemBlobStore _inner = new(root);

        public bool Swapped { get; private set; }

        public List<byte[]> Stored { get; } = [];

        public async Task<BlobRef> PutAsync(Guid tenantId, Stream content, string contentType, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Stored.Add(copy.ToArray());
            copy.Position = 0;
            var stored = await _inner.PutAsync(tenantId, copy, contentType, cancellationToken);
            if (!Swapped)
            {
                Swapped = true;
                swap();
            }

            return stored;
        }

        public Task<Stream> OpenReadAsync(BlobRef reference, CancellationToken cancellationToken) => _inner.OpenReadAsync(reference, cancellationToken);

        public Task<bool> ExistsAsync(BlobRef reference, CancellationToken cancellationToken) => _inner.ExistsAsync(reference, cancellationToken);

        public Task<Uri> GetReadUrlAsync(BlobRef reference, TimeSpan validFor, CancellationToken cancellationToken) => _inner.GetReadUrlAsync(reference, validFor, cancellationToken);
    }

    internal static Task<Guid> AddChannelAsync(PipelineTestHost host, Guid tenantId, string config, ChannelType type = ChannelType.WatchedFolder, string? secretRef = null) => host.WithDbAsync(async db =>
    {
        var channel = new IngestionChannel { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = $"{type} {Guid.NewGuid():N}"[..40], Type = type, ConfigJson = config, SecretRef = secretRef, CreatedAt = host.Clock.GetUtcNow() };
        db.IngestionChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    });
}
