using System.Text.Json;
using DocProc.Domain.Documents;

namespace DocProc.Domain.Tests;

/// <summary>The string type key that replaced the <c>DocumentType</c> enum (docs/11 §6.2, §20).</summary>
public sealed class DocumentTypeKeyTests
{
    [Fact]
    public void The_default_is_Unknown_and_equals_the_named_key()
    {
        default(DocumentTypeKey).Value.ShouldBe("Unknown");
        default(DocumentTypeKey).IsUnknown.ShouldBeTrue();
        DocumentTypeKey.Unknown.ShouldBe(new DocumentTypeKey("Unknown"));
        new DocumentTypeKey("Unknown").GetHashCode().ShouldBe(default(DocumentTypeKey).GetHashCode());
    }

    [Fact]
    public void The_legacy_keys_are_the_former_enum_members_in_order()
    {
        DocumentTypeKey.Legacy.Select(k => k.Value).ShouldBe(["Unknown", "Invoice", "CreditNote", "PurchaseOrder", "DeliveryNote", "Statement", "Reminder", "Other"]);
        DocumentTypeKey.Legacy.ShouldAllBe(k => k.IsLegacy);
        new DocumentTypeKey("price-list").IsLegacy.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Invoice", true)]
    [InlineData("CreditNote", true)]
    [InlineData("PurchaseOrder", false)]
    [InlineData("price-list", false)]
    [InlineData("invoice", false)]
    public void Only_Invoice_and_CreditNote_are_the_invoice_family(string key, bool family) =>
        new DocumentTypeKey(key).IsInvoiceFamily.ShouldBe(family);

    [Theory]
    [InlineData("price-list")]
    [InlineData("std.invoice")]
    [InlineData("A")]
    [InlineData("return_slip-2")]
    public void Valid_keys_are_taken_as_they_are(string key)
    {
        DocumentTypeKey.TryParse(key, out var parsed).ShouldBeTrue();
        parsed.Value.ShouldBe(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1abc")]
    [InlineData("-abc")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("äbc")]
    public void Invalid_keys_are_refused(string key)
    {
        DocumentTypeKey.TryParse(key, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new DocumentTypeKey(key));
        Should.Throw<FormatException>(() => DocumentTypeKey.Parse(key));
    }

    [Fact]
    public void A_key_has_at_most_64_characters()
    {
        DocumentTypeKey.IsValid("a" + new string('b', 63)).ShouldBeTrue();
        DocumentTypeKey.IsValid("a" + new string('b', 64)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("invoice", "Invoice")]
    [InlineData("CREDITNOTE", "CreditNote")]
    [InlineData("other", "Other")]
    [InlineData("unknown", "Unknown")]
    public void Parsing_takes_the_legacy_names_in_any_case_as_the_enum_did(string input, string expected) =>
        DocumentTypeKey.Parse(input).Value.ShouldBe(expected);

    [Fact]
    public void Keys_compare_ordinally()
    {
        new DocumentTypeKey("price-list").ShouldNotBe(new DocumentTypeKey("Price-List"));
        new[] { new DocumentTypeKey("b"), new DocumentTypeKey("B"), new DocumentTypeKey("a") }.Order().Select(k => k.Value).ShouldBe(["B", "a", "b"]);
    }

    [Fact]
    public void Json_is_the_bare_string_and_reads_the_legacy_names_in_any_case()
    {
        JsonSerializer.Serialize(DocumentTypeKey.CreditNote).ShouldBe("\"CreditNote\"");
        JsonSerializer.Serialize(default(DocumentTypeKey)).ShouldBe("\"Unknown\"");
        JsonSerializer.Deserialize<DocumentTypeKey>("\"creditNote\"").ShouldBe(DocumentTypeKey.CreditNote);
        JsonSerializer.Deserialize<DocumentTypeKey>("\"price-list\"").Value.ShouldBe("price-list");
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DocumentTypeKey>("\"no such key\""));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DocumentTypeKey>("3"));

        var byKey = new Dictionary<DocumentTypeKey, int> { [DocumentTypeKey.Invoice] = 1 };
        var json = JsonSerializer.Serialize(byKey);
        json.ShouldBe("{\"Invoice\":1}");
        JsonSerializer.Deserialize<Dictionary<DocumentTypeKey, int>>(json)!.ShouldContainKey(DocumentTypeKey.Invoice);
    }

    [Fact]
    public void Stored_values_are_read_without_case_folding()
    {
        DocumentTypeKey.FromStored("Invoice").ShouldBe(DocumentTypeKey.Invoice);
        DocumentTypeKey.FromStored(null).ShouldBe(DocumentTypeKey.Unknown);
        DocumentTypeKey.FromStored("price-list").Value.ShouldBe("price-list");
    }
}
