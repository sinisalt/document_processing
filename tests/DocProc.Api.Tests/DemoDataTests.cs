using DocProc.Api.Dev;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Llm;
using DocProc.Llm.Providers;
using DocProc.Pipeline.Demo;
using DocProc.Pipeline.Queue;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocProc.Api.Tests;

/// <summary>
/// The demo data of the development mode (docs/09 "Demo data"): the API booted with the Development environment, so
/// <c>appsettings.Development.json</c> applies as under <c>dotnet run</c> (SQLite, the seed, <c>SeedDemoDocuments</c>,
/// the replay directory <c>fixtures/demo/llm-replay</c>); only the database, blob and secret folders move to a
/// temporary directory. The committed fixtures must take every demo document to its designed end state with no LLM key.
/// </summary>
public sealed class DemoDataTests
{
    private const string MetroInvoice = "METRO_Rechnung_RG-2026-882140.xml";
    private const string MetroCreditNote = "METRO_Gutschrift_GS-2026-31207.xml";
    private const string HoffmannInvoice = "Getraenke-Hoffmann_RE-2026-55318.xml";
    private const string BioHofXmlInvoice = "BioHof-Mueller_Rechnung_2026-118.xml";
    private const string BioHofPdfInvoice = "BioHof-Mueller_Rechnung_2026-131.pdf";
    private const string BioHofDeliveryNote = "BioHof-Mueller_Lieferung_L-2026-0457.pdf";
    private const string KonzumInvoice = "Konzum_Racun_04518-2026-P12.xml";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Development_mode_processes_the_demo_documents_to_their_designed_end_state_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), "docproc-demo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Snapshot first;
            using (var factory = new DemoModeFactory(directory))
            {
                await SettleAsync(factory);
                first = await SnapshotAsync(factory);
                await AssertDesignedEndStateAsync(factory);
            }

            // A second start-up on the same database and blobs finds every demo row and file and adds nothing.
            using (var again = new DemoModeFactory(directory))
            {
                await SettleAsync(again);
                (await SnapshotAsync(again)).ShouldBe(first);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        if (!DemoData.IsFixtureCapturePlatform)
        {
            Assert.Skip(
                $"The delivery-note assertions ran only on {DemoData.FixtureCapturePlatform}: its classification fixture hashes the first page as PDFium " +
                $"renders it there, and this machine ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) rasterises differently, so the " +
                "delivery note gets a replay miss. Everything else was checked; see docs/09-local-development.md, \"Demo data\".");
        }
    }

    [Fact]
    public void Demo_members_are_the_customer_dev_users()
    {
        var customers = DevUsers.All.Where(u => u.TenantId is not null).OrderBy(u => u.Username, StringComparer.Ordinal).ToList();
        var members = DemoData.Members.OrderBy(m => m.Username, StringComparer.Ordinal).ToList();

        members.Select(m => (m.Username, m.Email, m.DisplayName, m.TenantId, m.UserId, Role: m.Role.ToString()))
            .ShouldBe(customers.Select(u => (u.Username, u.Email, u.DisplayName, u.TenantId!.Value, UserId: u.Subject, Role: u.Roles.Single())));
    }

    [Fact]
    public void Demo_documents_are_refused_in_production()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "SqlServer",
            ["ConnectionStrings:DocProc"] = "Server=sql.example.test;Database=DocProc;User Id=app;Password=unused;TrustServerCertificate=true",
            ["Persistence:SeedDemoDocuments"] = "true",
        });

        Should.Throw<InvalidOperationException>(() => builder.Services.TryAddDocProcPersistence(builder.Configuration, builder.Environment, migrateByDefault: false))
            .Message.ShouldContain("SeedDemoDocuments");
        Should.Throw<InvalidOperationException>(() => builder.AddDocProcDemoData())
            .Message.ShouldContain("SeedDemoDocuments");
    }

    private static async Task AssertDesignedEndStateAsync(DemoModeFactory factory)
    {
        await using var db = factory.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        var documents = await db.Documents.AsNoTracking().Include(d => d.Flags).Include(d => d.IngestionItem).Include(d => d.ReviewTasks).ToListAsync(Ct);
        documents.Select(d => d.IngestionItem!.FileName).Order(StringComparer.Ordinal).ToList().ShouldBe(
            [BioHofDeliveryNote, BioHofXmlInvoice, BioHofPdfInvoice, HoffmannInvoice, KonzumInvoice, MetroCreditNote, MetroInvoice],
            "one document per demo file, none split or failed");
        Document Of(string file) => documents.Single(d => d.IngestionItem!.FileName == file);

        // The clean e-invoices: structured path, supplier matched, no blocking flag, auto-approved and published.
        foreach (var (file, supplier, type, tenant) in new[]
                 {
                     (MetroInvoice, DevSeed.MetroDeutschland, DocumentType.Invoice, DevSeed.TenantA),
                     (MetroCreditNote, DevSeed.MetroDeutschland, DocumentType.CreditNote, DevSeed.TenantA),
                     (HoffmannInvoice, DevSeed.GetraenkeHoffmann, DocumentType.Invoice, DevSeed.TenantA),
                     (KonzumInvoice, DevSeed.KonzumPlus, DocumentType.Invoice, DevSeed.TenantB),
                 })
        {
            Of(file).ShouldSatisfyAllConditions(
                d => d.TenantId.ShouldBe(tenant, file),
                d => d.Type.ShouldBe(type, file),
                d => d.ExtractionMethod.ShouldBe(ExtractionMethod.StructuredXml, file),
                d => d.SupplierId.ShouldBe(supplier, file),
                d => d.Status.ShouldBe(DocumentStatus.AutoApproved, $"{file}: {string.Join(", ", d.Flags.Select(f => f.Code))}"),
                d => d.Flags.Where(f => f.ResolvedAt is null && f.Severity == FlagSeverity.Blocking).ShouldBeEmpty(file));
        }

        (await db.OutboxMessages.AsNoTracking().CountAsync(Ct)).ShouldBe(4, "every auto-approved document is published");

        // The arithmetic error: lines 230.00, printed subtotal 240.00.
        Of(BioHofXmlInvoice).ShouldSatisfyAllConditions(
            d => d.SupplierId.ShouldBe(DevSeed.BioHofMueller),
            d => d.Status.ShouldBe(DocumentStatus.NeedsReview),
            d => d.Flags.Where(f => f.ResolvedAt is null && f.Severity == FlagSeverity.Blocking).Select(f => f.Code).ShouldBe([FlagCodes.TotalsMismatch]),
            d => d.ReviewTasks.Single().Status.ShouldBe(ReviewTaskStatus.Open));

        // The two-page PDF invoice, extracted from the replay fixture with its hand-authored values.
        Of(BioHofPdfInvoice).ShouldSatisfyAllConditions(
            d => d.PageCount.ShouldBe(2),
            d => d.Type.ShouldBe(DocumentType.Invoice),
            d => d.TypeSource.ShouldBe(TypeSource.Rule),
            d => d.SupplierId.ShouldBe(DevSeed.BioHofMueller),
            d => d.ExtractionMethod.ShouldBe(ExtractionMethod.Llm),
            d => d.DocumentNumber.ShouldBe("2026-131"),
            d => d.IssueDate.ShouldBe(new DateOnly(2026, 10, 28)),
            d => d.Currency.ShouldBe("EUR"),
            d => d.TotalGross.ShouldBe(461.60m),
            d => d.Costs.LlmProvider.ShouldBe(nameof(LlmProviderKind.Replay)),
            d => d.Costs.LlmModel.ShouldBe("claude-opus-5-5"),
            d => d.Costs.LlmOutputTokens.ShouldBe(1712),
            // The due date was inferred from "14 Tagen", not printed: the one field a reviewer has to confirm.
            d => d.Flags.Where(f => f.ResolvedAt is null && f.Code == FlagCodes.LowConfidence).Select(f => f.Field).ShouldContain("dueDate"),
            d => d.Flags.Where(f => f.ResolvedAt is null && f.Severity == FlagSeverity.Blocking).ShouldBeEmpty(),
            d => d.Status.ShouldBe(DocumentStatus.NeedsReview));
        var canonical = CanonicalDocumentSerializer.DeserializeInvoice(Of(BioHofPdfInvoice).CanonicalJson!);
        canonical.Lines.Count.ShouldBe(10);
        canonical.SubtotalNet.Value!.Amount.ShouldBe(431.40m);

        // The delivery note: no keyword type, so the LLM classifier (replayed) decided; no extraction path yet. Its
        // fixture hashes the page PDFium renders on linux-x64; elsewhere it is a replay miss and the test is skipped.
        if (DemoData.IsFixtureCapturePlatform)
        {
            Of(BioHofDeliveryNote).ShouldSatisfyAllConditions(
                d => d.Type.ShouldBe(DocumentType.DeliveryNote),
                d => d.TypeSource.ShouldBe(TypeSource.Llm),
                d => d.TypeConfidence.ShouldBe(0.91),
                d => d.SupplierId.ShouldBe(DevSeed.BioHofMueller),
                d => d.Flags.Where(f => f.ResolvedAt is null).Select(f => f.Code).ShouldContain(FlagCodes.UnsupportedType),
                d => d.Status.ShouldBe(DocumentStatus.NeedsReview));
        }

        // The replay provider served both calls; the synthetic usage fills the charts for the last two weeks.
        var replay = await db.LlmProviderConfigs.AsNoTracking().SingleAsync(c => c.Id == DemoData.PlatformReplayConfig, Ct);
        replay.ShouldSatisfyAllConditions(
            c => c.Kind.ShouldBe(LlmProviderKind.Replay),
            c => c.Scope.ShouldBe(ConfigScope.Platform),
            c => c.Status.ShouldBe(ProviderConfigStatus.Active));
        var usage = await db.LlmUsage.AsNoTracking().ToListAsync(Ct);
        var replayed = usage.Where(u => u.ConfigId == DemoData.PlatformReplayConfig).Select(u => (u.Stage, u.Error)).OrderBy(u => u.Stage).ToList();
        if (DemoData.IsFixtureCapturePlatform)
        {
            replayed.ShouldBe([(LlmStage.Classify, (LlmErrorKind?)null), (LlmStage.Extract, null)]);
        }
        else
        {
            replayed.Where(u => u.Stage == LlmStage.Extract).ShouldBe([(LlmStage.Extract, (LlmErrorKind?)null)]);
        }
        var synthetic = usage.Where(u => u.ProviderRequestId?.StartsWith(DemoData.UsageMarker, StringComparison.Ordinal) == true).ToList();
        synthetic.ShouldNotBeEmpty();
        synthetic.Select(u => u.TenantId).Distinct().ShouldBe([DevSeed.TenantA, DevSeed.TenantB], ignoreOrder: true);
        var now = DateTimeOffset.UtcNow;
        synthetic.ShouldAllBe(u => u.At <= now && u.At > now.AddDays(-DemoData.UsageDays));
        synthetic.Select(u => DateOnly.FromDateTime(u.At.UtcDateTime)).Distinct().Count().ShouldBeGreaterThanOrEqualTo(10, "usage spread over the two weeks");
        synthetic.Count(u => u.Error is not null).ShouldBe(2);

        var members = await db.TenantMembers.AsNoTracking().ToListAsync(Ct);
        members.Select(m => (m.TenantId, m.UserId, m.Role)).ShouldBe(
            DemoData.Members.Select(m => (m.TenantId, m.UserId, m.Role)),
            ignoreOrder: true);
    }

    private static async Task SettleAsync(DemoModeFactory factory)
    {
        _ = factory.Server;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        await factory.Services.GetRequiredService<InProcessPipelineQueue>().WaitForIdleAsync(cts.Token);
    }

    private static async Task<Snapshot> SnapshotAsync(DemoModeFactory factory)
    {
        await using var db = factory.Services.GetRequiredService<ISystemDbContextFactory>().CreatePlatformScoped();
        return new Snapshot(
            await db.Ingestions.CountAsync(Ct),
            await db.Documents.CountAsync(Ct),
            await db.TenantMembers.CountAsync(Ct),
            await db.LlmUsage.CountAsync(Ct),
            await db.LlmProviderConfigs.CountAsync(Ct),
            await db.OutboxMessages.CountAsync(Ct));
    }

    private sealed record Snapshot(int Ingestions, int Documents, int Members, int UsageRows, int ProviderConfigs, int OutboxMessages);

    /// <summary>The API under the Development environment with only its data folders redirected; the folder outlives the factory.</summary>
    private sealed class DemoModeFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Persistence:SqlitePath", Path.Combine(directory, "docproc.db"));
            builder.UseSetting("Blobs:RootPath", Path.Combine(directory, "blobs"));
            builder.UseSetting("Secrets:Path", Path.Combine(directory, "secrets"));
            builder.UseSetting("Authentication:Oidc:Authority", string.Empty);
            builder.UseSetting("Authentication:Dev:SigningKeyPath", string.Empty);
        }
    }
}
