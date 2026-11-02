using DocProc.Application.Configuration;
using DocProc.Definitions;
using DocProc.Domain.Documents;
using DocProc.Domain.Flags;
using DocProc.Domain.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Pipeline.Tests;

/// <summary>
/// The validate stage takes its rule list and its auto-approval switch from the definition that
/// <see cref="IEffectiveConfigResolver"/> resolves (docs/11 §7.3, §11). With the P0 resolver that is the adopted standard
/// template, and the outcome is today's (<see cref="P0SeamParityTests"/>); a resolver that returns a different
/// definition changes the outcome, which shows the stage really reads it.
/// </summary>
public sealed class EffectiveConfigValidateTests
{
    private const string XmlMime = "application/xml";

    [Fact]
    public async Task The_pipeline_resolves_definitions_through_the_type_catalogue()
    {
        await using var host = await PipelineTestHost.StartAsync();

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEffectiveConfigResolver>().ShouldBeOfType<Infrastructure.Catalogue.CatalogueConfigResolver>();
    }

    [Fact]
    public async Task On_the_standard_template_a_clean_e_invoice_is_auto_approved_as_before()
    {
        await using var host = await PipelineTestHost.StartAsync();

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.AutoApproved);

        // The Extract stage pinned the tenant's adopted Invoice v1 (docs/11 §5.7); the development seed adopted it.
        Guid? adopted = null;
        await host.WithDbAsync(async db => adopted = await db.DocumentTypeVersions.IgnoreQueryFilters()
            .Where(v => v.TenantId == PipelineTestHost.TenantA && v.Definition!.Key == "Invoice" && v.Version == 1)
            .Select(v => (Guid?)v.Id)
            .SingleAsync());
        document.DocumentTypeVersionId.ShouldNotBeNull().ShouldBe(adopted!.Value);
    }

    [Fact]
    public async Task A_definition_without_auto_approval_sends_the_same_clean_e_invoice_to_review()
    {
        var resolver = new ChangedInvoiceResolver(t => t with { Processing = t.Processing with { Review = new ReviewPolicy { AutoApprove = false } } });
        await using var host = await PipelineTestHost.StartAsync(services: s => s.AddSingleton<IEffectiveConfigResolver>(resolver));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        resolver.Requests.ShouldContain(r => r.TenantId == PipelineTestHost.TenantA && r.Type == DocumentTypeKey.Invoice);
    }

    [Fact]
    public async Task The_stage_runs_the_rule_list_of_the_definition_and_nothing_else()
    {
        // Only the totals rule: the broken invoice still gets TOTALS_MISMATCH, but no DATE_IMPLAUSIBLE any more.
        var resolver = new ChangedInvoiceResolver(t => t with { Rules = [.. t.Rules.Where(r => r.Kind == InvoiceRuleKinds.Totals)] });
        await using var host = await PipelineTestHost.StartAsync(services: s => s.AddSingleton<IEffectiveConfigResolver>(resolver));
        var text = System.Text.Encoding.UTF8.GetString(PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))
            .Replace("<cbc:TaxInclusiveAmount currencyID=\"EUR\">437.99<", "<cbc:TaxInclusiveAmount currencyID=\"EUR\">499.99<", StringComparison.Ordinal)
            .Replace("<cbc:IssueDate>2026-10-27<", "<cbc:IssueDate>2019-10-27<", StringComparison.Ordinal);

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, System.Text.Encoding.UTF8.GetBytes(text))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview);
        var codes = document.Flags.Select(f => f.Code).ToList();
        codes.ShouldContain(FlagCodes.TotalsMismatch);
        codes.ShouldNotContain(FlagCodes.DateImplausible);
    }

    [Fact]
    public async Task A_definition_with_an_empty_rule_list_is_not_validated_and_goes_to_review()
    {
        var resolver = new ChangedInvoiceResolver(t => t with { Rules = [] });
        await using var host = await PipelineTestHost.StartAsync(services: s => s.AddSingleton<IEffectiveConfigResolver>(resolver));

        var ingestion = await host.IngestAsync(PipelineTestHost.TenantA, [new IngestFile("rechnung.xml", XmlMime, PipelineTestHost.Fixture("peppol-bis3-invoice.xml"))]);
        await host.RunUntilIdleAsync();

        var document = await host.SingleDocumentAsync(ingestion.Id);
        document.Status.ShouldBe(DocumentStatus.NeedsReview, "auto-approval needs at least one rule (docs/11 §11, layer 3)");
    }

    /// <summary>The standard resolver, with the invoice template changed by <paramref name="change"/>.</summary>
    private sealed class ChangedInvoiceResolver(Func<TypeDefinition, TypeDefinition> change) : IEffectiveConfigResolver
    {
        public List<EffectiveConfigRequest> Requests { get; } = [];

        public async ValueTask<EffectiveConfig> ResolveAsync(EffectiveConfigRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            var config = await StandardTemplateConfigResolver.Instance.ResolveAsync(request, cancellationToken);
            if (request.Type != DocumentTypeKey.Invoice || config.Type is not { } type)
            {
                return config;
            }

            var changed = change(type.Definition);
            return config with { Type = new EffectiveType(type.TypeKey, changed, DefinitionOrigin.Copied, type.TemplateKey, type.TemplateVersion) };
        }
    }
}
