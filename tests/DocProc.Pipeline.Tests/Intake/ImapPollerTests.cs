using System.Globalization;
using DocProc.Application.Abstractions;
using DocProc.Application.Routing;
using DocProc.Domain.Flags;
using DocProc.Infrastructure.Mail;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Pipeline.Intake;
using DocProc.Pipeline.Tests.Routing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace DocProc.Pipeline.Tests.Intake;

/// <summary>A GreenMail container (the development mailbox of docs/09) the IMAP poller tests read.</summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public const int ImapPort = 3143;
    public const string Users = "invoices@tenant-a.local:invoices,invoices@tenant-b.local:invoices,routed@tenant-a.local:invoices";

    private IContainer? _container;

    public bool Available { get; private set; }

    public string? Reason { get; private set; }

    public string Host => _container!.Hostname;

    public int Port => _container!.GetMappedPublicPort(ImapPort);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("greenmail/standalone:2.1.15")
                .WithEnvironment("GREENMAIL_OPTS", $"-Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled -Dgreenmail.users={Users}")
                .WithPortBinding(ImapPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
                .Build();
            using var start = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _container.StartAsync(start.Token);
            await WaitForImapAsync();
            Available = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Reason = e.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Puts a message into a GreenMail mailbox with IMAP APPEND (the mailbox exists for every configured user).</summary>
    public async Task AppendAsync(string user, MimeMessage message)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(Host, Port, SecureSocketOptions.None);
        await client.AuthenticateAsync(user, "invoices");
        await client.Inbox.AppendAsync(message);
        await client.DisconnectAsync(true);
    }

    private async Task WaitForImapAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var client = new ImapClient();
                await client.ConnectAsync(Host, Port, SecureSocketOptions.None);
                await client.DisconnectAsync(true);
                return;
            }
            catch (Exception) when (attempt < 60)
            {
                await Task.Delay(500);
            }
        }
    }
}

/// <summary>
/// The IMAP poller against GreenMail (docs/11 §7.1): each message ingested once (UID tracking), attachments as items,
/// the mailbox and recipients recorded, forward unwrapping, the DMARC result in the source context with the source
/// rules acting on it, the channel's secret, and a mailbox of a tenant without <c>sourceRouting</c> left unread.
/// </summary>
public sealed class ImapPollerTests(GreenMailFixture mail) : IClassFixture<GreenMailFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Each_message_is_ingested_once_with_its_source_facts_and_its_attachments_as_items()
    {
        SkipUnlessAvailable();
        await using var host = await StartAsync();
        var mailbox = $"box-{Guid.NewGuid():N}"[..16] + "@tenant-a.local";
        var channel = await FolderWatcherTests.AddChannelAsync(host, PipelineTestHost.TenantA, $$"""{"address":"{{mailbox}}"}""", ChannelType.Mailbox);
        await mail.AppendAsync(mailbox, MailMessageReaderTests.Message("Rechnungsversand <rechnungsversand@nordwind.example>", "Rechnung 1", "mx.tenant-a.local; dmarc=pass header.from=nordwind.example", files: ("rechnung-1.pdf", "application/pdf", MailMessageReaderTests.Pdf)));
        await mail.AppendAsync(mailbox, MailMessageReaderTests.Message("rechnungsversand@nordwind.example", "Rechnung 2", files: [("a.pdf", "application/pdf", "%PDF-1.4 a"u8.ToArray()), ("b.xml", "application/xml", "<x/>"u8.ToArray())]));
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(2);
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0, "the cursor moved past both");
        await mail.AppendAsync(mailbox, MailMessageReaderTests.Message("rechnungsversand@nordwind.example", "Rechnung 3"));
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1, "only the new one");

        var ingestions = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().Include(i => i.Items).Where(i => i.ChannelId == channel).ToListAsync());
        ingestions.Count.ShouldBe(3);
        var first = ingestions.Single(i => i.SourceSubject == "Rechnung 1");
        first.SourceSender.ShouldBe("rechnungsversand@nordwind.example");
        first.SourceMailbox.ShouldBe(mailbox);
        first.SourceRecipientsJson.ShouldNotBeNull().ShouldContain("nordwind@tenant-a.local");
        first.SourceMessageId.ShouldNotBeNull().ShouldStartWith("imap:");
        first.RawBlob.ContentType.ShouldBe("message/rfc822");
        first.Items.ShouldHaveSingleItem().FileName.ShouldBe("rechnung-1.pdf");
        SenderAuthentication.Parse(first.SenderAuthJson)!.Verified.ShouldBeTrue();
        ingestions.Single(i => i.SourceSubject == "Rechnung 2").Items.Select(i => i.FileName).Order().ShouldBe(["a.pdf", "b.xml"]);
        ingestions.Single(i => i.SourceSubject == "Rechnung 3").Items.ShouldBeEmpty("a mail without attachments is recorded, with nothing to process");

        var stored = await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().SingleAsync(c => c.Id == channel));
        stored.Cursor.ShouldNotBeNull().ShouldEndWith(":3");
        stored.LastError.ShouldBeNull();
        stored.LastPolledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_lost_cursor_does_not_ingest_a_message_twice()
    {
        SkipUnlessAvailable();
        await using var host = await StartAsync();
        var mailbox = $"cur-{Guid.NewGuid():N}"[..16] + "@tenant-a.local";
        var channel = await FolderWatcherTests.AddChannelAsync(host, PipelineTestHost.TenantA, $$"""{"address":"{{mailbox}}"}""", ChannelType.Mailbox);
        await mail.AppendAsync(mailbox, MailMessageReaderTests.Message("a@nordwind.example", "eins", files: ("1.pdf", "application/pdf", MailMessageReaderTests.Pdf)));
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(1);

        // A second host, or a crash before the cursor was saved: the cursor is back where it was.
        await host.WithDbAsync(async db =>
        {
            var row = await db.IngestionChannels.SingleAsync(c => c.Id == channel);
            row.Cursor = null;
            await db.SaveChangesAsync();
        });

        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(0);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().CountAsync(i => i.ChannelId == channel))).ShouldBe(1);
        (await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().SingleAsync(c => c.Id == channel))).Cursor.ShouldNotBeNull("the cursor moved on again");
    }

    [Fact]
    public async Task A_forward_from_a_trusted_forwarder_is_routed_by_its_original_sender_and_a_DMARC_failure_is_not()
    {
        SkipUnlessAvailable();
        await using var host = await StartAsync();
        var nordwind = await host.AddSupplierAsync(PipelineTestHost.TenantA, "Nordwind Großhandel GmbH");
        await SourceRoutingPipelineTests.AddRuleAsync(host, nordwind.Id, r => r.Sender = "rechnungsversand@nordwind.example");
        var mailbox = $"fwd-{Guid.NewGuid():N}"[..16] + "@tenant-a.local";
        var channel = await FolderWatcherTests.AddChannelAsync(host, PipelineTestHost.TenantA, $$"""{"address":"{{mailbox}}","trustedForwarderDomains":["bureau.example"]}""", ChannelType.Mailbox);

        var original = MailMessageReaderTests.Message("rechnungsversand@nordwind.example", "Rechnung 77", files: ("rechnung-77.pdf", "application/pdf", "%PDF-1.4 seventy-seven"u8.ToArray()));
        var forward = MailMessageReaderTests.Message("scans@bureau.example", "Fwd: Rechnung 77", "mx.tenant-a.local; dmarc=pass header.from=bureau.example");
        var body = new BodyBuilder { TextBody = "weitergeleitet" };
        body.Attachments.Add(new MessagePart { Message = original });
        forward.Body = body.ToMessageBody();
        await mail.AppendAsync(mailbox, forward);

        var spoofed = MailMessageReaderTests.Message("rechnungsversand@nordwind.example", "Rechnung 78", "mx.tenant-a.local; spf=fail; dmarc=fail header.from=nordwind.example", files: ("rechnung-78.pdf", "application/pdf", "%PDF-1.4 seventy-eight"u8.ToArray()));
        await mail.AppendAsync(mailbox, spoofed);

        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();
        (await poller.PollChannelAsync(PipelineTestHost.TenantA, channel, Ct)).ShouldBe(2);
        await host.RunUntilIdleAsync();

        var ingestions = await host.WithDbAsync(db => db.Ingestions.AsNoTracking().Where(i => i.ChannelId == channel).ToListAsync());
        var unwrapped = ingestions.Single(i => i.SourceSubject == "Rechnung 77");
        unwrapped.SourceSender.ShouldBe("rechnungsversand@nordwind.example");
        unwrapped.ForwardedBy.ShouldBe("scans@bureau.example");
        var routed = await host.SingleDocumentAsync(unwrapped.Id);
        routed.SupplierId.ShouldBe(nordwind.Id);
        routed.SupplierMatchMethod.ShouldBe(SupplierMatchMethod.SourceRule);

        var failed = await host.SingleDocumentAsync(ingestions.Single(i => i.SourceSubject == "Rechnung 78").Id);
        failed.SupplierMatchMethod.ShouldNotBe(SupplierMatchMethod.SourceRule, "DMARC failed, so the sender rule does not apply");
        failed.Flags.ShouldContain(f => f.Code == FlagCodes.SupplierSourceUnverified);
    }

    [Fact]
    public async Task The_channels_secret_is_the_password_and_a_tenant_without_sourceRouting_is_not_polled()
    {
        SkipUnlessAvailable();
        await using var host = await StartAsync();
        var mailbox = $"sec-{Guid.NewGuid():N}"[..16] + "@tenant-a.local";
        var channel = Guid.CreateVersion7();
        await host.WithDbAsync(async db =>
        {
            db.IngestionChannels.Add(new IngestionChannel { Id = channel, TenantId = PipelineTestHost.TenantA, Name = "Mail", Type = ChannelType.Mailbox, ConfigJson = $$"""{"address":"{{mailbox}}"}""", SecretRef = ChannelSecrets.Reference(PipelineTestHost.TenantA, channel), CreatedAt = host.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        await host.Services.GetRequiredService<ISecretStore>().SetAsync(ChannelSecrets.Reference(PipelineTestHost.TenantA, channel), "invoices", Ct);
        var tenantB = await FolderWatcherTests.AddChannelAsync(host, Infrastructure.Persistence.DevSeed.TenantB, """{"address":"invoices@tenant-b.local"}""", ChannelType.Mailbox);
        await mail.AppendAsync(mailbox, MailMessageReaderTests.Message("a@nordwind.example", "eins", files: ("1.pdf", "application/pdf", MailMessageReaderTests.Pdf)));
        await mail.AppendAsync("invoices@tenant-b.local", MailMessageReaderTests.Message("a@nordwind.example", "für B", files: ("b.pdf", "application/pdf", MailMessageReaderTests.Pdf)));

        (await host.Services.GetRequiredService<ChannelPollerHostedService>().PollOnceAsync(Ct)).ShouldBe(1);
        (await host.WithDbAsync(db => db.Ingestions.AsNoTracking().CountAsync(i => i.ChannelId == tenantB))).ShouldBe(0);
    }

    [Fact]
    public async Task An_unreachable_or_refused_server_is_the_channels_last_error()
    {
        SkipUnlessAvailable();
        await using var host = await StartAsync(settings => settings["Mail:ImapPort"] = "1");
        var channel = await FolderWatcherTests.AddChannelAsync(host, PipelineTestHost.TenantA, """{"address":"invoices@tenant-a.local"}""", ChannelType.Mailbox);
        var poller = host.Services.GetRequiredService<ChannelPollerHostedService>();

        (await poller.PollOnceAsync(Ct)).ShouldBe(0);

        (await host.WithDbAsync(db => db.IngestionChannels.AsNoTracking().SingleAsync(c => c.Id == channel))).LastError.ShouldNotBeNull().ShouldContain("could not be reached");
    }

    private async Task<PipelineTestHost> StartAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var host = await PipelineTestHost.StartAsync(settings =>
        {
            settings["Intake:PollersEnabled"] = "false";
            settings["Mail:ImapHost"] = mail.Host;
            settings["Mail:ImapPort"] = mail.Port.ToString(CultureInfo.InvariantCulture);
            settings["Mail:ImapSecurity"] = "None";
            settings["Mail:Timeout"] = "00:00:10";
            configure?.Invoke(settings);
        });
        await SourceRoutingPipelineTests.EnableRoutingAsync(host, PipelineTestHost.TenantA);
        return host;
    }

    private void SkipUnlessAvailable()
    {
        if (!mail.Available)
        {
            Assert.Skip($"The GreenMail container did not start (Docker is needed): {mail.Reason}");
        }
    }
}
