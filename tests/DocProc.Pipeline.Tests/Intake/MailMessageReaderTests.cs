using DocProc.Application.Routing;
using DocProc.Infrastructure.Mail;
using MimeKit;

namespace DocProc.Pipeline.Tests.Intake;

/// <summary>Reading a received mail (docs/11 §7.1): attachments, Authentication-Results, forward unwrapping and the channel configs.</summary>
public sealed class MailMessageReaderTests
{
    private static readonly MailReadOptions TrustBureau = new(true, ["bureau.example"], []);

    internal static MimeMessage Message(string from, string subject, string? authResults = null, string? text = null, params (string Name, string Type, byte[] Bytes)[] files)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("invoices@tenant-a.local"));
        message.Cc.Add(MailboxAddress.Parse("Nordwind Desk <nordwind@tenant-a.local>"));
        message.Subject = subject;
        message.MessageId = $"{Guid.NewGuid():N}@test.example";
        if (authResults is not null)
        {
            message.Headers.Add("Authentication-Results", authResults);
        }

        var body = new BodyBuilder { TextBody = text ?? "Anbei die Rechnung." };
        foreach (var (name, type, bytes) in files)
        {
            body.Attachments.Add(name, bytes, ContentType.Parse(type));
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    internal static byte[] Pdf => "%PDF-1.4 test"u8.ToArray();

    [Fact]
    public void A_mail_gives_its_sender_recipients_subject_attachments_and_DMARC_result()
    {
        var message = Message(
            "Rechnungsversand <Rechnungsversand@Nordwind.example>",
            "Rechnung 4711",
            "mx.tenant-a.local; spf=pass smtp.mailfrom=nordwind.example; dkim=pass header.d=nordwind.example; dmarc=pass header.from=nordwind.example",
            files: ("rechnung.pdf", "application/pdf", Pdf));

        var mail = MailMessageReader.Read(message, MailReadOptions.Default);

        mail.Sender.ShouldBe("rechnungsversand@nordwind.example");
        mail.ForwardedBy.ShouldBeNull();
        mail.Recipients.ShouldBe(["invoices@tenant-a.local", "nordwind@tenant-a.local"]);
        mail.Subject.ShouldBe("Rechnung 4711");
        mail.Attachments.ShouldHaveSingleItem().FileName.ShouldBe("rechnung.pdf");
        mail.Attachments[0].Content.ToArray().ShouldBe(Pdf);
        mail.SenderAuth.Dmarc.ShouldBe("pass");
        mail.SenderAuth.Spf.ShouldBe("pass");
        mail.SenderAuth.DkimDomains.ShouldBe(["nordwind.example"]);
        mail.SenderAuth.AuthServId.ShouldBe("mx.tenant-a.local");
        mail.SenderAuth.Verified.ShouldBeTrue();
    }

    [Fact]
    public void A_DMARC_failure_or_a_missing_header_leaves_the_sender_unverified()
    {
        var failed = MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung", "mx.tenant-a.local; spf=fail; dmarc=fail header.from=nordwind.example"), MailReadOptions.Default);
        failed.SenderAuth.Dmarc.ShouldBe("fail");
        failed.SenderAuth.Verified.ShouldBeFalse();

        MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung"), MailReadOptions.Default).SenderAuth.Verified.ShouldBeFalse();
    }

    [Fact]
    public void A_DMARC_pass_for_another_domain_does_not_count()
    {
        var spoofed = MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung", "mx.tenant-a.local; dmarc=pass header.from=attacker.example"), MailReadOptions.Default);

        spoofed.SenderAuth.Dmarc.ShouldBe("mismatch");
        spoofed.SenderAuth.Verified.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_trusted_receivers_header_is_believed()
    {
        var message = Message("rechnungsversand@nordwind.example", "Rechnung", "attacker.example; dmarc=pass header.from=nordwind.example");
        message.Headers.Add("Authentication-Results", "mx.tenant-a.local; dmarc=fail header.from=nordwind.example");

        MailMessageReader.Read(message, new MailReadOptions(true, [], ["mx.tenant-a.local"])).SenderAuth.Verified.ShouldBeFalse("the receiver's own header says fail");
        MailMessageReader.Read(message, new MailReadOptions(true, [], ["mx.other.example"])).SenderAuth.Dmarc.ShouldBeNull("no header of a trusted receiver");
    }

    [Fact]
    public void A_forward_attached_as_a_message_from_a_trusted_forwarder_is_unwrapped_to_its_original_sender()
    {
        var original = Message("Rechnungsversand <rechnungsversand@nordwind.example>", "Rechnung 4711", files: ("rechnung.pdf", "application/pdf", Pdf));
        var forward = Message("scans@bureau.example", "Fwd: Rechnung 4711", "mx.tenant-a.local; dmarc=pass header.from=bureau.example");
        var builder = new BodyBuilder { TextBody = "see attached" };
        builder.Attachments.Add(new MessagePart { Message = original });
        builder.Attachments.Add("deckblatt.pdf", Pdf, ContentType.Parse("application/pdf"));
        forward.Body = builder.ToMessageBody();

        var mail = MailMessageReader.Read(forward, TrustBureau);

        mail.Sender.ShouldBe("rechnungsversand@nordwind.example");
        mail.ForwardedBy.ShouldBe("scans@bureau.example");
        mail.Unwrapped.ShouldBe("attachedMessage");
        mail.Subject.ShouldBe("Rechnung 4711");
        mail.Attachments.Select(a => a.FileName).ShouldBe(["rechnung.pdf", "deckblatt.pdf"]);
        mail.SenderAuth.Via.ShouldBe(SenderAuthentication.ViaTrustedForwarder);
        mail.SenderAuth.Verified.ShouldBeTrue("the trusted forwarder passed DMARC");

        var forwarderFailed = Message("scans@bureau.example", "Fwd", "mx.tenant-a.local; dmarc=fail header.from=bureau.example");
        forwarderFailed.Body = builder.ToMessageBody();
        MailMessageReader.Read(forwarderFailed, TrustBureau).SenderAuth.Verified.ShouldBeFalse();
    }

    [Fact]
    public void An_inline_forward_from_a_trusted_forwarder_names_its_original_sender()
    {
        var text = """
            Hallo,

            ---------- Weitergeleitete Nachricht ---------
            Von: Nordwind Rechnungsversand <rechnungsversand@nordwind.example>
            Datum: 3. Nov. 2026
            Betreff: Rechnung 4711
            """;
        var forward = Message("scans@bureau.example", "WG: Rechnung 4711", "mx.tenant-a.local; dmarc=pass header.from=bureau.example", text, ("rechnung.pdf", "application/pdf", Pdf));

        var mail = MailMessageReader.Read(forward, TrustBureau);

        mail.Sender.ShouldBe("rechnungsversand@nordwind.example");
        mail.ForwardedBy.ShouldBe("scans@bureau.example");
        mail.Unwrapped.ShouldBe("inline");
        mail.Subject.ShouldBe("Rechnung 4711");
        mail.Attachments.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_forward_from_an_untrusted_domain_or_with_unwrapping_off_keeps_the_forwarder_as_sender()
    {
        var text = "---------- Forwarded message ---------\nFrom: rechnungsversand@nordwind.example\n";
        var forward = Message("someone@private.example", "Fwd: Rechnung", "mx; dmarc=pass header.from=private.example", text);

        MailMessageReader.Read(forward, TrustBureau).Sender.ShouldBe("someone@private.example");
        MailMessageReader.Read(forward, new MailReadOptions(false, ["private.example"], [])).Sender.ShouldBe("someone@private.example");
        MailMessageReader.Read(forward, new MailReadOptions(true, ["private.example"], [])).Sender.ShouldBe("rechnungsversand@nordwind.example");
    }

    [Fact]
    public void The_text_body_is_not_a_document_and_a_nameless_attachment_gets_a_name()
    {
        var message = Message("a@b.example", "x");
        var builder = new BodyBuilder { TextBody = "body", HtmlBody = "<p>body</p>" };
        var part = new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream(Pdf)), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) };
        builder.Attachments.Add(part);
        message.Body = builder.ToMessageBody();

        MailMessageReader.Read(message, MailReadOptions.Default).Attachments.ShouldHaveSingleItem().FileName.ShouldBe("attachment-1");
    }

    [Fact]
    public void Mailbox_configs_are_checked_and_give_a_source_key()
    {
        var platform = MailboxChannelConfig.TryParse("""{"address":"Invoices@Tenant-A.local"}""", [], out var none).ShouldNotBeNull(string.Join(" ", none));
        platform.SourceKey.ShouldBe("imap:platform/invoices@tenant-a.local/inbox");
        platform.UsesPlatformServer.ShouldBeTrue();

        var own = MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"imap.example.com","username":"ap","folder":"Rechnungen","trustedForwarderDomains":["Bureau.example"]}""", [], out _).ShouldNotBeNull();
        own.SourceKey.ShouldBe("imap:imap.example.com:993/ap/rechnungen");
        own.ForwarderDomains.ShouldBe(["bureau.example"]);

        MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"127.0.0.1"}""", [], out var loopback).ShouldBeNull();
        loopback.ShouldContain(e => e.Contains("public", StringComparison.Ordinal));
        MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"mail.internal"}""", [], out _).ShouldBeNull();
        MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"mail.internal"}""", ["mail.internal"], out _).ShouldNotBeNull("an allowed host passes");
        MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"imap.example.com","security":"none"}""", [], out _).ShouldBeNull("a tenant host runs TLS");
        MailboxChannelConfig.TryParse("""{"address":"not-an-address"}""", [], out _).ShouldBeNull();
        MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","password":"x"}""", [], out _).ShouldBeNull("the password is the write-only secret, never config");
    }

    [Fact]
    public void Watched_folder_configs_stay_inside_the_tenants_root()
    {
        var config = WatchedFolderChannelConfig.TryParse("""{"path":"scans\\eingang","pattern":"*.pdf","stableSeconds":2}""", out var none).ShouldNotBeNull(string.Join(" ", none));
        config.RelativePath.ShouldBe("scans/eingang/");
        config.SourceKey("tenant-a").ShouldBe("folder:tenant-a/scans/eingang/");
        WatchedFolderChannelConfig.TryParse("{}", out _).ShouldNotBeNull().RelativePath.ShouldBe(string.Empty);

        foreach (var bad in new[] { """{"path":"../tenant-b"}""", """{"path":"/etc"}""", """{"path":"C:\\x"}""", """{"path":"processed"}""", """{"pattern":"a/b"}""", """{"stableSeconds":-1}""" })
        {
            WatchedFolderChannelConfig.TryParse(bad, out var errors).ShouldBeNull(bad);
            errors.ShouldNotBeEmpty();
        }
    }
}
