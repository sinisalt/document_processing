using DocProc.Application.Abstractions;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The ingestion actor split (docs/11 §6.3, §7.1): the mail sender and the uploading actor get their own columns,
/// while the legacy <c>SourceFrom</c> is written exactly as before for one more release.
/// </summary>
public sealed class UploadIntakeSourceTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<StoredUpload> OneFile() =>
    [
        new StoredUpload(
            new UploadFile("invoice.pdf", "application/pdf", ItemKind.Pdf, () => new MemoryStream([1])),
            new BlobRef("raw", $"{Tenant}/abc", new string('a', 64), 1, "application/pdf")),
    ];

    [Fact]
    public void An_upload_records_its_actor_and_no_sender()
    {
        var ingestion = UploadIntake.CreateIngestion(Tenant, Guid.NewGuid(), SourceMetadata.Upload("reviewer-a"), OneFile(), Now);

        ingestion.SubmittedBy.ShouldBe("reviewer-a");
        ingestion.SourceSender.ShouldBeNull();
        ingestion.SourceFrom.ShouldBe("reviewer-a", "the legacy column keeps what it held for uploads");
        ingestion.SourceSubject.ShouldBeNull();
    }

    [Fact]
    public void A_mail_records_its_lower_cased_sender_and_no_actor()
    {
        var ingestion = UploadIntake.CreateIngestion(Tenant, Guid.NewGuid(), new SourceMetadata(Sender: " Rechnungsversand@Nordwind.example ", Subject: "Gutschrift GS-2026-0042"), OneFile(), Now);

        ingestion.SourceSender.ShouldBe("rechnungsversand@nordwind.example");
        ingestion.SubmittedBy.ShouldBeNull();
        ingestion.SourceFrom.ShouldBe(" Rechnungsversand@Nordwind.example ", "the legacy column is written as before, verbatim");
        ingestion.SourceSubject.ShouldBe("Gutschrift GS-2026-0042");
    }

    [Fact]
    public void Values_are_fitted_to_their_columns()
    {
        var ingestion = UploadIntake.CreateIngestion(
            Tenant,
            Guid.NewGuid(),
            new SourceMetadata(Sender: new string('s', 500) + "@x.example", SubmittedBy: new string('a', 300), Subject: "   "),
            OneFile(),
            Now);

        ingestion.SourceSender!.Length.ShouldBe(UploadIntake.MaxSourceSenderLength);
        ingestion.SubmittedBy!.Length.ShouldBe(UploadIntake.MaxSubmittedByLength);
        ingestion.SourceFrom!.Length.ShouldBe(UploadIntake.MaxSourceFromLength);
        ingestion.SourceSubject.ShouldBeNull();
    }

    [Fact]
    public void No_source_at_all_leaves_every_column_empty()
    {
        var ingestion = UploadIntake.CreateIngestion(Tenant, Guid.NewGuid(), new SourceMetadata(), OneFile(), Now);

        ingestion.SourceFrom.ShouldBeNull();
        ingestion.SourceSender.ShouldBeNull();
        ingestion.SubmittedBy.ShouldBeNull();
    }
}
