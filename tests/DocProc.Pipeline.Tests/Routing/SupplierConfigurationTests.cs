using System.Text.Json;
using DocProc.Application.Routing;
using DocProc.Application.Suppliers;

namespace DocProc.Pipeline.Tests.Routing;

/// <summary>Supplier settings (docs/11 §6.4), the typed attribute schema, sender authentication and the normal forms of source facts.</summary>
public sealed class SupplierConfigurationTests
{
    [Fact]
    public void Settings_round_trip_inherit_from_the_group_and_resolve_layouts_required_against_the_tenant_default()
    {
        var own = SupplierSettings.Parse("""{"allowedTypeKeys":["Invoice"],"layouts":{"required":"inherit"},"review":{"mode":"always"}}""");
        var group = SupplierSettings.Parse("""{"fixDocumentType":"always","layouts":{"required":"true"},"locale":{"culture":"de-DE"},"requireSenderAuth":false}""");

        var effective = own.Over(group);

        effective.AllowedTypeKeys.ShouldBe(["Invoice"]);
        effective.FixDocumentType.ShouldBe(FixDocumentType.Always);
        effective.Locale!.Culture.ShouldBe("de-DE");
        effective.AlwaysReview.ShouldBeTrue();
        effective.RequireSenderAuth.ShouldBe(false);
        effective.LayoutsRequired(tenantDefault: false).ShouldBeTrue("the group says true and the supplier inherits");
        own.LayoutsRequired(tenantDefault: true).ShouldBeTrue("inherit reads the tenant default");
        own.LayoutsRequired(tenantDefault: false).ShouldBeFalse();
        SupplierSettings.Parse("""{"layouts":{"required":"false"}}""").LayoutsRequired(tenantDefault: true).ShouldBeFalse();
        SupplierSettings.Parse(own.ToJson()).ToJson().ShouldBe(own.ToJson());
        SupplierSettings.Empty.ToJson().ShouldBeNull();
        SupplierSettings.Parse("not json").ShouldBe(SupplierSettings.Empty);
    }

    [Fact]
    public void A_single_allowed_type_is_pinned_when_the_source_matched_or_always()
    {
        var whenSource = SupplierSettings.Parse("""{"allowedTypeKeys":["Invoice"]}""");
        whenSource.PinnedType(sourceMatched: true).ShouldBe("Invoice");
        whenSource.PinnedType(sourceMatched: false).ShouldBeNull();
        SupplierSettings.Parse("""{"allowedTypeKeys":["Invoice"],"fixDocumentType":"always"}""").PinnedType(sourceMatched: false).ShouldBe("Invoice");
        SupplierSettings.Parse("""{"allowedTypeKeys":["Invoice"],"fixDocumentType":"never"}""").PinnedType(sourceMatched: true).ShouldBeNull();
        SupplierSettings.Parse("""{"allowedTypeKeys":["Invoice","CreditNote"]}""").PinnedType(sourceMatched: true).ShouldBeNull();
    }

    [Fact]
    public void Unknown_settings_members_do_not_read()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<SupplierSettings>("""{"layoutz":{}}""", SupplierSettings.JsonOptions));
    }

    [Fact]
    public void The_attribute_schema_checks_types_enums_patterns_bounds_and_required()
    {
        var schema = SupplierAttributeSchema.TryParse(
            """
            { "type": "object",
              "properties": {
                "erpVendorId": { "type": "string", "pattern": "^V-[0-9]{5}$", "maxLength": 7, "title": "ERP vendor" },
                "paymentBlock": { "type": "boolean" },
                "costCentre": { "type": "string", "enum": ["4711", "4712"] },
                "discount": { "type": "number", "minimum": 0, "maximum": 100 },
                "terms": { "type": "integer" } },
              "required": ["erpVendorId"],
              "additionalProperties": false }
            """,
            out var errors).ShouldNotBeNull(string.Join(" ", errors));

        Attributes("""{"erpVendorId":"V-10042","paymentBlock":false,"costCentre":"4711","discount":2.5,"terms":30}""", schema, true).ShouldBeEmpty();
        Attributes("""{"erpVendorId":"10042"}""", schema, true).ShouldContain(e => e.Key == "erpVendorId");
        Attributes("""{"erpVendorId":"V-10042","paymentBlock":"no"}""", schema, true).ShouldContain(e => e.Key == "paymentBlock");
        Attributes("""{"erpVendorId":"V-10042","costCentre":"9999"}""", schema, true).ShouldContain(e => e.Key == "costCentre");
        Attributes("""{"erpVendorId":"V-10042","discount":101}""", schema, true).ShouldContain(e => e.Key == "discount");
        Attributes("""{"erpVendorId":"V-10042","terms":30.5}""", schema, true).ShouldContain(e => e.Key == "terms");
        Attributes("""{"erpVendorId":"V-10042","free":"x"}""", schema, true).ShouldContain(e => e.Key == "free");
        Attributes("""{"paymentBlock":true}""", schema, true).ShouldContain(e => e.Key == "erpVendorId");
        Attributes("""{"paymentBlock":true}""", schema, false).ShouldBeEmpty("a group's and an entity's attributes are partial");
        Attributes("""{"erpVendorId":"V-10042","discount":null}""", schema, true).ShouldBeEmpty("null clears an attribute");
    }

    [Theory]
    [InlineData("""{"properties":{"a":{"type":"date"}}}""")]
    [InlineData("""{"properties":{"a":{"type":"string","format":"email"}}}""")]
    [InlineData("""{"properties":{"a":{"type":"number","maxLength":3}}}""")]
    [InlineData("""{"properties":{"a":{"type":"string","pattern":"(?<=x)y"}}}""")]
    [InlineData("""{"properties":{"a":{"type":"string","enum":[1]}}}""")]
    [InlineData("""{"properties":{"1a":{"type":"string"}}}""")]
    [InlineData("""{"properties":{},"required":["missing"]}""")]
    [InlineData("""{"type":"array"}""")]
    [InlineData("""{"$ref":"#/x"}""")]
    [InlineData("""[1]""")]
    public void A_schema_outside_the_subset_is_refused(string json)
    {
        SupplierAttributeSchema.TryParse(json, out var errors).ShouldBeNull();
        errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Without_additionalProperties_false_free_attributes_stay_valid()
    {
        var schema = SupplierAttributeSchema.TryParse("""{"properties":{"erpVendorId":{"type":"string"}}}""", out _).ShouldNotBeNull();
        Attributes("""{"anything":"goes"}""", schema, true).ShouldBeEmpty();
    }

    [Fact]
    public void Sender_authentication_reads_DMARC_and_aligned_DKIM_and_a_trusted_forwarders_result()
    {
        new SenderAuthentication { Dmarc = "pass" }.Verified.ShouldBeTrue();
        new SenderAuthentication { Dmarc = "fail", Spf = "pass", Dkim = "pass", DkimDomains = ["nordwind.example"], FromDomain = "nordwind.example" }.Verified.ShouldBeTrue();
        new SenderAuthentication { Spf = "pass", FromDomain = "nordwind.example" }.Verified.ShouldBeFalse();
        SenderAuthentication.None("nordwind.example").Verified.ShouldBeFalse();
        SenderAuthentication.Aligned("example", "nordwind.example").ShouldBeFalse("a top-level domain is not a signing domain");
        SenderAuthentication.Aligned("nordwind.example", "mail.nordwind.example").ShouldBeTrue();

        var forwarded = new SenderAuthentication { Via = SenderAuthentication.ViaTrustedForwarder, FromDomain = "nordwind.example", Forwarder = new SenderAuthentication { Dmarc = "pass" } };
        forwarded.Verified.ShouldBeTrue();
        (forwarded with { Forwarder = new SenderAuthentication { Dmarc = "fail" } }).Verified.ShouldBeFalse();
        SenderAuthentication.Parse(forwarded.ToJson())!.Verified.ShouldBeTrue();
    }

    [Theory]
    [InlineData("lieferanten\\nordwind", "lieferanten/nordwind/")]
    [InlineData("/a//b/", "a/b/")]
    [InlineData("", "")]
    [InlineData("a/../b", null)]
    [InlineData("./a", null)]
    public void Folder_paths_are_normalised_and_dot_segments_refused(string path, string? expected) =>
        SourceConditions.Folder(path).ShouldBe(expected);

    [Fact]
    public void Addresses_lose_their_display_name_and_case_and_the_hash_is_the_same_for_the_same_conditions()
    {
        SourceConditions.Address("Nordwind Rechnungen <Rechnung@NordWind.example>").ShouldBe("rechnung@nordwind.example");
        SourceConditions.DomainOf("a@B.example").ShouldBe("b.example");
        SourceConditions.Domain("@Nordwind.Example.").ShouldBe("nordwind.example");
        SourceConditions.Hash(null, null, "a@b.example", null, null, null, null).ShouldBe(SourceConditions.Hash(null, null, "a@b.example", null, null, null, null));
        SourceConditions.Hash(null, null, "a@b.example", null, null, null, null).ShouldNotBe(SourceConditions.Hash(null, "a@b.example", null, null, null, null, null));
    }

    private static IReadOnlyList<(string Key, string Message)> Attributes(string json, SupplierAttributeSchema schema, bool checkRequired)
    {
        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return schema.Validate(attributes, checkRequired);
    }
}
