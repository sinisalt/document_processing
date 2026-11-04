using DocProc.Application.Routing;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Routing;

namespace DocProc.Pipeline.Tests.Routing;

/// <summary>The resolver of docs/11 §7.2: conditions, specificity, priority, sender authentication, ties and defaults.</summary>
public sealed class SupplierSourceResolverTests
{
    private static readonly Guid Nordwind = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Suedhof = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Scanner = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Mailbox = Guid.Parse("20000000-0000-4000-8000-000000000002");

    private static readonly SenderAuthentication DmarcPass = new() { Dmarc = "pass", FromDomain = "nordwind.example" };
    private static readonly SenderAuthentication DmarcFail = new() { Dmarc = "fail", Spf = "fail", FromDomain = "nordwind.example" };

    private static SupplierSourceRule Rule(Guid supplier, Action<SupplierSourceRule> conditions)
    {
        var rule = new SupplierSourceRule { Id = Guid.CreateVersion7(), SupplierId = supplier, ConditionHash = "x", CreatedBy = "test", RequireSenderAuth = true };
        conditions(rule);
        return rule;
    }

    private static SourceContext Mail(string sender, SenderAuthentication? auth = null, string? subject = null, string? fileName = "invoice.pdf", params string[] recipients) =>
        SourceContext.Create(Mailbox, sender, recipients, "invoices@tenant-a.local", null, subject, fileName, auth ?? DmarcPass);

    private static SourceContext Folder(string path, string fileName = "list.pdf") =>
        SourceContext.Create(Scanner, null, null, null, path, null, fileName, null);

    [Fact]
    public void A_sender_rule_routes_an_authenticated_sender_as_Exclusive()
    {
        var rule = Rule(Nordwind, r => r.Sender = "rechnungsversand@nordwind.example");

        var result = SupplierSourceResolver.Resolve(Mail("Rechnungsversand <RECHNUNGSVERSAND@nordwind.example>"), [rule], null);

        result.SupplierId.ShouldBe(Nordwind);
        result.Method.ShouldBe(SupplierMatchMethod.SourceRule);
        result.Rule.ShouldBe(rule);
        result.Mode.ShouldBe(SourceRuleMode.Exclusive);
        result.Confidence.ShouldBe(SupplierSourceResolver.ExclusiveConfidence);
        result.UnverifiedWinners.ShouldBeEmpty();
    }

    [Fact]
    public void A_sender_rule_needs_DMARC_by_default_and_a_skipped_winner_is_reported()
    {
        var rule = Rule(Nordwind, r => r.Sender = "rechnungsversand@nordwind.example");

        var result = SupplierSourceResolver.Resolve(Mail("rechnungsversand@nordwind.example", DmarcFail), [rule], null);

        result.SupplierId.ShouldBeNull();
        var evaluation = result.Evaluations.ShouldHaveSingleItem();
        evaluation.Unverified.ShouldBeTrue();
        evaluation.Failed.ShouldContain("senderAuth");
        result.UnverifiedWinners.ShouldHaveSingleItem().Rule.ShouldBe(rule);
    }

    [Fact]
    public void SPF_with_an_aligned_DKIM_signature_verifies_the_sender_and_an_unaligned_one_does_not()
    {
        var rule = Rule(Nordwind, r => r.Sender = "rechnungsversand@nordwind.example");
        var aligned = new SenderAuthentication { Spf = "pass", Dkim = "pass", DkimDomains = ["mail.nordwind.example"], FromDomain = "nordwind.example" };
        var foreign = aligned with { DkimDomains = ["bulkmailer.example"] };

        SupplierSourceResolver.Resolve(Mail("rechnungsversand@nordwind.example", aligned), [rule], null).SupplierId.ShouldBe(Nordwind);
        SupplierSourceResolver.Resolve(Mail("rechnungsversand@nordwind.example", foreign), [rule], null).SupplierId.ShouldBeNull();
    }

    [Fact]
    public void A_rule_with_the_override_routes_without_authentication()
    {
        var rule = Rule(Nordwind, r =>
        {
            r.Sender = "rechnungsversand@nordwind.example";
            r.RequireSenderAuth = false;
        });

        SupplierSourceResolver.Resolve(Mail("rechnungsversand@nordwind.example", DmarcFail), [rule], null).SupplierId.ShouldBe(Nordwind);
    }

    [Fact]
    public void A_recipient_rule_needs_no_sender_authentication()
    {
        var rule = Rule(Nordwind, r => r.Recipient = "nordwind@tenant-a.local");

        var result = SupplierSourceResolver.Resolve(Mail("someone@else.example", DmarcFail, recipients: "nordwind@tenant-a.local"), [rule], null);

        result.SupplierId.ShouldBe(Nordwind);
    }

    [Fact]
    public void The_more_specific_rule_wins_over_priority_and_priority_breaks_a_tie()
    {
        var domain = Rule(Suedhof, r =>
        {
            r.SenderDomain = "nordwind.example";
            r.Mode = SourceRuleMode.DefaultWhenNoContentMatch;
            r.Priority = 50;
        });
        var sender = Rule(Nordwind, r => r.Sender = "rechnungsversand@nordwind.example");
        SupplierSourceResolver.Resolve(Mail("rechnungsversand@nordwind.example"), [domain, sender], null).SupplierId.ShouldBe(Nordwind);

        var low = Rule(Suedhof, r => r.Recipient = "nordwind@tenant-a.local");
        var high = Rule(Nordwind, r =>
        {
            r.Recipient = "invoices@tenant-a.local";
            r.Priority = 10;
        });
        SupplierSourceResolver.Resolve(Mail("x@y.example", recipients: "nordwind@tenant-a.local"), [low, high], null).SupplierId.ShouldBe(Nordwind);
    }

    [Fact]
    public void Two_suppliers_with_equal_standing_are_ambiguous_and_nothing_is_assigned()
    {
        var one = Rule(Nordwind, r => r.Recipient = "invoices@tenant-a.local");
        var other = Rule(Suedhof, r => r.Recipient = "nordwind@tenant-a.local");

        var result = SupplierSourceResolver.Resolve(Mail("x@y.example", recipients: "nordwind@tenant-a.local"), [one, other], null);

        result.Ambiguous.ShouldBeTrue();
        result.SupplierId.ShouldBeNull();
        result.AmbiguousSupplierIds.ShouldBe(new[] { Nordwind, Suedhof }.Order());
    }

    [Fact]
    public void A_longer_folder_prefix_is_more_specific_and_pins_its_type()
    {
        var supplierFolder = Rule(Nordwind, r =>
        {
            r.ChannelId = Scanner;
            r.FolderPrefix = "lieferanten/nordwind/";
        });
        var priceLists = Rule(Nordwind, r =>
        {
            r.ChannelId = Scanner;
            r.FolderPrefix = "lieferanten/nordwind/preislisten/";
            r.PinTypeKey = "price-list";
        });

        var result = SupplierSourceResolver.Resolve(Folder("Lieferanten/Nordwind/Preislisten/2027/"), [supplierFolder, priceLists], null);

        result.Rule.ShouldBe(priceLists);
        result.PinTypeKey.ShouldBe("price-list");
        result.PinTypeFrom.ShouldBe("rule");
        SupplierSourceResolver.Resolve(Folder("lieferanten/nordwind/gutschriften/"), [supplierFolder, priceLists], null).Rule.ShouldBe(supplierFolder);
        SupplierSourceResolver.Resolve(Folder("lieferanten/suedhof/"), [supplierFolder, priceLists], null).SupplierId.ShouldBeNull();
    }

    [Fact]
    public void Subject_and_file_name_conditions_narrow_a_shared_intake_address()
    {
        var bureau = Rule(Nordwind, r =>
        {
            r.Sender = "scans@bureau.example";
            r.SubjectRegex = "^\\[NORDWIND\\]";
            r.RequireSenderAuth = false;
        });
        var invoicesOnly = Rule(Suedhof, r =>
        {
            r.Sender = "scans@bureau.example";
            r.FileNameRegex = "(?i)^suedhof_.*\\.pdf$";
            r.RequireSenderAuth = false;
        });

        SupplierSourceResolver.Resolve(Mail("scans@bureau.example", subject: "[NORDWIND] Rechnung 4711"), [bureau, invoicesOnly], null).SupplierId.ShouldBe(Nordwind);
        SupplierSourceResolver.Resolve(Mail("scans@bureau.example", subject: "Scan", fileName: "SUEDHOF_0815.pdf"), [bureau, invoicesOnly], null).SupplierId.ShouldBe(Suedhof);
        SupplierSourceResolver.Resolve(Mail("scans@bureau.example", subject: "Scan", fileName: "other.pdf"), [bureau, invoicesOnly], null).SupplierId.ShouldBeNull();
    }

    [Fact]
    public void Without_a_matching_rule_the_channel_default_applies_as_a_default_with_its_type_and_entity()
    {
        var entity = Guid.CreateVersion7();
        var channel = new IngestionChannel { Id = Mailbox, Name = "Mail", DefaultSupplierId = Suedhof, DefaultDocumentTypeKey = "Invoice", DefaultReceivingEntityId = entity };

        var result = SupplierSourceResolver.Resolve(Mail("unknown@else.example"), [], channel);

        result.SupplierId.ShouldBe(Suedhof);
        result.Method.ShouldBe(SupplierMatchMethod.ChannelDefault);
        result.Mode.ShouldBe(SourceRuleMode.DefaultWhenNoContentMatch);
        result.Confidence.ShouldBe(SupplierSourceResolver.DefaultConfidence);
        result.PinTypeKey.ShouldBe("Invoice");
        result.ReceivingEntityId.ShouldBe(entity);
    }

    [Fact]
    public void A_disabled_rule_is_ignored_and_a_rule_on_another_channel_does_not_hold()
    {
        var disabled = Rule(Nordwind, r =>
        {
            r.Recipient = "invoices@tenant-a.local";
            r.Enabled = false;
        });
        var otherChannel = Rule(Suedhof, r =>
        {
            r.ChannelId = Scanner;
            r.Recipient = "invoices@tenant-a.local";
        });

        var result = SupplierSourceResolver.Resolve(Mail("x@y.example"), [disabled, otherChannel], null);

        result.SupplierId.ShouldBeNull();
        result.Evaluations.ShouldHaveSingleItem().Failed.ShouldBe(["channel"]);
    }

    [Fact]
    public void An_authorised_hint_wins_over_every_rule()
    {
        var rule = Rule(Nordwind, r => r.Recipient = "invoices@tenant-a.local");
        var source = Mail("x@y.example") with { SupplierIdHint = Suedhof, TypeHint = "CreditNote" };

        var result = SupplierSourceResolver.Resolve(source, [rule], null);

        result.SupplierId.ShouldBe(Suedhof);
        result.Method.ShouldBe(SupplierMatchMethod.Hint);
        result.PinTypeKey.ShouldBe("CreditNote");
        SupplierSourceResolver.Resolve(source, [rule], null, hintAuthorised: false).SupplierId.ShouldBe(Nordwind);
    }

    [Fact]
    public void A_regular_expression_that_cannot_compile_never_holds()
    {
        var broken = Rule(Nordwind, r =>
        {
            r.Recipient = "invoices@tenant-a.local";
            r.SubjectRegex = "([unclosed";
        });

        SupplierSourceResolver.Resolve(Mail("x@y.example", subject: "anything"), [broken], null).SupplierId.ShouldBeNull();
    }
}
