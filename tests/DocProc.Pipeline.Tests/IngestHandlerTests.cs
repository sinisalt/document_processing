using System.Text;
using DocProc.Application.Pipeline;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Support;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Pipeline.Tests;

/// <summary>Stage 1 against the real runner: duplicate detection under concurrency and archive handling.</summary>
public class IngestHandlerTests
{
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task Two_ingestions_with_the_same_bytes_processed_together_create_exactly_one_document()
    {
        await using var host = await PipelineTestHost.StartAsync();

        // Ten rounds, because the race only shows up when both ingestions are in stage 1 at the same time (parallelism 2).
        // Even rounds give both items the same CreatedAt (the v7 id breaks the tie), odd rounds a later one for the second.
        for (var round = 0; round < 10; round++)
        {
            var bytes = PipelineTestHost.FixtureVariant($"round {round}");
            var first = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("a.xml", XmlMime, bytes)], enqueue: false);
            if (round % 2 == 1)
            {
                host.Clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var second = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("b.xml", XmlMime, bytes)], enqueue: false);
            await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, first.Id), CancellationToken.None);
            await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, second.Id), CancellationToken.None);
            await host.RunUntilIdleAsync();

            var firstItem = (await host.ItemsAsync(first.Id)).ShouldHaveSingleItem();
            var secondItem = (await host.ItemsAsync(second.Id)).ShouldHaveSingleItem();
            firstItem.Disposition.ShouldBe(ItemDisposition.Accepted, $"round {round}: the earlier item owns the document");
            secondItem.Disposition.ShouldBe(ItemDisposition.Duplicate, $"round {round}: the later item is the duplicate");
            secondItem.DuplicateOfItemId.ShouldBe(firstItem.Id);

            var itemIds = new[] { firstItem.Id, secondItem.Id };
            var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => itemIds.Contains(d.IngestionItemId)).ToListAsync());
            documents.ShouldHaveSingleItem($"round {round}").IngestionItemId.ShouldBe(firstItem.Id);
            foreach (var id in new[] { first.Id, second.Id })
            {
                (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == id))).Status.ShouldBe(IngestionStatus.Unpacked);
            }
        }
    }

    [Fact]
    public async Task An_item_is_never_the_duplicate_of_a_later_one()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var bytes = PipelineTestHost.FixtureVariant("later first");

        // The newer ingestion is processed first, while the older one is still waiting: the older item is strictly
        // earlier, so the newer one defers to it, and the older one must still own the document when its turn comes.
        var older = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("old.xml", XmlMime, bytes)], enqueue: false);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var newer = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("new.xml", XmlMime, bytes)], enqueue: false);
        await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, newer.Id), CancellationToken.None);
        await host.RunUntilIdleAsync();
        var newerItem = (await host.ItemsAsync(newer.Id)).ShouldHaveSingleItem();
        newerItem.Disposition.ShouldBe(ItemDisposition.Duplicate, "an older item with the same bytes exists, processed or not");
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(0);

        await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, older.Id), CancellationToken.None);
        await host.RunUntilIdleAsync();

        var olderItem = (await host.ItemsAsync(older.Id)).ShouldHaveSingleItem();
        olderItem.Disposition.ShouldBe(ItemDisposition.Accepted, "the older item is nobody's duplicate, whatever ran first");
        olderItem.DuplicateOfItemId.ShouldBeNull();
        newerItem.DuplicateOfItemId.ShouldBe(olderItem.Id);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().ToListAsync())).ShouldHaveSingleItem().IngestionItemId.ShouldBe(olderItem.Id);
    }

    [Fact]
    public async Task Two_ingestions_with_the_same_zip_processed_together_create_exactly_one_document_per_file()
    {
        await using var host = await PipelineTestHost.StartAsync();

        // Ten rounds, because the race only shows up when both ingestions are in stage 1 at the same time (parallelism 2).
        // The unpacked files exist in no table before stage 1 runs, so the two ingestions can only see each other's
        // files if each commits them before it decides; even rounds give both archives the same CreatedAt.
        for (var round = 0; round < 10; round++)
        {
            var zip = TestZips.With(
                ("a.xml", PipelineTestHost.FixtureVariant($"zip round {round} a")),
                ("b.xml", PipelineTestHost.FixtureVariant($"zip round {round} b")));
            var first = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)], enqueue: false);
            if (round % 2 == 1)
            {
                host.Clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var second = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)], enqueue: false);
            await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, first.Id), CancellationToken.None);
            await host.PipelineQueue.EnqueueAsync(new ProcessIngestion(PipelineTestHost.TenantA, second.Id), CancellationToken.None);
            await host.RunUntilIdleAsync();

            var children = new List<IngestionItem>();
            foreach (var id in new[] { first.Id, second.Id })
            {
                var items = await host.ItemsAsync(id);
                items.Count.ShouldBe(3, $"round {round}: the archive and its two files");
                items.Single(i => i.Kind == ItemKind.Archive).Disposition.ShouldBe(ItemDisposition.Unpacked);
                children.AddRange(items.Where(i => i.ParentItemId is not null));
                (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == id))).Status.ShouldBe(IngestionStatus.Unpacked);
            }

            var childIds = children.Select(c => c.Id).ToArray();
            var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => childIds.Contains(d.IngestionItemId)).ToListAsync());
            documents.Count.ShouldBe(2, $"round {round}: one document per distinct file, however many zips carried it");
            foreach (var sameBytes in children.GroupBy(c => c.Sha256))
            {
                var owner = sameBytes.Where(c => c.Disposition == ItemDisposition.Accepted).ToList().ShouldHaveSingleItem($"round {round}: exactly one owner per file");
                var duplicate = sameBytes.Where(c => c.Disposition == ItemDisposition.Duplicate).ToList().ShouldHaveSingleItem($"round {round}: the other copy is the duplicate");
                duplicate.DuplicateOfItemId.ShouldBe(owner.Id);
                documents.Where(d => d.IngestionItemId == owner.Id).ToList().ShouldHaveSingleItem();
                documents.ShouldNotContain(d => d.IngestionItemId == duplicate.Id);
            }
        }
    }

    [Fact]
    public async Task A_zip_whose_files_duplicate_an_earlier_ingestion_marks_them_duplicates()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var bytes = PipelineTestHost.FixtureVariant("stored before the zip");
        var earlier = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, bytes)]);
        await host.RunUntilIdleAsync();
        var earlierItem = (await host.ItemsAsync(earlier.Id)).ShouldHaveSingleItem();

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var zip = TestZips.With(("copy.xml", bytes), ("other.xml", PipelineTestHost.FixtureVariant("only in the zip")));
        var later = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)]);
        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(later.Id);
        items.Count.ShouldBe(3);
        var copy = items.Single(i => i.FileName == "copy.xml");
        copy.Disposition.ShouldBe(ItemDisposition.Duplicate, "the same bytes already have a document");
        copy.DuplicateOfItemId.ShouldBe(earlierItem.Id);
        var other = items.Single(i => i.FileName == "other.xml");
        other.Disposition.ShouldBe(ItemDisposition.Accepted);
        other.DuplicateOfItemId.ShouldBeNull();

        var itemIds = new[] { earlierItem.Id, copy.Id, other.Id };
        var documents = await host.WithDbAsync(db => db.Documents.AsNoTracking().Where(d => itemIds.Contains(d.IngestionItemId)).ToListAsync());
        documents.Select(d => d.IngestionItemId).Order().ShouldBe(new[] { earlierItem.Id, other.Id }.Order());

        // The unpacked files are committed on their own before the decisions: two units of work, two version bumps.
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == later.Id))).Version.ShouldBe(2);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == earlier.Id))).Version.ShouldBe(1, "an ingestion without archives commits once");
    }

    [Fact]
    public async Task A_corrupt_zip_is_ignored_with_the_reason()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("broken.zip", TestZips.MimeType, TestZips.Corrupt())]);

        await host.RunUntilIdleAsync();

        var item = (await host.ItemsAsync(ingestion.Id)).ShouldHaveSingleItem();
        item.Kind.ShouldBe(ItemKind.Archive);
        item.Disposition.ShouldBe(ItemDisposition.Ignored);
        item.DispositionReason.ShouldNotBeNull().ShouldStartWith("unreadable zip archive: ");
        item.DispositionReason.Length.ShouldBeGreaterThan("unreadable zip archive: ".Length, "the reader's message follows");
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(0);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id))).Status.ShouldBe(IngestionStatus.Unpacked);
    }

    [Fact]
    public async Task A_zip_is_unpacked_one_level_and_its_files_become_documents()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var zip = TestZips.With(("rechnung.xml", PipelineTestHost.Fixture("peppol-bis3-invoice.xml")), ("notes/", []));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        items.Count.ShouldBe(2);
        var archive = items.Single(i => i.Kind == ItemKind.Archive);
        archive.Disposition.ShouldBe(ItemDisposition.Unpacked);
        archive.DispositionReason.ShouldBe("1 file(s) unpacked");
        var child = items.Single(i => i.ParentItemId == archive.Id);
        child.FileName.ShouldBe("rechnung.xml");
        child.Kind.ShouldBe(ItemKind.Xml);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync(d => d.IngestionItemId == child.Id))).ShouldBe(1);
    }

    [Fact]
    public async Task A_zip_entry_that_inflates_past_the_limit_is_skipped_whatever_its_header_says()
    {
        await using var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:MaxArchiveEntryBytes"] = "4096");
        var payload = new byte[256 * 1024];
        var zip = TestZips.LyingAboutSize("bomb.pdf", payload, declaredLength: 100);
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip), System.IO.Compression.ZipArchiveMode.Read))
        {
            archive.Entries.Single().Length.ShouldBe(100, "the header lies, so the declared-length check alone would let the entry through");
        }

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)]);

        await host.RunUntilIdleAsync();

        var item = (await host.ItemsAsync(ingestion.Id)).ShouldHaveSingleItem("the entry was skipped, so no child item exists");
        item.Disposition.ShouldBe(ItemDisposition.Unpacked);
        item.DispositionReason.ShouldBe("0 file(s) unpacked; 1 skipped over the entry size or compression limit");
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync())).ShouldBe(0);
    }

    /// <summary>A zip of <paramref name="archives"/> inner zips, each of <paramref name="entries"/> distinct text files of <paramref name="size"/> bytes.</summary>
    private static byte[] NestedZips(int archives, int entries, int size, Func<int, int, byte[]>? content = null)
    {
        content ??= (i, j) =>
        {
            var bytes = new byte[size];
            var random = new Random((i * 1000) + j);
            random.NextBytes(bytes); // incompressible, so the ratio check never applies
            return bytes;
        };
        return TestZips.With([.. Enumerable.Range(0, archives).Select(i =>
            ($"inner-{i}.zip", TestZips.With([.. Enumerable.Range(0, entries).Select(j => ($"file-{i}-{j}.txt", content(i, j)))])))]);
    }

    [Fact]
    public async Task Nested_archives_stop_at_the_ingestions_unpacked_file_budget()
    {
        await using var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:MaxUnpackedItemsPerIngestion"] = "7");
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, NestedZips(archives: 3, entries: 4, size: 512))]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        items.Count(i => i.ParentItemId is not null).ShouldBe(7, "the three inner zips and four of their files; nothing beyond the budget");
        var outer = items.Single(i => i.ParentItemId is null);
        outer.DispositionReason.ShouldBe("3 file(s) unpacked");
        var inner = items.Where(i => i.Kind == ItemKind.Archive && i.ParentItemId == outer.Id).OrderBy(i => i.FileName).ToList();
        inner[0].DispositionReason.ShouldBe("4 file(s) unpacked");
        inner[1].Disposition.ShouldBe(ItemDisposition.Ignored);
        inner[1].DispositionReason.ShouldBe("not unpacked: the ingestion's limit of 7 unpacked files is reached");
        inner[2].Disposition.ShouldBe(ItemDisposition.Ignored);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync(d => d.IngestionItem!.IngestionId == ingestion.Id))).ShouldBe(4);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().SingleAsync(i => i.Id == ingestion.Id))).Status.ShouldBe(IngestionStatus.Unpacked);
    }

    [Fact]
    public async Task Nested_archives_stop_at_the_ingestions_unpacked_byte_budget()
    {
        await using var host = await PipelineTestHost.StartAsync(settings => settings["Pipeline:MaxUnpackedBytesPerIngestion"] = "40000");
        var zip = NestedZips(archives: 2, entries: 5, size: 6000);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        var unpacked = items.Where(i => i.ParentItemId is not null).ToList();
        unpacked.Sum(i => i.SizeBytes).ShouldBeLessThanOrEqualTo(40000);
        unpacked.Count(i => i.Kind == ItemKind.Text).ShouldBeInRange(1, 9, "some files were unpacked, the rest left in the archives");

        // Each inner zip is about 30 KB: the first fits, the second does not, so the outer archive stops after one file
        // and the first inner zip after one of its files (6 KB of the 9 KB that were left).
        var outer = items.Single(i => i.ParentItemId is null);
        outer.Disposition.ShouldBe(ItemDisposition.Unpacked);
        outer.DispositionReason.ShouldBe("1 file(s) unpacked; the rest was not: the ingestion's limit of 40000 unpacked bytes is reached");
        var inner = items.Single(i => i.Kind == ItemKind.Archive && i.ParentItemId == outer.Id);
        inner.DispositionReason.ShouldBe("1 file(s) unpacked; the rest was not: the ingestion's limit of 40000 unpacked bytes is reached");
    }

    [Fact]
    public async Task A_small_nested_zip_bomb_does_not_inflate_into_the_raw_store()
    {
        // The shape of the reported attack: a few KB of nested zips whose entries are megabytes of one repeated byte.
        await using var host = await PipelineTestHost.StartAsync();
        var bomb = NestedZips(archives: 4, entries: 10, size: 4_000_000, content: (i, j) =>
        {
            var bytes = new byte[4_000_000];
            Encoding.ASCII.GetBytes($"{i}-{j}-").CopyTo(bytes, 0);
            return bytes;
        });
        bomb.Length.ShouldBeLessThan(200_000);
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("bomb.zip", TestZips.MimeType, bomb)]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        items.Where(i => i.Kind == ItemKind.Text).ShouldBeEmpty("every entry inflates about a thousand times its compressed size");
        // Only the four small inner zips themselves (under the ratio floor) are stored, not the 160 MB they hold.
        items.Where(i => i.Kind == ItemKind.Archive && i.ParentItemId is not null).Select(i => i.DispositionReason).ShouldBe(Enumerable.Repeat("0 file(s) unpacked; 10 skipped over the entry size or compression limit", 4));
        items.Where(i => i.ParentItemId is not null).Sum(i => i.SizeBytes).ShouldBeLessThan(4L * PipelineOptions.CompressionRatioFloorBytes);
        (await host.WithDbAsync(db => db.Documents.AsNoTracking().CountAsync(d => d.IngestionItem!.IngestionId == ingestion.Id))).ShouldBe(0);
    }

    [Fact]
    public async Task Highly_compressed_entries_are_unpacked_below_the_ratio_floor_and_skipped_above_it()
    {
        await using var host = await PipelineTestHost.StartAsync();
        var small = new byte[512 * 1024]; // zeros, but under the floor
        "small"u8.CopyTo(small);
        var large = new byte[2 * 1024 * 1024];
        "large"u8.CopyTo(large);
        var zip = TestZips.With(("small.txt", small), ("large.txt", large));
        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("batch.zip", TestZips.MimeType, zip)]);

        await host.RunUntilIdleAsync();

        var items = await host.ItemsAsync(ingestion.Id);
        items.Where(i => i.ParentItemId is not null).Select(i => i.FileName).ShouldBe(["small.txt"]);
        items.Single(i => i.ParentItemId is null).DispositionReason.ShouldBe("1 file(s) unpacked; 1 skipped over the entry size or compression limit");
    }

    [Fact]
    public async Task Bounded_copy_returns_the_bytes_up_to_the_limit_and_null_beyond_it()
    {
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var cancellation = TestContext.Current.CancellationToken;

        (await BoundedCopy.ReadAsync(new MemoryStream(content), content.Length, cancellation)).ShouldBe(content, "exactly the limit is allowed");
        (await BoundedCopy.ReadAsync(new MemoryStream(content), content.Length + 1, cancellation)).ShouldBe(content);
        (await BoundedCopy.ReadAsync(new MemoryStream(content), content.Length - 1, cancellation)).ShouldBeNull();
        (await BoundedCopy.ReadAsync(new MemoryStream([]), 0, cancellation)).ShouldBe([]);
        (await BoundedCopy.ReadAsync(new OneByteAtATimeStream(content), 50_000, cancellation)).ShouldBeNull("the ceiling holds for a stream that trickles");
    }

    /// <summary>Reads one byte per call, so the copy sees many small chunks instead of one read.</summary>
    private sealed class OneByteAtATimeStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
