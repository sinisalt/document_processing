using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocProc.Api.Tests;

public sealed class IngestionEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Uri IngestionsUri = new("/api/v1/ingestions", UriKind.Relative);
    private static readonly Uri DocumentsUri = new("/api/v1/documents", UriKind.Relative);
    private static readonly string TenantA = ApiFactory.TenantA.ToString();
    private static readonly string TenantB = ApiFactory.TenantB.ToString();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ReviewerA() => factory.ClientFor(new TokenSpec("reviewer-a", [Roles.Reviewer], TenantA));

    private static MultipartFormDataContent Multipart(params (string FileName, string ContentType, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (fileName, contentType, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(part, "files", fileName);
        }

        return content;
    }

    [Fact]
    public async Task Multipart_upload_stores_items_queues_the_ingestion_and_stays_inside_the_tenant()
    {
        using var reviewer = ReviewerA();
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 invoice " + Guid.NewGuid());
        var xml = Encoding.UTF8.GetBytes("<Invoice xmlns=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\"/>");
        using var body = Multipart(("Rechnung 42.pdf", "application/pdf", pdf), ("rechnung.xml", "application/xml", xml));

        var response = await reviewer.PostAsync(IngestionsUri, body, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await response.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;
        response.Headers.Location!.ToString().ShouldBe($"/api/v1/ingestions/{created.Id}");
        created.Status.ShouldBe(IngestionStatus.Received);
        created.Items.Count.ShouldBe(2);
        created.Items[0].FileName.ShouldBe("Rechnung 42.pdf");
        created.Items[0].SizeBytes.ShouldBe(pdf.Length);
        created.Items[0].Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)));

        factory.Queue.Ingestions.ShouldContain(m => m.IngestionId == created.Id && m.TenantId == ApiFactory.TenantA);

        var stored = factory.Query(db => db.Ingestions.Include(i => i.Items).Include(i => i.Channel).AsNoTracking().Single(i => i.Id == created.Id));
        stored.TenantId.ShouldBe(ApiFactory.TenantA);
        stored.SourceFrom.ShouldBe("reviewer-a");
        stored.SourceFileName.ShouldBe("Rechnung 42.pdf");
        stored.Channel!.Type.ShouldBe(ChannelType.ApiUpload);
        stored.Channel.Name.ShouldBe("API upload");
        stored.Items.Single(i => i.FileName == "rechnung.xml").Kind.ShouldBe(ItemKind.Xml);
        stored.Items.Single(i => i.FileName == "Rechnung 42.pdf").Kind.ShouldBe(ItemKind.Pdf);
        stored.Items.ShouldAllBe(i => i.Disposition == ItemDisposition.Candidate && i.Blob.Container == "raw");

        var detail = (await reviewer.GetFromJsonAsync<IngestionDetail>(new Uri($"/api/v1/ingestions/{created.Id}", UriKind.Relative), ApiFactory.Json, Ct))!;
        detail.Items.Count.ShouldBe(2);
        detail.Items.ShouldAllBe(i => i.Documents.Count == 0);

        var page = (await reviewer.GetFromJsonAsync<Page<IngestionSummary>>(IngestionsUri, ApiFactory.Json, Ct))!;
        page.Items.ShouldContain(i => i.Id == created.Id && i.ItemCount == 2);

        // Tenant B neither sees the ingestion nor finds it by id; the channel created on the fly belongs to A only.
        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        (await tenantB.GetAsync(new Uri($"/api/v1/ingestions/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenantB.GetFromJsonAsync<Page<IngestionSummary>>(IngestionsUri, ApiFactory.Json, Ct))!.Items.ShouldNotContain(i => i.Id == created.Id);

        // A platform operator sees it without a tenant header.
        using var operatorClient = factory.ClientFor(new TokenSpec("ops", [Roles.Operator]));
        (await operatorClient.GetAsync(new Uri($"/api/v1/ingestions/{created.Id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Json_upload_accepts_a_single_base64_file()
    {
        using var reviewer = ReviewerA();
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7 json upload " + Guid.NewGuid());

        var response = await reviewer.PostAsJsonAsync(IngestionsUri, new IngestionJsonUpload("scan.pdf", "application/pdf", Convert.ToBase64String(bytes), null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await response.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;
        created.Items.ShouldHaveSingleItem().SizeBytes.ShouldBe(bytes.Length);
        factory.Queue.Ingestions.ShouldContain(m => m.IngestionId == created.Id);

        var bad = await reviewer.PostAsJsonAsync(IngestionsUri, new IngestionJsonUpload("scan.pdf", "application/pdf", "not base64!", null), Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("base64");
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_response_and_refuses_a_different_body()
    {
        using var reviewer = ReviewerA();
        var key = "upload-" + Guid.NewGuid().ToString("N");
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 idempotent " + Guid.NewGuid());

        using var first = Multipart(("a.pdf", "application/pdf", bytes));
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, IngestionsUri) { Content = first };
        firstRequest.Headers.Add("Idempotency-Key", key);
        var firstResponse = await reviewer.SendAsync(firstRequest, Ct);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await firstResponse.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;

        using var second = Multipart(("a.pdf", "application/pdf", bytes));
        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, IngestionsUri) { Content = second };
        secondRequest.Headers.Add("Idempotency-Key", key);
        var secondResponse = await reviewer.SendAsync(secondRequest, Ct);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        secondResponse.Headers.Location!.ToString().ShouldBe($"/api/v1/ingestions/{created.Id}");
        var replayed = (await secondResponse.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;
        JsonSerializer.Serialize(replayed, ApiFactory.Json).ShouldBe(JsonSerializer.Serialize(created, ApiFactory.Json));

        factory.Query(db => db.Ingestions.Count(i => i.Id == created.Id)).ShouldBe(1);
        factory.Queue.Ingestions.Count(m => m.IngestionId == created.Id).ShouldBe(1, "a replay does not queue the ingestion again");

        using var different = Multipart(("b.pdf", "application/pdf", Encoding.ASCII.GetBytes("other bytes")));
        using var differentRequest = new HttpRequestMessage(HttpMethod.Post, IngestionsUri) { Content = different };
        differentRequest.Headers.Add("Idempotency-Key", key);
        (await reviewer.SendAsync(differentRequest, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The same key is tenant-scoped: tenant B gets its own ingestion.
        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        using var fromB = Multipart(("a.pdf", "application/pdf", bytes));
        using var requestB = new HttpRequestMessage(HttpMethod.Post, IngestionsUri) { Content = fromB };
        requestB.Headers.Add("Idempotency-Key", key);
        var responseB = await tenantB.SendAsync(requestB, Ct);
        responseB.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await responseB.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!.Id.ShouldNotBe(created.Id);
    }

    [Fact]
    public async Task Ingest_scoped_api_key_can_upload_but_cannot_read_documents()
    {
        var key = factory.InsertKey(ApiFactory.TenantA, "scanner", ApiKeyScope.Ingest);
        using var machine = factory.ClientForApiKey(key);
        using var body = Multipart(("scan.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 from scanner " + Guid.NewGuid())));

        var upload = await machine.PostAsync(IngestionsUri, body, Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var created = (await upload.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!;
        factory.Query(db => db.Ingestions.AsNoTracking().Single(i => i.Id == created.Id)).SourceFrom.ShouldBe("scanner");

        (await machine.GetAsync(DocumentsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await machine.GetAsync(IngestionsUri, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Uploads_need_a_tenant_the_ingest_policy_and_a_non_empty_file()
    {
        using var viewer = factory.ClientFor(new TokenSpec("viewer", [Roles.Viewer], TenantA));
        using var viewerBody = Multipart(("a.pdf", "application/pdf", [1, 2, 3]));
        (await viewer.PostAsync(IngestionsUri, viewerBody, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var platform = factory.ClientFor(new TokenSpec("superadmin", [Roles.SuperAdmin]));
        using var platformBody = Multipart(("a.pdf", "application/pdf", [1, 2, 3]));
        (await platform.PostAsync(IngestionsUri, platformBody, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var reviewer = ReviewerA();
        using var empty = Multipart(("empty.pdf", "application/pdf", []));
        var emptyResponse = await reviewer.PostAsync(IngestionsUri, empty, Ct);
        emptyResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await emptyResponse.Content.ReadAsStringAsync(Ct)).ShouldContain("empty");

        using var noFiles = new MultipartFormDataContent { { new StringContent("x"), "channelId" } };
        (await reviewer.PostAsync(IngestionsUri, noFiles, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var text = new StringContent("hello", Encoding.UTF8, "text/plain");
        (await reviewer.PostAsync(IngestionsUri, text, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        using var unknownChannel = Multipart(("a.pdf", "application/pdf", [1, 2, 3]));
        unknownChannel.Add(new StringContent(Guid.NewGuid().ToString()), "channelId");
        var unknownResponse = await reviewer.PostAsync(IngestionsUri, unknownChannel, Ct);
        unknownResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await unknownResponse.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors").TryGetProperty("channelId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Ingestion_list_is_newest_first_and_cursor_paginated()
    {
        using var tenantB = factory.ClientFor(new TokenSpec("reviewer-b", [Roles.Reviewer], TenantB));
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            using var body = Multipart(($"page-{i}.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF " + Guid.NewGuid())));
            var response = await tenantB.PostAsync(IngestionsUri, body, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            ids.Add((await response.Content.ReadFromJsonAsync<IngestionCreated>(ApiFactory.Json, Ct))!.Id);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await tenantB.GetFromJsonAsync<Page<IngestionSummary>>(new Uri($"/api/v1/ingestions?limit=2&cursor={cursor}", UriKind.Relative), ApiFactory.Json, Ct))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        seen.ShouldBeUnique();
        seen.Take(3).ShouldBe(((IEnumerable<Guid>)ids).Reverse());

        (await tenantB.GetAsync(new Uri("/api/v1/ingestions?cursor=garbage", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
