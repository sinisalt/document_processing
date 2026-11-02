using DocProc.Application.Abstractions;
using DocProc.Domain.Documents;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The P0 seams inside the running pipeline (docs/11 §23): the extract stage stores, through the outcome's tree,
/// exactly the JSON the parsed invoice serialises to, and the validate stage, now running the invoice rule list
/// through <see cref="InvoiceRuleAdapter"/>, stores exactly the flags <see cref="InvoiceValidator.Default"/> raises.
/// </summary>
public sealed class P0SeamParityTests
{
    private const string XmlMime = "application/xml";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_e_invoice_is_stored_and_flagged_exactly_as_before_the_seams(bool broken)
    {
        await using var host = await PipelineTestHost.StartAsync();
        var xml = PipelineTestHost.Fixture("peppol-bis3-invoice.xml");
        if (broken)
        {
            // A gross that does not add up and an issue date years back: the coded rules raise flags and it goes to review.
            var text = System.Text.Encoding.UTF8.GetString(xml)
                .Replace("<cbc:TaxInclusiveAmount currencyID=\"EUR\">437.99<", "<cbc:TaxInclusiveAmount currencyID=\"EUR\">499.99<", StringComparison.Ordinal)
                .Replace("<cbc:IssueDate>2026-10-27<", "<cbc:IssueDate>2019-10-27<", StringComparison.Ordinal);
            xml = System.Text.Encoding.UTF8.GetBytes(text);
        }

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, xml)]);
        await host.RunUntilIdleAsync();
        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(broken ? DocumentStatus.NeedsReview : DocumentStatus.AutoApproved);

        var parser = host.Services.GetRequiredService<IEInvoiceParser>();
        var parsed = parser.Parse(xml, parser.Detect(xml)!);
        document.CanonicalJson.ShouldBe(CanonicalDocumentSerializer.Serialize(parsed), "the extract stage stores the tree byte for byte as the invoice serialised before");
        CanonicalDocumentSerializer.Options.ShouldBeSameAs(CanonicalJson.Options);

        var tenant = await host.WithDbAsync(db => db.Tenants.AsNoTracking().SingleAsync(t => t.Id == PipelineTestHost.TenantA));
        var context = new ValidationContext(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(host.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone)).DateTime),
            ValidationOptions.Default with { ConfidenceThreshold = tenant.Settings.AutoApproveConfidenceThreshold },
            new TenantContext(tenant.LegalName, tenant.VatId, tenant.DefaultCurrency, tenant.NameAliases));
        var expected = InvoiceValidator.Default.Validate(CanonicalDocumentSerializer.DeserializeInvoice(document.CanonicalJson!), context).Flags;
        var codes = expected.Select(f => f.Code).ToHashSet(StringComparer.Ordinal);
        if (broken)
        {
            codes.ShouldContain(Domain.Flags.FlagCodes.TotalsMismatch);
            codes.ShouldContain(Domain.Flags.FlagCodes.DateImplausible);
        }

        document.Flags.Where(f => codes.Contains(f.Code)).Select(f => $"{f.Code}|{f.Field}|{f.Message}").Order(StringComparer.Ordinal)
            .ShouldBe(expected.Select(f => $"{f.Code}|{f.Field}|{f.Message}").Order(StringComparer.Ordinal));
    }
}
