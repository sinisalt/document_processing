using DocProc.Application.Routing;
using DocProc.Infrastructure.Mail;
using MimeKit;
using MimeKit.Cryptography;

namespace DocProc.Pipeline.Tests.Intake;

/// <summary>Reading a received mail (docs/11 §7.1): attachments, Authentication-Results, forward unwrapping and the channel configs.</summary>
public sealed class MailMessageReaderTests
{
    /// <summary>The receiving server of the tests' mails: only its Authentication-Results are believed.</summary>
    internal const string Receiver = "mx.platform.local";

    private static readonly MailReadOptions TrustReceiver = new(true, [], [Receiver]);
    private static readonly MailReadOptions TrustBureau = new(true, ["bureau.example"], [Receiver]);

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
            "mx.platform.local; spf=pass smtp.mailfrom=nordwind.example; dkim=pass header.d=nordwind.example; dmarc=pass header.from=nordwind.example",
            files: ("rechnung.pdf", "application/pdf", Pdf));

        var mail = MailMessageReader.Read(message, TrustReceiver);

        mail.Sender.ShouldBe("rechnungsversand@nordwind.example");
        mail.ForwardedBy.ShouldBeNull();
        mail.Recipients.ShouldBe(["invoices@tenant-a.local", "nordwind@tenant-a.local"]);
        mail.Subject.ShouldBe("Rechnung 4711");
        mail.Attachments.ShouldHaveSingleItem().FileName.ShouldBe("rechnung.pdf");
        mail.Attachments[0].Content.ToArray().ShouldBe(Pdf);
        mail.SenderAuth.Dmarc.ShouldBe("pass");
        mail.SenderAuth.Spf.ShouldBe("pass");
        mail.SenderAuth.DkimDomains.ShouldBe(["nordwind.example"]);
        mail.SenderAuth.AuthServId.ShouldBe("mx.platform.local");
        mail.SenderAuth.Verified.ShouldBeTrue();
    }

    [Fact]
    public void A_DMARC_failure_or_a_missing_header_leaves_the_sender_unverified()
    {
        var failed = MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung", "mx.platform.local; spf=fail; dmarc=fail header.from=nordwind.example"), TrustReceiver);
        failed.SenderAuth.Dmarc.ShouldBe("fail");
        failed.SenderAuth.Verified.ShouldBeFalse();

        MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung"), TrustReceiver).SenderAuth.Verified.ShouldBeFalse();
    }

    [Fact]
    public void A_DMARC_pass_for_another_domain_does_not_count()
    {
        var spoofed = MailMessageReader.Read(Message("rechnungsversand@nordwind.example", "Rechnung", "mx.platform.local; dmarc=pass header.from=attacker.example"), TrustReceiver);

        spoofed.SenderAuth.Dmarc.ShouldBe("mismatch");
        spoofed.SenderAuth.Verified.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_trusted_receivers_header_is_believed()
    {
        var message = Message("rechnungsversand@nordwind.example", "Rechnung", "attacker.example; dmarc=pass header.from=nordwind.example");
        message.Headers.Add("Authentication-Results", "mx.platform.local; dmarc=fail header.from=nordwind.example");

        MailMessageReader.Read(message, new MailReadOptions(true, [], ["mx.platform.local"])).SenderAuth.Verified.ShouldBeFalse("the receiver's own header says fail");
        MailMessageReader.Read(message, new MailReadOptions(true, [], ["mx.other.example"])).SenderAuth.Dmarc.ShouldBeNull("no header of a trusted receiver");
    }

    [Fact]
    public void Without_a_trusted_receiver_no_header_is_believed()
    {
        // GreenMail, a plain Postfix or Dovecot write no Authentication-Results: the topmost header is then the sender's
        // own, and a forged dmarc=pass must not count (docs/11 §7.1, Correction (P4)).
        var forged = Message("Rechnungsversand <rechnung@nordwind.example>", "Rechnung", "anything.example; dmarc=pass header.from=nordwind.example");

        var mail = MailMessageReader.Read(forged, MailReadOptions.Default);

        mail.Sender.ShouldBe("rechnung@nordwind.example");
        mail.SenderAuth.Verified.ShouldBeFalse();
        mail.SenderAuth.Dmarc.ShouldBeNull();
        mail.SenderAuth.AuthServId.ShouldBeNull();
        MailMessageReader.Read(forged, new MailReadOptions(true, [], ["anything.example"])).SenderAuth.Verified.ShouldBeTrue("listed, it is believed");
    }

    [Fact]
    public void A_mailbox_on_a_tenants_own_host_believes_only_its_own_receiver_never_the_platforms()
    {
        // The tenant's provider is not the platform's receiving server, so it leaves a header stamped with the platform's
        // id as the sender wrote it (docs/11 §7.1, Correction (P4, fourth review fixes)).
        string[] platform = [Receiver];
        var message = Message("rechnungsversand@nordwind.example", "Rechnung", "mx.provider.example; spf=fail; dmarc=fail header.from=nordwind.example");
        message.Headers.Add("Authentication-Results", $"{Receiver}; spf=pass; dkim=pass header.d=nordwind.example; dmarc=pass header.from=nordwind.example");
        SenderAuthentication Read(MailboxChannelConfig config) =>
            MailMessageReader.Read(message, new MailReadOptions(true, [], config.TrustedAuthServIds(platform).ToList())).SenderAuth;

        var tenantHost = MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"imap.example.com"}""", [], out _).ShouldNotBeNull();
        tenantHost.TrustedAuthServIds(platform).ShouldBeEmpty("the platform's receiver is not the tenant host's");
        var unlisted = Read(tenantHost);
        unlisted.Verified.ShouldBeFalse("the forged platform header is not believed on a tenant's host");
        unlisted.Dmarc.ShouldBeNull("no header is believed without the channel's own authServId");
        unlisted.AuthServId.ShouldBeNull();

        var withOwn = MailboxChannelConfig.TryParse("""{"address":"ap@acme.example","host":"imap.example.com","authServId":"MX.Provider.example"}""", [], out _).ShouldNotBeNull();
        withOwn.TrustedAuthServIds(platform).ShouldBe(["mx.provider.example"]);
        var own = Read(withOwn);
        own.AuthServId.ShouldBe("mx.provider.example", "only the provider's own header is read");
        own.Dmarc.ShouldBe("fail");
        own.Verified.ShouldBeFalse();

        // Below the provider's header the platform's id is a forgery too: with the provider's pass on top, it is that one.
        var passed = Message("rechnungsversand@nordwind.example", "Rechnung", $"{Receiver}; dmarc=fail header.from=nordwind.example");
        passed.Headers.Insert(0, new Header("Authentication-Results", "mx.provider.example; dmarc=pass header.from=nordwind.example"));
        MailMessageReader.Read(passed, new MailReadOptions(true, [], withOwn.TrustedAuthServIds(platform).ToList())).SenderAuth.Verified.ShouldBeTrue();

        // On the platform server the platform's receiver is believed, with the channel's own id next to it.
        var onPlatform = MailboxChannelConfig.TryParse("""{"address":"invoices@tenant-a.local","authServId":"mx.forwarder.example"}""", [], out _).ShouldNotBeNull();
        onPlatform.TrustedAuthServIds(platform).ShouldBe([Receiver, "mx.forwarder.example"], ignoreOrder: true);
        Read(onPlatform).AuthServId.ShouldBe(Receiver, "the provider's header names no listed receiver, the platform's does");
    }

    [Fact]
    public void A_trusted_receivers_header_that_does_not_parse_is_not_passed_over_for_one_further_down()
    {
        const string malformed = "mx.platform.local; spf=pass (unclosed comment";
        AuthenticationResults.TryParse(System.Text.Encoding.ASCII.GetBytes(malformed), out _).ShouldBeFalse("the premise: MimeKit cannot read it");
        var message = Message("rechnung@nordwind.example", "Rechnung");
        message.Headers.Add("Authentication-Results", malformed);
        message.Headers.Add("Authentication-Results", "mx.platform.local; dmarc=pass header.from=nordwind.example");
        message.Headers.Add("Authentication-Results", "attacker.example; dmarc=pass header.from=nordwind.example");

        var auth = MailMessageReader.Read(message, TrustReceiver).SenderAuth;

        auth.Verified.ShouldBeFalse("the receiver's own header is unreadable, and the ones below it were written by someone else");
        auth.Dmarc.ShouldBeNull();

        // An unreadable header naming another server is skipped, as any header of another server is.
        var other = Message("rechnung@nordwind.example", "Rechnung");
        other.Headers.Add("Authentication-Results", "relay.example; spf=pass (unclosed comment");
        other.Headers.Add("Authentication-Results", "mx.platform.local; dmarc=pass header.from=nordwind.example");
        MailMessageReader.Read(other, TrustReceiver).SenderAuth.Verified.ShouldBeTrue();
    }

    [Fact]
    public void The_documents_of_a_forward_attached_as_a_message_are_items_whoever_forwarded_it()
    {
        var original = Message("Rechnungsversand <rechnungsversand@nordwind.example>", "Rechnung 4711", files: ("rechnung.pdf", "application/pdf", Pdf));
        var forward = Message("someone@private.example", "Fwd: Rechnung 4711", "mx.platform.local; dmarc=pass header.from=private.example");
        var builder = new BodyBuilder { TextBody = "see attached" };
        builder.Attachments.Add(new MessagePart { Message = original });
        forward.Body = builder.ToMessageBody();

        foreach (var options in new[] { TrustReceiver, TrustBureau, new MailReadOptions(false, ["private.example"], [Receiver]) })
        {
            var mail = MailMessageReader.Read(forward, options);
            mail.Sender.ShouldBe("someone@private.example", "an untrusted forwarder, or unwrapping off: the sender stays the forwarder");
            mail.Unwrapped.ShouldBeNull();
            mail.Attachments.ShouldHaveSingleItem().FileName.ShouldBe("rechnung.pdf");
        }
    }

    [Fact]
    public void The_documents_of_every_attached_message_are_items_and_the_first_names_the_sender()
    {
        var first = Message("rechnungsversand@nordwind.example", "Rechnung 1", files: ("rechnung-1.pdf", "application/pdf", Pdf));
        var second = Message("buchhaltung@suedhof.example", "Rechnung 2", files: [("rechnung-2.pdf", "application/pdf", Pdf), ("anlage-2.pdf", "application/pdf", Pdf)]);
        var nested = Message("archiv@bureau.example", "Archiv");
        var inner = new BodyBuilder { TextBody = "nested" };
        inner.Attachments.Add("rechnung-3.pdf", Pdf, ContentType.Parse("application/pdf"));
        inner.Attachments.Add(new MessagePart { Message = first });
        nested.Body = inner.ToMessageBody();
        var forward = Message("scans@bureau.example", "Fwd: Rechnungen", "mx.platform.local; dmarc=pass header.from=bureau.example");
        var builder = new BodyBuilder { TextBody = "see attached" };
        builder.Attachments.Add(new MessagePart { Message = first });
        builder.Attachments.Add(new MessagePart { Message = second });
        builder.Attachments.Add(new MessagePart { Message = nested });
        builder.Attachments.Add("deckblatt.pdf", Pdf, ContentType.Parse("application/pdf"));
        forward.Body = builder.ToMessageBody();

        var mail = MailMessageReader.Read(forward, TrustBureau);

        mail.Sender.ShouldBe("rechnungsversand@nordwind.example");
        mail.Unwrapped.ShouldBe("attachedMessage");
        mail.Attachments.Select(a => a.FileName).ShouldBe(["rechnung-1.pdf", "rechnung-2.pdf", "anlage-2.pdf", "rechnung-3.pdf", "rechnung-1.pdf", "deckblatt.pdf"]);
    }

    [Fact]
    public async Task A_message_body_is_read_up_to_the_limit_whatever_size_the_server_reported()
    {
        (await MailKitImapMailSource.ReadBoundedAsync(new MemoryStream(new byte[1000]), 1000, CancellationToken.None)).ShouldNotBeNull().Length.ShouldBe(1000);
        (await MailKitImapMailSource.ReadBoundedAsync(new MemoryStream(new byte[1001]), 1000, CancellationToken.None)).ShouldBeNull();
        (await MailKitImapMailSource.ReadBoundedAsync(new MemoryStream(new byte[200_000]), 100_000, CancellationToken.None)).ShouldBeNull("stops past the limit, across chunks");
    }

    [Fact]
    public void A_messages_id_is_its_Message_ID_sender_and_bytes_not_its_place_in_the_folder()
    {
        var a = MailKitImapMailSource.ContentMessageId("<4711@nordwind.example>", "Rechnung@Nordwind.example", "x"u8);
        a.ShouldStartWith("imap:mid:");
        MailKitImapMailSource.ContentMessageId("4711@nordwind.example", "rechnung@nordwind.example", "x"u8).ShouldBe(a, "the same stored message read again");
        MailKitImapMailSource.ContentMessageId("4711@nordwind.example", "rechnung@nordwind.example", "other bytes"u8).ShouldNotBe(a, "a mailer that reuses a Message-ID for another mail");
        MailKitImapMailSource.ContentMessageId("4711@nordwind.example", "someone@else.example", "x"u8).ShouldNotBe(a);
        var raw = MailKitImapMailSource.ContentMessageId(null, "a@b.example", "raw"u8);
        raw.ShouldStartWith("imap:sha:");
        MailKitImapMailSource.ContentMessageId("  ", "a@b.example", "raw"u8).ShouldBe(raw);
        MailKitImapMailSource.ContentMessageId(null, "a@b.example", "other"u8).ShouldNotBe(raw);
        a.Length.ShouldBeLessThan(998, "fits Ingestions.SourceMessageId");
    }

    [Fact]
    public void A_body_is_fetched_up_to_one_byte_past_the_limit()
    {
        MailKitImapMailSource.FetchLength(50L * 1024 * 1024).ShouldBe((50 * 1024 * 1024) + 1);
        MailKitImapMailSource.FetchLength(0).ShouldBe(1);
        MailKitImapMailSource.FetchLength(long.MaxValue - 1).ShouldBe(int.MaxValue);
    }

    [Fact]
    public void A_free_mail_domain_cannot_be_a_trusted_forwarder()
    {
        MailboxChannelConfig.TryParse("""{"address":"invoices@tenant-a.local","trustedForwarderDomains":["Gmail.com"]}""", [], out var errors).ShouldBeNull();
        errors.ShouldContain(e => e.Contains("free-mail", StringComparison.Ordinal));
        MailboxChannelConfig.TryParse("""{"address":"invoices@tenant-a.local","trustedForwarderDomains":["bureau.example"]}""", [], out _).ShouldNotBeNull();
    }

    [Fact]
    public void A_forward_attached_as_a_message_from_a_trusted_forwarder_is_unwrapped_to_its_original_sender()
    {
        var original = Message("Rechnungsversand <rechnungsversand@nordwind.example>", "Rechnung 4711", files: ("rechnung.pdf", "application/pdf", Pdf));
        var forward = Message("scans@bureau.example", "Fwd: Rechnung 4711", "mx.platform.local; dmarc=pass header.from=bureau.example");
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

        var forwarderFailed = Message("scans@bureau.example", "Fwd", "mx.platform.local; dmarc=fail header.from=bureau.example");
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
        var forward = Message("scans@bureau.example", "WG: Rechnung 4711", "mx.platform.local; dmarc=pass header.from=bureau.example", text, ("rechnung.pdf", "application/pdf", Pdf));

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
        MailMessageReader.Read(forward, new MailReadOptions(false, ["private.example"], [Receiver])).Sender.ShouldBe("someone@private.example");
        MailMessageReader.Read(forward, new MailReadOptions(true, ["private.example"], [Receiver])).Sender.ShouldBe("rechnungsversand@nordwind.example");
    }

    [Fact]
    public void The_text_body_is_not_a_document_and_a_nameless_attachment_gets_a_name()
    {
        var message = Message("a@b.example", "x");
        var builder = new BodyBuilder { TextBody = "body", HtmlBody = "<p>body</p>" };
        var part = new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream(Pdf)), ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) };
        builder.Attachments.Add(part);
        message.Body = builder.ToMessageBody();

        MailMessageReader.Read(message, TrustReceiver).Attachments.ShouldHaveSingleItem().FileName.ShouldBe("attachment-1");
    }

    [Fact]
    public void The_source_key_names_the_port_the_poller_connects_to()
    {
        // starttls without a port connects to 143, as does an explicit 143: one server, one key (and one channel).
        static MailboxChannelConfig Config(string json) => MailboxChannelConfig.TryParse(json, [], out var errors).ShouldNotBeNull(string.Join(" ", errors));
        var implicitStartTls = Config("""{"address":"ap@acme.example","host":"imap.example.com","security":"starttls"}""");
        var explicitStartTls = Config("""{"address":"ap@acme.example","host":"imap.example.com","port":143,"security":"starttls"}""");
        implicitStartTls.EffectivePort.ShouldBe(143);
        implicitStartTls.SourceKey.ShouldBe("imap:imap.example.com:143/ap@acme.example/inbox");
        explicitStartTls.SourceKey.ShouldBe(implicitStartTls.SourceKey);
        MailKitImapMailSource.Endpoint(implicitStartTls, "pw", new MailOptions(), out _).ShouldNotBeNull().Port.ShouldBe(143);
        MailKitImapMailSource.Endpoint(explicitStartTls, "pw", new MailOptions(), out _).ShouldNotBeNull().Port.ShouldBe(143);

        var implicitSsl = Config("""{"address":"ap@acme.example","host":"imap.example.com"}""");
        var explicitSsl = Config("""{"address":"ap@acme.example","host":"imap.example.com","port":993,"security":"ssl"}""");
        implicitSsl.SourceKey.ShouldBe("imap:imap.example.com:993/ap@acme.example/inbox");
        explicitSsl.SourceKey.ShouldBe(implicitSsl.SourceKey);
        MailKitImapMailSource.Endpoint(implicitSsl, "pw", new MailOptions(), out _).ShouldNotBeNull().Port.ShouldBe(993);
        Config("""{"address":"ap@acme.example","host":"imap.example.com","port":143}""").SourceKey.ShouldNotBe(implicitSsl.SourceKey, "another port is another key");
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
