using System.Reflection;
using System.Text.Json;
using DocProc.Api.Documents;
using DocProc.Application.Documents;
using DocProc.Domain.Common;
using DocProc.Domain.DeliveryNotes;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Invoices;
using DocProc.Domain.PurchaseOrders;
using DocProc.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace DocProc.Api.Tests;

/// <summary>
/// The <see cref="IFieldSchemaResolver"/> seam of docs/11 §12.8 changes nothing: for every path the old reflection
/// walk could be asked about (every property of every coded record, nested and indexed, real or not, with values of
/// every shape), the patcher behind the seam returns the same JSON and the same audit records, or fails with the same
/// path and message, as the patcher before it (<see cref="LegacyCanonicalPatcher"/>, kept verbatim).
/// </summary>
public sealed class FieldSchemaSeamTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);

    private static readonly FieldSource Llm = new(SourceMethod.Llm, Page: 1);

    /// <summary>Values of every shape a client can send: nothing, strings, numbers, booleans, objects, arrays.</summary>
    private static readonly JsonElement?[] Values =
    [
        null,
        JsonSerializer.SerializeToElement<object?>(null),
        JsonSerializer.SerializeToElement("RE-2026-0100"),
        JsonSerializer.SerializeToElement("   "),
        JsonSerializer.SerializeToElement("eur"),
        JsonSerializer.SerializeToElement("Euro"),
        JsonSerializer.SerializeToElement("2026-10-28"),
        JsonSerializer.SerializeToElement("12.50"),
        JsonSerializer.SerializeToElement(42),
        JsonSerializer.SerializeToElement(12.505m),
        JsonSerializer.SerializeToElement(true),
        JsonSerializer.SerializeToElement(new { amount = 99.95m, currency = "usd" }),
        JsonSerializer.SerializeToElement(new { verbatim = "po 4711", normalized = "X" }),
        JsonSerializer.SerializeToElement(new { name = "Nordwind GmbH", vatId = "DE 811 177 937" }),
        JsonSerializer.SerializeToElement(new { start = "2026-10-01", end = "2026-10-31" }),
        JsonSerializer.SerializeToElement(new[] { 1, 2 }),
    ];

    public static TheoryData<string> Roots => ["invoice", "purchaseOrder", "deliveryNote"];

    [Theory]
    [MemberData(nameof(Roots))]
    public void Every_path_and_value_patches_exactly_as_before_the_seam(string root)
    {
        var (type, json) = Fixture(root);
        var paths = PathsOf(type, depth: 0).Concat(Oddities).Distinct(StringComparer.Ordinal).ToList();
        paths.Count.ShouldBeGreaterThan(40);

        var compared = 0;
        foreach (var path in paths)
        {
            foreach (var value in Values)
            {
                var correction = new FieldCorrection(path, value, "parity");
                Outcome(() => LegacyCanonicalPatcher.Apply(type, json, [correction], "anna", At, "EUR"))
                    .ShouldBe(Outcome(() => CanonicalPatcher.Apply(ReflectionFieldSchemaResolver.Instance.NodeFor(type), json, [correction], "anna", At, "EUR")), $"{root}: {path} = {value?.GetRawText() ?? "(none)"}");
                compared++;
            }
        }

        compared.ShouldBe(paths.Count * Values.Length);
    }

    [Fact]
    public void Several_corrections_in_one_request_match_too()
    {
        var json = CanonicalDocumentSerializer.Serialize(TestInvoices.Valid());
        FieldCorrection[] corrections =
        [
            new("issueDate", JsonSerializer.SerializeToElement("2026-10-28"), "misread"),
            new("lines[1].quantity", JsonSerializer.SerializeToElement(6), null),
            new("supplier.vatId", JsonSerializer.SerializeToElement("DE811177937"), null),
            new("totalGross", JsonSerializer.SerializeToElement(166.5m), null),
            new("currency", JsonSerializer.SerializeToElement("eur"), null),
        ];

        var legacy = Outcome(() => LegacyCanonicalPatcher.Apply(typeof(Invoice), json, corrections, "anna", At));
        legacy.ShouldStartWith("ok:");
        Outcome(() => CanonicalPatcher.Apply(typeof(Invoice), json, corrections, "anna", At)).ShouldBe(legacy);
        Outcome(() => CanonicalPatcher.Apply(ReflectionFieldSchemaResolver.Instance.RootFor(DocumentTypeKey.CreditNote)!, json, corrections, "anna", At)).ShouldBe(legacy);
    }

    [Theory]
    [InlineData(WellKnownTypeKeys.Invoice, typeof(Invoice))]
    [InlineData(WellKnownTypeKeys.CreditNote, typeof(Invoice))]
    [InlineData(WellKnownTypeKeys.PurchaseOrder, typeof(PurchaseOrder))]
    [InlineData(WellKnownTypeKeys.DeliveryNote, typeof(DeliveryNote))]
    [InlineData(WellKnownTypeKeys.Statement, null)]
    [InlineData(WellKnownTypeKeys.Reminder, null)]
    [InlineData(WellKnownTypeKeys.Other, null)]
    [InlineData(WellKnownTypeKeys.Unknown, null)]
    public void The_reflection_resolver_has_a_schema_for_exactly_the_types_with_canonical_data(DocumentTypeKey type, Type? root)
    {
        ReflectionFieldSchemaResolver.Instance.RootFor(type)?.ClrType.ShouldBe(root);
        (ReflectionFieldSchemaResolver.Instance.RootFor(type) is null).ShouldBe(root is null);
        CanonicalPatcher.RootTypeFor(type).ShouldBe(LegacyCanonicalPatcher.RootTypeFor(type));
    }

    [Fact]
    public void The_schema_describes_the_records_as_the_walk_read_them()
    {
        var invoice = ReflectionFieldSchemaResolver.Instance.RootFor(DocumentTypeKey.Invoice)!;
        invoice.Name.ShouldBe(nameof(Invoice));

        var number = invoice.Member("invoiceNumber")!;
        number.Kind.ShouldBe(FieldSchemaMemberKind.Field);
        number.ValueType.ShouldBe(typeof(DocumentIdentifier));
        number.IsCurrencyCode.ShouldBeFalse();

        invoice.Member("currency")!.IsCurrencyCode.ShouldBeTrue();
        invoice.Member("supplier")!.ValueShape!.Member("vatId")!.Kind.ShouldBe(FieldSchemaMemberKind.Scalar);
        invoice.Member("supplier")!.ValueShape!.Member("normalizedVatId").ShouldBeNull("computed, the serialiser ignores it on input");

        var lines = invoice.Member("lines")!;
        lines.Kind.ShouldBe(FieldSchemaMemberKind.List);
        lines.IsNavigable.ShouldBeFalse();
        lines.Element!.Kind.ShouldBe(FieldSchemaMemberKind.Group);
        lines.Element.Shape.Name.ShouldBe(nameof(InvoiceLine));
        lines.Element.Shape.Member("lineNumber")!.Kind.ShouldBe(FieldSchemaMemberKind.Scalar);
        lines.Element.Shape.Member("extra").ShouldBeNull("the extension bag is not addressable");

        invoice.Member("orderReferences")!.Element!.Kind.ShouldBe(FieldSchemaMemberKind.Field);
        invoice.Member("taxBreakdown")!.Element!.Kind.ShouldBe(FieldSchemaMemberKind.Group);
        invoice.Member("extra").ShouldBeNull();
        invoice.Member("Currency").ShouldBeNull("members are matched by their JSON name");

        ReflectionFieldSchemaResolver.Instance.NodeFor(typeof(Invoice)).ShouldBeSameAs(invoice, "nodes are cached per type");
    }

    [Fact]
    public void The_api_resolves_the_reflection_schema()
    {
        factory.Services.GetRequiredService<IFieldSchemaResolver>().ShouldBeSameAs(ReflectionFieldSchemaResolver.Instance);
    }

    /// <summary>Paths the property enumeration does not produce: unknown names, wrong indexes, bad grammar, the bag.</summary>
    private static readonly string[] Oddities =
    [
        string.Empty, "  ", "nope", "Currency", "extra", "lines[0].extra", "lines[9].quantity", "lines.quantity", "lines[0]",
        "lines[0].quantity.amount", "supplier.address.city", "supplier.address", "supplier.vatId.x", "supplier[0]",
        "totalGross.amount", "totalGross.currency", "invoiceNumber.verbatim", "issueDate.year", "orderReferences[0].verbatim",
        "taxBreakdown[0].rate", "taxBreakdown[5].rate", "taxBreakdown.rate", "charges[0].amount", "lines[0].unitRaw",
        "lines[0].lineNumber", "lines[0].matchedProductId", "status", "temperatureReadings[0].celsius", "a..b", "lines[x]",
        "lines[-1].quantity", "lines[1234567].quantity", "9lives", "supplier.normalizedVatId", "paymentTerms.discountPercent",
        "servicePeriod.from", "deliveryAddress.city", "paymentTerms.iban", "supplier.address.city.x", "charges[0].description", "lines[0].unit", "kind",
    ];

    private static IEnumerable<string> PathsOf(Type type, int depth)
    {
        if (depth > 3)
        {
            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            yield return name;
            var propertyType = property.PropertyType;
            var element = ElementOf(propertyType);
            if (element is not null)
            {
                yield return name + "[0]";
                yield return name + "[7]";
                foreach (var nested in PathsOf(element.IsGenericType && element.GetGenericTypeDefinition() == typeof(FieldValue<>) ? element.GetGenericArguments()[0] : element, depth + 1))
                {
                    yield return $"{name}[0].{nested}";
                }

                continue;
            }

            var inner = propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(FieldValue<>) ? propertyType.GetGenericArguments()[0] : propertyType;
            if (inner.IsClass && inner != typeof(string) && !inner.IsArray && inner.Namespace?.StartsWith("DocProc", StringComparison.Ordinal) == true)
            {
                foreach (var nested in PathsOf(inner, depth + 1))
                {
                    yield return $"{name}.{nested}";
                }
            }
        }
    }

    private static Type? ElementOf(Type type) =>
        type == typeof(string)
            ? null
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                ? type.GetGenericArguments()[0]
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))?.GetGenericArguments()[0];

    /// <summary>The outcome as one comparable string: the JSON and the audit records, or the failure's type, path and message.</summary>
    private static string Outcome(Func<CanonicalPatchResult> apply)
    {
        try
        {
            var result = apply();
            return "ok:" + result.Json + "|" + string.Join("|", result.Applied.Select(a => $"{a.Path};{a.OldValueJson};{a.NewValueJson};{a.Reason};{a.Op};{a.Source}"));
        }
        catch (CanonicalPatchException ex)
        {
            return $"refused:{ex.Path}:{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"threw:{ex.GetType().FullName}:{ex.Message}";
        }
    }

    private static (Type Type, string Json) Fixture(string root) => root switch
    {
        "invoice" => (typeof(Invoice), CanonicalDocumentSerializer.Serialize(TestInvoices.Valid() with
        {
            OrderReferences = [FieldValue.Extracted(DocumentIdentifier.Create("PO-0001"), Llm, 0.9)],
            ServicePeriod = FieldValue.Extracted(new DatePeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), Llm, 0.9),
            Charges = [new ChargeOrAllowance("Fracht", TestInvoices.Eur(5m), 19m, IsCharge: true)],
            PaymentTerms = FieldValue.Extracted(new PaymentTerms(DiscountPercent: 2m, Iban: "DE02120300000000202051"), Llm, 0.9),
        })),
        "purchaseOrder" => (typeof(PurchaseOrder), CanonicalDocumentSerializer.Serialize(new PurchaseOrder
        {
            PoNumber = FieldValue.Extracted(DocumentIdentifier.Create("PO-7"), Llm, 0.9),
            Currency = FieldValue.Extracted("EUR", Llm, 0.9),
            Supplier = FieldValue.Extracted(new Party("Nordwind GmbH", Address: new Address("Hauptstr. 1", City: "Berlin")), Llm, 0.9),
            DeliveryAddress = FieldValue.Extracted(new Address("Hauptstr. 1", PostalCode: "10115", City: "Berlin", CountryCode: "DE"), Llm, 0.9),
            TotalGross = FieldValue.Extracted(TestInvoices.Eur(10m), Llm, 0.9),
            Lines = [new PurchaseOrderLine { LineNumber = 1, QuantityOrdered = FieldValue.Extracted(3m, Llm, 0.9), UnitPrice = FieldValue.Extracted(TestInvoices.Eur(2m), Llm, 0.9) }],
        })),
        "deliveryNote" => (typeof(DeliveryNote), CanonicalDocumentSerializer.Serialize(new DeliveryNote
        {
            DeliveryNoteNumber = FieldValue.Extracted(DocumentIdentifier.Create("LS-9"), Llm, 0.9),
            OrderReferences = [FieldValue.Extracted(DocumentIdentifier.Create("PO-7"), Llm, 0.9)],
            TemperatureReadings = [new TemperatureReading(4.5m, "Kühlraum")],
            Lines = [new DeliveryNoteLine { LineNumber = 1, QuantityDelivered = FieldValue.Extracted(3m, Llm, 0.9) }],
        })),
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, null),
    };
}
