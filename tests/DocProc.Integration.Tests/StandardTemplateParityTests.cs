using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Definitions;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Flags;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Domain.Validation;
using DocProc.Domain.Validation.Rules;
using DocProc.Infrastructure.Classification;
using DocProc.Infrastructure.Persistence;
using DocProc.Llm.Schema;

namespace DocProc.Integration.Tests;

/// <summary>
/// The seeded standard templates are today's types as data (docs/11 §5.1, §22 "Parity and compatibility"): their
/// field trees are exactly the canonical records' paths, and everything the running code still takes from coded
/// tables (the rule lists, the role bindings, the invoice-only auto-approval, the required fields, the duplicate check,
/// the extraction path and the keyword classifier's phrases) is what the templates say. A change to a record or a
/// table without the template, or the reverse, fails here.
/// </summary>
public sealed class StandardTemplateParityTests
{
    public static TheoryData<DocumentType> LegacyTypes() => [.. Enum.GetValues<DocumentType>().Where(t => t != DocumentType.Unknown)];

    public static TheoryData<DocumentType, Type> RecordTypes() => new()
    {
        { DocumentType.Invoice, typeof(Invoice) },
        { DocumentType.CreditNote, typeof(Invoice) },
        { DocumentType.PurchaseOrder, typeof(PurchaseOrder) },
        { DocumentType.DeliveryNote, typeof(DeliveryNote) },
    };

    [Theory]
    [InlineData("std.invoice")]
    [InlineData("std.credit-note")]
    public void The_invoice_templates_envelope_paths_equal_Invoice_EnumerateAllFields(string key)
    {
        var template = StandardTemplates.Find(key).ShouldNotBeNull();
        var invoice = new Invoice
        {
            OrderReferences = [FieldValue<DocumentIdentifier>.Missing(), FieldValue<DocumentIdentifier>.Missing()],
            DeliveryNoteReferences = [FieldValue<DocumentIdentifier>.Missing()],
            Lines = [new InvoiceLine { LineNumber = 1 }, new InvoiceLine { LineNumber = 2 }],
        };

        var recordPaths = invoice.EnumerateAllFields().Select(f => Regex.Replace(f.Path, @"\[\d+\]", "[*]")).Distinct(StringComparer.Ordinal).ToList();

        DefinitionPaths.EnvelopePaths(template).ShouldBe(recordPaths);
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void The_template_tree_is_the_record_tree_member_by_member(DocumentType type, Type record)
    {
        var template = StandardTemplates.ForLegacyType(type).ShouldNotBeNull();
        ReflectionFieldSchemaResolver.RootTypeFor(type).ShouldBe(record);

        CompareTree(record, template.Fields, template.Key);

        // The patcher's seam sees the same members: every template field is a member there, with the same shape.
        var root = ReflectionFieldSchemaResolver.Instance.RootFor(type).ShouldNotBeNull();
        foreach (var field in template.Fields)
        {
            var member = root.Member(field.Key).ShouldNotBeNull($"{template.Key}: {field.Key}");
            member.Kind.ShouldBe(field switch
            {
                { Kind: FieldKinds.Array } or { List: true } => FieldSchemaMemberKind.List,
                { Provenance: true } => FieldSchemaMemberKind.Field,
                _ => FieldSchemaMemberKind.Scalar,
            }, $"{template.Key}: {field.Key}");
        }
    }

    [Theory]
    [InlineData(DocumentType.Statement)]
    [InlineData(DocumentType.Reminder)]
    [InlineData(DocumentType.Other)]
    public void Types_without_canonical_data_have_templates_without_fields(DocumentType type)
    {
        ReflectionFieldSchemaResolver.RootTypeFor(type).ShouldBeNull();
        var template = StandardTemplates.ForLegacyType(type).ShouldNotBeNull();

        template.Fields.ShouldBeEmpty();
        template.Rules.ShouldBeEmpty();
        template.Processing.Extract.ShouldBeEmpty();
        template.Processing.Review.AutoApprove.ShouldBeFalse();
    }

    [Fact]
    public void Each_composite_kind_has_exactly_the_members_of_its_record()
    {
        var records = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [FieldKinds.Identifier] = typeof(DocumentIdentifier),
            [FieldKinds.Money] = typeof(Money),
            [FieldKinds.Period] = typeof(DatePeriod),
            [FieldKinds.Party] = typeof(Party),
            [FieldKinds.Address] = typeof(Address),
            [FieldKinds.TaxRow] = typeof(TaxBreakdownLine),
            [FieldKinds.PaymentTerms] = typeof(PaymentTerms),
            [FieldKinds.ChargeOrAllowance] = typeof(ChargeOrAllowance),
            [FieldKinds.TemperatureReading] = typeof(TemperatureReading),
        };
        records.Keys.ShouldBe(FieldKinds.CompositeMembers.Keys, ignoreOrder: true);

        foreach (var (kind, record) in records)
        {
            FieldKinds.CompositeMembers[kind].ShouldBe(Members(record).Select(p => JsonName(p)), kind);
        }
    }

    [Fact]
    public void A_fully_populated_invoice_as_stored_has_exactly_the_template_paths()
    {
        var json = (JsonObject)JsonNode.Parse(CanonicalDocumentSerializer.Serialize(FullInvoice()))!;
        var template = StandardTemplates.Find("std.invoice")!;

        Walk(json, template.Fields, "invoice");

        static void Walk(JsonObject node, IReadOnlyList<FieldDefinition> fields, string where)
        {
            node.Select(p => p.Key).ShouldBe(fields.Select(f => f.Key), ignoreOrder: true, $"{where}: stored properties");
            foreach (var field in fields)
            {
                var value = node[field.Key];
                if (field.Kind == FieldKinds.Array)
                {
                    foreach (var row in value!.AsArray())
                    {
                        Walk(row!.AsObject(), field.Item!.Fields, $"{where}.{field.Key}[*]");
                    }
                }
                else if (field.Provenance)
                {
                    var envelopes = field.List ? value!.AsArray().Select(e => e!) : [value!];
                    foreach (var envelope in envelopes)
                    {
                        envelope.AsObject().ContainsKey("status").ShouldBeTrue($"{where}.{field.Key} is a FieldValue envelope");
                    }
                }
                else
                {
                    if (value is JsonObject obj)
                    {
                        obj.ContainsKey("status").ShouldBeFalse($"{where}.{field.Key} is stored bare");
                    }

                    if (field.List)
                    {
                        value.ShouldBeOfType<JsonArray>($"{where}.{field.Key}");
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void The_rule_list_is_the_coded_rule_set_of_the_type(DocumentType type)
    {
        var effective = StandardTemplateConfigResolver.TypeFor(type).ShouldNotBeNull();
        var legacy = StandardRuleSets.ForLegacyType(type);

        if (legacy is null)
        {
            effective.Rules.ShouldBeEmpty();
        }
        else
        {
            effective.Rules.Select(r => r.Kind).ShouldBe(legacy.Select(r => r.Kind));
            effective.Rules.Select(r => r.Kind).ShouldBe(InvoiceRuleAdapter.All.Select(a => a.Kind));
            effective.Rules.ShouldAllBe(r => r.Params.Values.Count == 0, "the coded rules take no parameters");
        }
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void Only_the_invoice_family_may_be_auto_approved_as_today(DocumentType type)
    {
        var effective = StandardTemplateConfigResolver.TypeFor(type).ShouldNotBeNull();

        effective.AutoApprove.ShouldBe(InvoiceProjection.IsInvoiceCompatible(type));
        effective.Definition.Processing.Review.AutoApprove.ShouldBe(InvoiceProjection.IsInvoiceCompatible(type));
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void The_summary_roles_are_the_list_column_bindings(DocumentType type)
    {
        var effective = StandardTemplateConfigResolver.TypeFor(type).ShouldNotBeNull();

        effective.Roles.ShouldBe(RoleBindings.ForLegacyType(type) ?? new RoleBindings(null, null, null, null));
    }

    [Theory]
    [InlineData("std.invoice", InvoiceKind.Invoice)]
    [InlineData("std.credit-note", InvoiceKind.CreditNote)]
    public void The_required_fields_are_those_the_coded_required_rule_checks(string key, InvoiceKind kind)
    {
        var template = StandardTemplates.Find(key)!;
        var invoice = new Invoice { Kind = FieldValue<InvoiceKind>.Extracted(kind) };
        var flagged = new RequiredFieldsRule().Evaluate(invoice, ValidationContext.For(new DateOnly(2026, 11, 2)))
            .Where(f => f.Code == FlagCodes.MissingRequired)
            .Select(f => f.Field!)
            .Append("kind")
            .ToList();

        DefinitionPaths.Enumerate(template).Where(f => f.Field.Required).Select(f => f.Path).ShouldBe(flagged, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void Only_the_invoice_family_declares_the_duplicate_check_it_runs_today(DocumentType type)
    {
        var template = StandardTemplates.ForLegacyType(type)!;
        var invoiceFamily = InvoiceProjection.IsInvoiceCompatible(type);

        // ValidateHandler compares the matched supplier and the normalised document number, for validated types only.
        template.Processing.Validate.Any(s => s.Kind == "validate.duplicate").ShouldBe(invoiceFamily);
        if (invoiceFamily)
        {
            template.Roles.DuplicateKey.ShouldBe(["$supplierId", template.Roles.DocumentNumber!]);
            template.Processing.Validate.Select(s => s.Kind).ShouldBe(["validate.rules", "validate.duplicate"]);
        }
        else
        {
            template.Roles.DuplicateKey.ShouldBeNull();
            template.Processing.Validate.ShouldBeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void Only_the_invoice_family_lists_an_extraction_path_the_e_invoice_reader_then_the_invoice_adapter(DocumentType type)
    {
        var template = StandardTemplates.ForLegacyType(type)!;
        var adapter = $"{CanonicalSchemas.InvoiceExtraction.Name}@{CanonicalSchemas.InvoiceExtractionVersion}";

        if (InvoiceProjection.IsInvoiceCompatible(type))
        {
            template.Processing.Extract.Select(s => s.Kind).ShouldBe(["xml-einvoice", "llm"]);
            template.Processing.Extract[1].Params!["adapter"]!.GetValue<string>().ShouldBe(adapter);
            template.Llm.ShouldNotBeNull().Adapter.ShouldBe(adapter);
        }
        else
        {
            // ExtractHandler skips these types with UNSUPPORTED_TYPE.
            template.Processing.Extract.ShouldBeEmpty();
            template.Llm.ShouldBeNull();
        }
    }

    [Theory]
    [MemberData(nameof(LegacyTypes))]
    public void The_classification_phrases_are_the_keyword_rule_set(DocumentType type)
    {
        var template = StandardTemplates.ForLegacyType(type)!;
        var classification = template.Classification.ShouldNotBeNull();
        var blocks = new (KeywordKind Kind, IReadOnlyDictionary<string, IReadOnlyList<string>>? Block)[]
        {
            (KeywordKind.Title, classification.Keywords),
            (KeywordKind.Label, classification.Labels),
            (KeywordKind.DateLabel, classification.DateLabels),
            (KeywordKind.Negative, classification.Negatives),
        };

        foreach (var (kind, block) in blocks)
        {
            var expected = KeywordRuleSet.Rules
                .Where(r => r.Type == type && r.Kind == kind)
                .GroupBy(r => r.Language, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.SelectMany(r => r.Phrases).ToList(), StringComparer.Ordinal);
            var actual = (block ?? new Dictionary<string, IReadOnlyList<string>>()).ToDictionary(e => e.Key, e => e.Value.ToList(), StringComparer.Ordinal);

            actual.Keys.ShouldBe(expected.Keys, ignoreOrder: true, $"{template.Key} {kind}: languages");
            foreach (var (language, phrases) in expected)
            {
                actual[language].ShouldBe(phrases, $"{template.Key} {kind} {language}");
            }
        }

        KeywordRuleSet.Rules.Select(r => r.Kind).Distinct().ShouldBeSubsetOf(blocks.Select(b => b.Kind), "every keyword kind has a block in the format");
    }

    /// <summary>Compares a template level with a record type, member by member and in declaration order.</summary>
    private static void CompareTree(Type record, IReadOnlyList<FieldDefinition> fields, string where)
    {
        var members = Members(record);
        fields.Select(f => f.Key).ShouldBe(members.Select(m => JsonName(m)), $"{where}: members of {record.Name}");

        foreach (var (field, member) in fields.Zip(members))
        {
            var at = $"{where}.{field.Key}";
            var type = member.PropertyType;
            if (IsFieldValue(type))
            {
                field.Provenance.ShouldBeTrue(at);
                field.List.ShouldBeFalse(at);
                ExpectKind(field, type.GetGenericArguments()[0], at);
            }
            else if (ListElement(type) is { } element && IsFieldValue(element))
            {
                field.Provenance.ShouldBeTrue(at);
                field.List.ShouldBeTrue(at);
                ExpectKind(field, element.GetGenericArguments()[0], at);
            }
            else if (ListElement(type) is { } row && Members(row).Any(p => IsFieldValue(p.PropertyType)))
            {
                field.Kind.ShouldBe(FieldKinds.Array, at);
                CompareTree(row, field.Item.ShouldNotBeNull(at).Fields, $"{at}[*]");
            }
            else if (ListElement(type) is { } plainElement)
            {
                field.Provenance.ShouldBeFalse($"{at} is a bare list");
                field.List.ShouldBeTrue(at);
                field.ReadOnly.ShouldBeTrue($"{at}: no correction addresses a bare member");
                ExpectKind(field, plainElement, at);
            }
            else
            {
                field.Provenance.ShouldBeFalse($"{at} is stored bare");
                field.List.ShouldBeFalse(at);
                field.ReadOnly.ShouldBeTrue($"{at}: no correction addresses a bare member");
                ExpectKind(field, type, at);
            }
        }
    }

    private static void ExpectKind(FieldDefinition field, Type valueType, string at)
    {
        var type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (type.IsEnum && type != typeof(UnitOfMeasure))
        {
            field.Kind.ShouldBe(FieldKinds.Enum, at);
            field.Options.ShouldNotBeNull(at).Select(o => o.Code).ShouldBe(Enum.GetNames(type), at);
            return;
        }

        string[] allowed = type switch
        {
            _ when type == typeof(DocumentIdentifier) => [FieldKinds.Identifier],
            _ when type == typeof(DateOnly) => [FieldKinds.Date],
            _ when type == typeof(DatePeriod) => [FieldKinds.Period],
            _ when type == typeof(Party) => [FieldKinds.Party],
            _ when type == typeof(Address) => [FieldKinds.Address],
            _ when type == typeof(Money) => [FieldKinds.Money],
            _ when type == typeof(PaymentTerms) => [FieldKinds.PaymentTerms],
            _ when type == typeof(TaxBreakdownLine) => [FieldKinds.TaxRow],
            _ when type == typeof(ChargeOrAllowance) => [FieldKinds.ChargeOrAllowance],
            _ when type == typeof(TemperatureReading) => [FieldKinds.TemperatureReading],
            _ when type == typeof(UnitOfMeasure) => [FieldKinds.Unit],
            _ when type == typeof(decimal) => [FieldKinds.Decimal, FieldKinds.Percent],
            _ when type == typeof(int) => [FieldKinds.Integer],
            _ when type == typeof(bool) => [FieldKinds.Boolean],
            _ when type == typeof(Guid) => [FieldKinds.String],
            _ when type == typeof(string) && field.Key == "currency" => [FieldKinds.Currency],
            _ when type == typeof(string) => [FieldKinds.String, FieldKinds.Text],
            _ => throw new ShouldAssertException($"{at}: no field kind is known for {type.Name}"),
        };
        field.Kind.ShouldBeOneOf(allowed, at);
        if (field.Kind == FieldKinds.Money)
        {
            field.CurrencyFrom.ShouldBe("currency", at);
        }
    }

    /// <summary>The members the serialiser reads, as the field schema seam takes them: public read-write properties without the extension bag.</summary>
    private static List<PropertyInfo> Members(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .OrderBy(p => p.MetadataToken)];

    private static string JsonName(PropertyInfo property) => JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static bool IsFieldValue(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FieldValue<>);

    private static Type? ListElement(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type.GetGenericArguments()[0] : null;

    private static Invoice FullInvoice()
    {
        static FieldValue<T> V<T>(T value) => FieldValue<T>.Extracted(value, confidence: 0.99);
        var eur = new Money(100m, "EUR");
        var party = new Party("Nordwind Großhandel GmbH", "DE811111111", "12/345/67890", "HRB 1", new Address("Hafenstr. 1", null, "20457", "Hamburg", null, "DE"), "ap@nordwind.example", "+49 40 1", "DE02120300000000202051", "BYLADEM1001");
        var line = new InvoiceLine
        {
            LineNumber = 1,
            SupplierSku = V("4711"),
            Gtin = V("4006381333931"),
            Description = V("Kaffee Crema 1kg"),
            Quantity = V(2m),
            Unit = V(UnitOfMeasure.Kilogram),
            UnitRaw = "kg",
            PackSize = V("1 kg"),
            UnitPrice = V(new Money(50m, "EUR")),
            DiscountPercent = V(0m),
            DiscountAmount = V(new Money(0m, "EUR")),
            LineNet = V(eur),
            TaxRate = V(7m),
            LineGross = V(new Money(107m, "EUR")),
            DepositAmount = V(new Money(0m, "EUR")),
            LotNumber = V("L-1"),
            BestBefore = V(new DateOnly(2027, 1, 1)),
            MatchedProductId = Guid.NewGuid(),
        };
        return new Invoice
        {
            Kind = V(InvoiceKind.Invoice),
            InvoiceNumber = V(DocumentIdentifier.Create("RE-2026-0001")),
            IssueDate = V(new DateOnly(2026, 10, 28)),
            DeliveryDate = V(new DateOnly(2026, 10, 27)),
            ServicePeriod = V(new DatePeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))),
            DueDate = V(new DateOnly(2026, 11, 27)),
            Currency = V("EUR"),
            Supplier = V(party),
            Customer = V(party with { Name = "Acme GmbH" }),
            OrderReferences = [V(DocumentIdentifier.Create("PO-1"))],
            DeliveryNoteReferences = [V(DocumentIdentifier.Create("LS-1"))],
            OriginalInvoiceReference = V(DocumentIdentifier.Create("RE-2026-0000")),
            SubtotalNet = V(eur),
            TaxBreakdown = [new TaxBreakdownLine(7m, eur, new Money(7m, "EUR"), TaxCategory.Standard)],
            TotalTax = V(new Money(7m, "EUR")),
            TotalGross = V(new Money(107m, "EUR")),
            AmountDue = V(new Money(107m, "EUR")),
            Rounding = V(new Money(0m, "EUR")),
            Charges = [new ChargeOrAllowance("Fracht", new Money(5m, "EUR"), 7m, IsCharge: true)],
            Allowances = [new ChargeOrAllowance("Rabatt", new Money(5m, "EUR"), 7m, IsCharge: false)],
            PaymentTerms = V(new PaymentTerms(new DateOnly(2026, 11, 27), 2m, new DateOnly(2026, 11, 7), PaymentMeans.BankTransfer, "RE-2026-0001", "DE02120300000000202051", "BYLADEM1001", "14 Tage 2 % Skonto")),
            Notes = V("Danke"),
            Lines = [line],
        };
    }
}
