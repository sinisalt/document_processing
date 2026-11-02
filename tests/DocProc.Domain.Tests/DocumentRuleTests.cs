using System.Text.Json;
using DocProc.Domain.Common;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.Validation;
using static DocProc.Domain.Tests.InvoiceFixtures;

namespace DocProc.Domain.Tests;

/// <summary>
/// The rule seam of docs/11 §11: the coded invoice rules run as rule kinds through <see cref="InvoiceRuleAdapter"/>,
/// and the rule list of a coded invoice type raises exactly the flags <see cref="InvoiceValidator.Default"/> raises,
/// in the same order, whether the view holds the typed invoice or the stored JSON.
/// </summary>
public sealed class DocumentRuleTests
{
    /// <summary>One invoice per flag the coded rules can raise, plus a clean one.</summary>
    public static TheoryData<string> Cases => [.. Variants.Keys];

    private static readonly Dictionary<string, Func<Invoice>> Variants = new(StringComparer.Ordinal)
    {
        ["valid"] = Valid,
        ["credit note without reference"] = () => Valid() with { Kind = FieldValue.Extracted(InvoiceKind.CreditNote) },
        ["missing number and date"] = () => Valid() with { InvoiceNumber = FieldValue<DocumentIdentifier>.Missing(), IssueDate = FieldValue<DateOnly>.Missing() },
        ["missing supplier"] = () => Valid() with { Supplier = FieldValue<Party>.Missing() },
        ["subtotal off"] = () => Valid() with { SubtotalNet = FieldValue.Extracted(Eur(140.00m)) },
        ["gross off"] = () => Valid() with { TotalGross = FieldValue.Extracted(Eur(170.00m)) },
        ["tax row off"] = () => Valid() with { TaxBreakdown = [new TaxBreakdownLine(7m, Eur(100.00m), Eur(8.00m), TaxCategory.Standard), new TaxBreakdownLine(19m, Eur(50.00m), Eur(9.50m), TaxCategory.Standard)] },
        ["line arithmetic"] = () =>
        {
            var invoice = Valid();
            var lines = invoice.Lines.ToList();
            lines[0] = lines[0] with { UnitPrice = FieldValue.Extracted(Eur(9.50m)) };
            return invoice with { Lines = lines };
        },
        ["old issue date"] = () => Valid() with { IssueDate = FieldValue.Extracted(new DateOnly(2020, 1, 1)), DueDate = FieldValue<DateOnly>.Missing() },
        ["due before issue"] = () => Valid() with { DueDate = FieldValue.Extracted(new DateOnly(2026, 10, 1)) },
        ["currency inferred"] = () => Valid() with { Currency = FieldValue.Inferred("EUR") },
        ["currency not a code"] = () => Valid() with { Currency = FieldValue.Extracted("Euro") },
        ["mixed currencies"] = () => Valid() with { TotalGross = FieldValue.Extracted(new Money(166.50m, "USD")) },
        ["low confidence"] = () => Valid() with { TotalGross = FieldValue.Extracted(Eur(166.50m), confidence: 0.4) },
        ["customer vat differs"] = () => Valid() with { Customer = FieldValue.Extracted(new Party("Gasthaus zur Post GmbH", VatId: "DE999999999")) },
        ["customer name differs"] = () => Valid() with { Customer = FieldValue.Extracted(new Party("Pizzeria Napoli")) },
        ["custom properties"] = () => Valid() with { Extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["x_costCentre"] = JsonSerializer.SerializeToElement("K-4711") } },
    };

    [Fact]
    public void The_adapters_wrap_the_default_rules_one_for_one_and_in_order()
    {
        InvoiceRuleAdapter.All.Select(a => a.Rule).ShouldBe(InvoiceValidator.Default.Rules);
        InvoiceRuleAdapter.All.Select(a => a.Kind).ShouldBe(
        [
            InvoiceRuleKinds.Required, InvoiceRuleKinds.Currency, InvoiceRuleKinds.Totals, InvoiceRuleKinds.TaxBreakdown,
            InvoiceRuleKinds.LineArithmetic, InvoiceRuleKinds.DatePlausibility, InvoiceRuleKinds.CustomerMatch, InvoiceRuleKinds.Confidence,
        ]);
        StandardRuleSets.Invoice.Select(r => r.Kind).ShouldBe(InvoiceRuleAdapter.All.Select(a => a.Kind));
        StandardRuleSets.Invoice.ShouldAllBe(r => r.Params.Values.Count == 0 && r.Id == r.Kind);
        RuleRegistry.Default.Kinds.Order(StringComparer.Ordinal).ShouldBe(InvoiceRuleAdapter.All.Select(a => a.Kind).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_invoice_rule_list_raises_exactly_the_flags_of_the_invoice_validator(string variant)
    {
        var invoice = Variants[variant]();
        var expected = Describe(InvoiceValidator.Default.Validate(invoice, Context));

        Describe(DocumentValidator.Default.Validate(DocumentView.ForInvoice(invoice), StandardRuleSets.Invoice, Context)).ShouldBe(expected);

        // As the validate stage reads it: the stored JSON, deserialised once for all eight kinds.
        var json = JsonSerializer.Serialize(invoice, CanonicalJson.Options);
        var stored = DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.Invoice, json), StandardRuleSets.Invoice, Context);
        Describe(stored).ShouldBe(Describe(InvoiceValidator.Default.Validate(JsonSerializer.Deserialize<Invoice>(json, CanonicalJson.Options)!, Context)));
        Describe(DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.CreditNote, json), StandardRuleSets.Invoice, Context)).ShouldBe(Describe(stored));
    }

    [Fact]
    public void The_variants_cover_every_flag_the_coded_rules_raise()
    {
        var raised = Variants.Values.SelectMany(v => InvoiceValidator.Default.Validate(v(), Context).Flags.Select(f => f.Code)).ToHashSet(StringComparer.Ordinal);
        string[] coded =
        [
            FlagCodes.MissingRequired, FlagCodes.InvalidValue, FlagCodes.TotalsMismatch, FlagCodes.TaxMismatch, FlagCodes.LineArithmetic,
            FlagCodes.DateImplausible, FlagCodes.CustomerMismatch, FlagCodes.LowConfidence, FlagCodes.CurrencyInferred,
        ];
        coded.Where(code => !raised.Contains(code)).ShouldBeEmpty();
    }

    [Fact]
    public void Eligibility_for_auto_approval_is_the_same_as_the_validators()
    {
        foreach (var (name, build) in Variants)
        {
            var invoice = build();
            DocumentValidator.Default.Validate(DocumentView.ForInvoice(invoice), StandardRuleSets.Invoice, Context).IsEligibleForAutoApproval
                .ShouldBe(InvoiceValidator.Default.Validate(invoice, Context).IsEligibleForAutoApproval, name);
        }
    }

    [Theory]
    [InlineData(DocumentType.Invoice, true)]
    [InlineData(DocumentType.CreditNote, true)]
    [InlineData(DocumentType.PurchaseOrder, false)]
    [InlineData(DocumentType.DeliveryNote, false)]
    [InlineData(DocumentType.Statement, false)]
    [InlineData(DocumentType.Unknown, false)]
    public void Only_the_invoice_types_have_a_rule_list_and_an_invoice_projection(DocumentType type, bool invoice)
    {
        (StandardRuleSets.ForLegacyType(type) is not null).ShouldBe(invoice);
        InvoiceProjection.IsInvoiceCompatible(type).ShouldBe(invoice);
    }

    [Fact]
    public void A_coded_invoice_kind_raises_nothing_on_a_view_without_an_invoice()
    {
        var json = JsonSerializer.Serialize(Valid() with { SubtotalNet = FieldValue.Extracted(Eur(1m)) }, CanonicalJson.Options);
        var deliveryNote = DocumentView.FromJson(DocumentType.DeliveryNote, json);
        InvoiceProjection.Of(deliveryNote).ShouldBeNull();
        DocumentValidator.Default.Validate(deliveryNote, StandardRuleSets.Invoice, Context).Flags.ShouldBeEmpty();
        DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.Invoice, null), StandardRuleSets.Invoice, Context).Flags.ShouldBeEmpty();
    }

    [Fact]
    public void The_view_reads_the_tree_and_the_invoice_once()
    {
        var invoice = Valid();
        var view = DocumentView.ForInvoice(invoice);
        InvoiceProjection.Of(view).ShouldBeSameAs(invoice);
        view.Root.ShouldNotBeNull()["invoiceNumber"]!["value"]!["verbatim"]!.GetValue<string>().ShouldBe("RE-2026-0042");
        view.Root.ShouldBeSameAs(view.Root);

        var stored = DocumentView.FromJson(DocumentType.Invoice, JsonSerializer.Serialize(invoice, CanonicalJson.Options));
        InvoiceProjection.Of(stored).ShouldBeSameAs(InvoiceProjection.Of(stored));
        stored.Root!["currency"]!["value"]!.GetValue<string>().ShouldBe("EUR");
    }

    [Fact]
    public void A_malformed_tree_fails_as_the_validator_did()
    {
        Should.Throw<JsonException>(() => DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.Invoice, "null"), StandardRuleSets.Invoice, Context));
        Should.Throw<JsonException>(() => DocumentValidator.Default.Validate(DocumentView.FromJson(DocumentType.Invoice, "{\"lines\": 5}"), StandardRuleSets.Invoice, Context));
    }

    [Fact]
    public void An_unregistered_kind_is_a_platform_defect_and_parameters_reach_the_rule()
    {
        var unknown = new RuleReference("r1", "sumEquals", RuleParams.None);
        Should.Throw<InvalidOperationException>(() => DocumentValidator.Default.Validate(DocumentView.ForInvoice(Valid()), [unknown], Context)).Message.ShouldContain("sumEquals");

        var probe = new ProbeRule();
        var parameters = new RuleParams(new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["path"] = JsonSerializer.SerializeToElement("lines") });
        var result = new DocumentValidator(new RuleRegistry([probe])).Validate(DocumentView.ForInvoice(Valid()), [new RuleReference("r_probe", ProbeRule.Name, parameters)], Context);
        result.Flags.ShouldHaveSingleItem().Field.ShouldBe("lines");
        probe.Seen.ShouldBe(DocumentType.Invoice);
        Should.Throw<ArgumentOutOfRangeException>(() => InvoiceRuleAdapter.KindOf(new ProbeInvoiceRule()));
    }

    private static List<string> Describe(ValidationResult result) =>
        [.. result.Flags.Select(f => $"{f.Code}|{f.Severity}|{f.Field}|{f.Message}|{string.Join(",", (f.Params ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))}")];

    private sealed class ProbeRule : IDocumentRule
    {
        public const string Name = "test.probe";

        public DocumentType? Seen { get; private set; }

        public string Kind => Name;

        public IEnumerable<DocumentFlag> Evaluate(DocumentView view, RuleParams parameters, ValidationContext context)
        {
            Seen = view.Type;
            parameters.TryGet("path", out var path).ShouldBeTrue();
            yield return new DocumentFlag("X_PROBE", FlagSeverity.Info, "probe", path.GetString());
        }
    }

    private sealed class ProbeInvoiceRule : IInvoiceRule
    {
        public IEnumerable<DocumentFlag> Evaluate(Invoice invoice, ValidationContext context) => [];
    }
}
