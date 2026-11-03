using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocProc.Api.Auth;
using DocProc.Api.Documents;
using DocProc.Application.Abstractions;
using DocProc.Application.Configuration;
using DocProc.Application.Documents;
using DocProc.Domain.Documents;
using DocProc.Domain.Fields;
using DocProc.Domain.Text;
using DocProc.Domain.Validation;
using DocProc.Infrastructure.Persistence;
using DocProc.Infrastructure.Persistence.Entities;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;
using DocProc.Pipeline.Review;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocProc.Api.Endpoints;

/// <summary>
/// Map mode of docs/11 §12.3 and the table tool of §12.4 (P2, track A): the page's words with boxes for the selection
/// overlay (<c>/pages/{n}/words</c>, with the selection's text and its readings per kind), region OCR for a scan
/// (<c>regions:read</c>), and the table tool (<c>regions:table</c>: column proposal, one mapping applied to every page,
/// the table report, and the write of all rows as one correction with the report stored next to it).
/// </summary>
public static partial class DocumentEndpoints
{
    /// <summary>The problem code of <c>regions:read</c> when no OCR provider is configured.</summary>
    public const string OcrNotConfigured = "OCR_NOT_CONFIGURED";

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    private static void MapMapMode(RouteGroupBuilder documents)
    {
        documents.MapGet("/{id:guid}/pages/{n:int}/words", GetPageWordsAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentPageWords")
            .WithSummary("The words of a page with their boxes in page units (docs/11 §12.3); with bbox=x0,y0,x1,y1 the words inside it, their text in reading order and its readings per kind (date, number, amount, ...) in the locale (the tenant's country by default).");

        documents.MapPost("/{id:guid}/regions:read", ReadRegionAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(TableCapture.MaxReadRequestBytes))
            .RequireRateLimiting(TableCapture.RateLimitPolicy)
            .WithName("ReadDocumentRegion")
            .WithSummary("Reads the words inside a box of a page image with OCR (docs/11 §12.3), for a scan without a text layer there; 501 when no OCR provider is configured. A few reads run at a time per tenant (429 beyond).");

        documents.MapPost("/{id:guid}/regions:table", ReadTableAsync)
            .RequireAuthorization(Policies.Review)
            .WithMetadata(new RequestSizeLimitAttribute(TableCapture.MaxTableRequestBytes))
            .RequireRateLimiting(TableCapture.RateLimitPolicy)
            .WithName("ReadDocumentTable")
            .WithSummary("The table tool (docs/11 §12.4): proposes columns for a box, or reads the rows of a mapping drawn on one page over the pages named, with the table report per page; write: true stores the rows as one correction and the report. One read has a time budget (422 TABLE_READ_TOO_SLOW past it), and a few run at a time per tenant (429 beyond).");

        documents.MapGet("/{id:guid}/table-reports", GetTableReportsAsync)
            .RequireAuthorization(Policies.Read)
            .WithName("GetDocumentTableReports")
            .WithSummary("The stored table reports of the document's repeating groups (docs/11 §6.2 DocumentTableReport): the page strip after a table capture.");
    }

    private static async Task<Results<Ok<PageWordsResponse>, ValidationProblem, ProblemHttpResult>> GetPageWordsAsync(
        Guid id,
        int n,
        string? bbox,
        string? locale,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        BoundingBox? box = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',', StringSplitOptions.TrimEntries);
            var numbers = parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            var candidate = numbers.Length == 4 ? new SourceBox(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
            if (!TableCapture.IsBox(candidate))
            {
                errors["bbox"] = ["x0,y0,x1,y1 in page units (0 to 1000), with x0 < x1 and y0 < y1."];
            }
            else
            {
                box = TableCapture.Box(candidate!);
            }
        }

        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return NotFound(id);
        }

        var readLocale = await LocaleAsync(db, document.TenantId, locale, errors, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (n < 1 || n > Math.Max(document.PageCount, document.PageImageKeys.Count))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page not found", detail: $"Document {id} has {document.PageCount} page(s); page {n} does not exist.");
        }

        var layer = await TextLayerOrNullAsync(blobs, document, ct);
        var page = layer?.Pages.FirstOrDefault(p => p.Number == n);
        if (layer is null || page is null)
        {
            return TypedResults.Ok(new PageWordsResponse(n, WordSources.None, readLocale.Name, [], null, null, []));
        }

        var words = WordPage.From(page).Words;
        var source = layer.Source == TextLayerSource.Ocr ? WordSources.Ocr : WordSources.PdfText;
        return TypedResults.Ok(Words(n, source, readLocale, box is { } b ? PageGeometry.Hit(words, b) : words, selection: box is not null, provider: null));
    }

    private static async Task<Results<Ok<PageWordsResponse>, ValidationProblem, ProblemHttpResult>> ReadRegionAsync(
        Guid id,
        ReadRegionRequest request,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEnumerable<IOcrProvider> ocrProviders,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TableCapture.IsBox(request.BBox))
        {
            errors["bBox"] = ["The box lies within the page (0 to 1000 on both axes) with x0 < x1 and y0 < y1."];
        }

        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return NotFound(id);
        }

        var readLocale = await LocaleAsync(db, document.TenantId, request.Locale, errors, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (request.Page < 1 || request.Page > document.PageImageKeys.Count)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page not found", detail: $"Document {id} has {document.PageImageKeys.Count} rendered page(s); page {request.Page} does not exist.");
        }

        // docs/11 §12.3: the page image cut to the box, read by the configured IOcrProvider (Tesseract or Azure Document
        // Intelligence, docs/01). The repository ships no provider yet, so a host without one answers 501.
        var ocr = ocrProviders.FirstOrDefault();
        if (ocr is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status501NotImplemented,
                title: "OCR not configured",
                detail: "No OCR provider is configured on this host, so a region cannot be read from the page image; type the value instead.",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = OcrNotConfigured });
        }

        var png = await PageSources.ReadPageImageAsync(blobs, document, request.Page, ct);
        var region = png is null ? null : PageSources.Crop(png, TableCapture.Box(request.BBox));
        if (region is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Page image missing", detail: $"The rendered image of page {request.Page} is not in the derived store or does not decode.");
        }

        var recognized = await ocr.RecognizeAsync(request.Page, region.Png, [readLocale.Name[..2]], ct);
        var words = PageSources.ToPageUnits(region, recognized)
            .Select((w, i) => new IndexedWord(i, w.Text, w.Box, w.Confidence))
            .ToList();
        var response = Words(request.Page, WordSources.Ocr, readLocale, words, selection: true, ocr.Name);
        return TypedResults.Ok(response with { Words = [.. response.Words.Select(w => w with { Index = null })] });
    }

    /// <summary>
    /// The answer of <c>/words</c> and <c>regions:read</c>: every word of the page in the text layer's order, or, for a
    /// selection, its words in reading order with their joined text, their union and the text's readings per kind.
    /// </summary>
    private static PageWordsResponse Words(int page, string source, ValueLocale locale, IReadOnlyList<IndexedWord> words, bool selection, string? provider)
    {
        static PageWordDto Dto(IndexedWord w) => new(w.Index, w.Text, TableCapture.Box(w.Box), w.Confidence);
        if (!selection)
        {
            return new PageWordsResponse(page, source, locale.Name, [.. words.Select(Dto)], null, null, [], provider);
        }

        var ordered = PageGeometry.Lines(words).SelectMany(l => l.Words).ToList();
        var text = ordered.Count == 0 ? null : string.Join(' ', ordered.Select(w => w.Text));
        var union = PageGeometry.Union(ordered) is { } u ? TableCapture.Box(u) : null;
        var candidates = PrintedValues.Candidates(text, locale)
            .Select(c => new ValueCandidateDto(c.Kind, JsonSerializer.SerializeToElement(c.Value)))
            .ToList();
        return new PageWordsResponse(page, source, locale.Name, [.. ordered.Select(Dto)], text, union, candidates, provider);
    }

    private static async Task<Results<Ok<ReadTableResponse>, ValidationProblem, ProblemHttpResult>> ReadTableAsync(
        Guid id,
        ReadTableRequest request,
        [FromServices] DocProcDbContext db,
        [FromServices] IContainerBlobStore blobs,
        [FromServices] IEffectiveConfigResolver configs,
        [FromServices] ITenantFeatures features,
        [FromServices] DocumentValidator validator,
        [FromServices] IOptions<TableToolOptions> limits,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var budget = limits.Value.ReadBudget;
        var row = await DocumentQueries.FindForDetailAsync(db, id, track: request.Write, ct);
        if (row is null)
        {
            return NotFound(id);
        }

        var document = row.Document;
        if (request.Write && NotInReview(document, "write a table into it") is { } notInReview)
        {
            return notInReview;
        }

        if (document.Type.IsUnknown)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No type", detail: $"Document {id} has no type yet, so nothing says which fields a table fills; set its type first (POST /documents/{id}:set-type).");
        }

        var config = await configs.ResolveAsync(new EffectiveConfigRequest(document.TenantId, document.Type, document.SupplierId, document.DocumentTypeVersionId), ct);
        if (config.Type is not { Compiled: { } compiled } type)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No definition", detail: $"{document.Type} has no published definition this tenant can use, so nothing says which fields a table fills.");
        }

        var (target, targetError) = TableCapture.ResolveTarget(type, compiled, request.Path);
        if (target is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["path"] = [targetError!] });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var readLocale = await LocaleAsync(db, document.TenantId, request.Locale, errors, ct);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var layer = await TextLayerOrNullAsync(blobs, document, ct);
        if (layer is null || layer.Pages.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "No text layer", detail: $"Document {id} has no text layer, so there are no words to read a table from.");
        }

        var pages = layer.Pages.Select(WordPage.From).ToList();
        var pageCount = pages.Max(p => p.Number);
        if (request.Columns is not { Count: > 0 })
        {
            return Propose(request, target, pages, pageCount, readLocale, errors, budget, ct);
        }

        var spec = TableCapture.Build(request, target, pageCount, readLocale, errors);
        if (spec is null)
        {
            return TypedResults.ValidationProblem(errors);
        }

        TableResult result;
        try
        {
            result = ReadWithin(pages, spec, budget, ct);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { ["columns"] = [ex.Message] });
        }
        catch (TablePatternTimeoutException ex)
        {
            return PatternTooSlow(request, ex);
        }
        catch (TableReadTooSlowException)
        {
            return ReadTooSlow(budget);
        }

        var report = TableCapture.ToDto(result.Report);
        var columns = request.Columns;
        if (request.Write && result.Rows.Count == 0)
        {
            // A mapping that reads no row would replace every row with none: refused, so a wrong divider cannot wipe a list.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["columns"] = [$"The mapping read no rows ({report.RejectedRows} rejected); nothing was written. Check the dividers and fields without write first."],
            });
        }

        var rows = result.Rows.Take(TableCapture.MaxRowsInResponse).Select(TableCapture.ToDto).ToList();
        var header = spec.HeaderRow is { } h ? new TableBandDto(h.Y0, h.Y1) : null;
        if (!request.Write)
        {
            return TypedResults.Ok(new ReadTableResponse(target.Path, false, columns, header, spec.RowAnchor, report, rows, result.Rows.Count > rows.Count, false, Locale: readLocale.Name));
        }

        // The write (docs/11 §12.4): every row as one correction (op set on the group) through the definition patcher, the
        // report stored as the group's DocumentTableReport, then the checks inline as after any correction.
        var configurable = await features.IsEnabledAsync(document.TenantId, TenantFeature.ConfigurableTypes, ct);
        var definitionDriven = document.CanonicalSchemaVersion == DefinitionTree.SchemaVersion
                               || (string.IsNullOrWhiteSpace(document.CanonicalJson) && CanonicalStorage.IsDefinitionDriven(type, configurable));
        if (!definitionDriven)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Not a definition-driven document",
                detail: $"Document {id} holds the coded {document.Type} record, which the table tool does not write; add its rows with PATCH …/fields (op append), or switch the tenant to configurable types.");
        }

        var now = clock.GetUtcNow();
        var actor = DocumentQueries.ActorName(user);
        var tenantCurrency = await db.Tenants.AsNoTracking().Where(t => t.Id == document.TenantId).Select(t => t.DefaultCurrency).FirstOrDefaultAsync(ct);
        var source = new CorrectionSource(Method: "table", Page: spec.Page, BBox: request.BBox);
        CanonicalPatchResult patched;
        try
        {
            patched = DefinitionPatcher.SetRows(
                compiled.Descriptors,
                DefinitionTreeOf(document.CanonicalJson, compiled.Descriptors),
                target.Path,
                TableCapture.ToCaptured(result.Rows, spec.TierColumns),
                actor,
                now,
                tenantCurrency,
                typedRecord: document.Type.IsInvoiceFamily,
                reason: Clean(request.Reason, 500),
                source: source);
        }
        catch (CanonicalPatchException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [string.IsNullOrEmpty(ex.Path) ? "rows" : ex.Path] = [ex.Message] });
        }

        document.CanonicalJson = patched.Json;
        document.CanonicalSchemaVersion = DefinitionTree.SchemaVersion;
        document.ExtractionMethod ??= ExtractionMethod.Manual;
        document.UpdatedAt = now;
        DocumentRoleColumns.Refresh(document, type.Roles);
        await DocumentFieldIndexWriter.RefreshAsync(db, document, type.IndexedFields, ct);

        var applied = patched.Applied[0];
        var reportNode = JsonSerializer.SerializeToNode(report, ReportJson)!.AsObject();
        var correction = new Correction
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            FieldPath = applied.Path,
            Op = applied.Op,
            OldValueJson = applied.OldValueJson,
            NewValueJson = applied.NewValueJson,
            SourceJson = CorrectionSources.ToJson(applied.Source, new JsonObject { ["spec"] = TableCapture.Specification(request, target.Path, spec.RowAnchor), ["report"] = reportNode.DeepClone() }),
            DocumentTypeVersionId = document.DocumentTypeVersionId,
            By = actor,
            At = now,
            Reason = applied.Reason,
        };
        db.Corrections.Add(correction);
        await DocumentTableReports.ReplaceAsync(db, new DocumentTableReport
        {
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Path = target.Path,
            Origin = TableReportOrigin.ReviewTool,
            Correction = correction,
            ProducedAtVersion = document.Version + 1,
            ReportJson = reportNode.ToJsonString(),
            CreatedAt = now,
        }, ct);
        DocumentQueries.AddEvent(db, document, DocumentEventTypes.Corrected, null, actor, new
        {
            paths = new[] { target.Path },
            ops = new[] { CorrectionOps.Set },
            table = new { rows = report.Rows, rejectedRows = report.RejectedRows, pages = report.Pages.Count, pagesWithoutRows = report.PagesWithoutRows },
        }, now);
        if (!await TrySaveAsync(db, ct))
        {
            return ConcurrentChange(id);
        }

        await RecheckAsync(db, document, type, validator, now, ct);
        var detail = await FreshDetailAsync(db, id, configs, features, ct);
        return TypedResults.Ok(new ReadTableResponse(target.Path, false, columns, header, spec.RowAnchor, report, rows, result.Rows.Count > rows.Count, true, detail, readLocale.Name));
    }

    /// <summary>
    /// <c>regions:table</c> without columns (docs/11 §12.4 step 2): dividers from the word gaps of the box, each column named
    /// from the field whose label its header matches, and the rows they read on that page, with the request's group
    /// headings (the portal sends <c>groupHeader: {target: "category"}</c> when the row has a category, as it does for
    /// the preview), so a heading is not read as a row; a group header whose target a proposed column fills, or that the
    /// row cannot take, is left out of the proposal's readout.
    /// </summary>
    private static Results<Ok<ReadTableResponse>, ValidationProblem, ProblemHttpResult> Propose(ReadTableRequest request, TableCapture.Target target, List<WordPage> pages, int pageCount, ValueLocale locale, Dictionary<string, string[]> errors, TimeSpan budget, CancellationToken ct)
    {
        if (request.Write)
        {
            errors["columns"] = ["A table is written from a mapping: send its columns (propose them first without write)."];
        }

        if (!TableCapture.IsBox(request.BBox))
        {
            errors["bBox"] = ["The box lies within the page (0 to 1000 on both axes) with x0 < x1 and y0 < y1."];
        }

        var page = pages.FirstOrDefault(p => p.Number == request.Page);
        if (page is null)
        {
            errors["page"] = [$"The document has {pageCount} page(s) with text."];
        }

        if (request.HeaderRow is { } band && (!TableCapture.IsUnit(band.Y0) || !TableCapture.IsUnit(band.Y1) || band.Y0 >= band.Y1))
        {
            errors["headerRow"] = ["The header row lies within the page with y0 < y1."];
        }

        var tierPattern = TableCapture.TierPattern(request.TierColumns, errors);
        TableCapture.CheckLinePatterns(request, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var proposal = ColumnProposer.Propose(page!, TableCapture.Box(request.BBox), request.HeaderRow is { } h ? (h.Y0, h.Y1) : null, TableCapture.Proposable(target), tierPattern, locale);
        var columns = proposal.Columns.Select(c => new TableColumnDto(c.X0, c.X1, c.Field, c.Header, c.TierMinQuantity)).ToList();
        var header = proposal.HeaderRow is { } ph ? new TableBandDto(ph.Y0, ph.Y1) : null;
        var heading = request.GroupHeader is { } group
                      && columns.All(c => c.Field != group.Target)
                      && (group.When is null || columns.Any(c => c.Field == group.When))
                      && target.RowFields.Any(f => f.Key == group.Target && f.IsEnvelope && f.Kind is DocProc.Definitions.FieldKinds.String or DocProc.Definitions.FieldKinds.Text)
            ? group
            : null;
        var proposed = request with { Columns = columns, HeaderRow = header, Pages = null, Write = false, GroupHeader = heading };
        var anchor = TableCapture.DefaultAnchor(target, columns.Select(c => new TableColumn(c.X0, c.X1, c.Field)));
        var empty = new TableReportDto(0, 0, [request.Page], [request.Page], null, []);
        if (anchor is null || columns.Count == 0)
        {
            return TypedResults.Ok(new ReadTableResponse(target.Path, true, columns, header, null, empty, [], false, false, Locale: locale.Name));
        }

        var spec = TableCapture.Build(proposed with { RowAnchor = anchor }, target, pageCount, locale, new Dictionary<string, string[]>(StringComparer.Ordinal));
        if (spec is null)
        {
            return TypedResults.Ok(new ReadTableResponse(target.Path, true, columns, header, anchor, empty, [], false, false, Locale: locale.Name));
        }

        TableResult result;
        try
        {
            result = ReadWithin(pages, spec, budget, ct);
        }
        catch (TablePatternTimeoutException ex)
        {
            return PatternTooSlow(request, ex);
        }
        catch (TableReadTooSlowException)
        {
            return ReadTooSlow(budget);
        }

        var rows = result.Rows.Take(TableCapture.MaxRowsInResponse).Select(TableCapture.ToDto).ToList();
        return TypedResults.Ok(new ReadTableResponse(target.Path, true, columns, header, anchor, TableCapture.ToDto(result.Report), rows, result.Rows.Count > rows.Count, false, Locale: locale.Name));
    }

    /// <summary>
    /// The table engine run within <paramref name="budget"/> (all pages and patterns together) and while the client waits:
    /// a client that goes away cancels it, and a run past the budget ends in <see cref="TableReadTooSlowException"/>.
    /// </summary>
    private static TableResult ReadWithin(IReadOnlyList<WordPage> pages, ColumnTableSpec spec, TimeSpan budget, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budget > TimeSpan.Zero)
        {
            deadline.CancelAfter(budget);
        }
        else
        {
            deadline.Cancel();
        }
        try
        {
            return ColumnTableEngine.Extract(pages, spec, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TableReadTooSlowException();
        }
    }

    private static ProblemHttpResult ReadTooSlow(TimeSpan budget) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Table read too slow",
            detail: $"Reading the table took longer than {budget.TotalSeconds:0.#} s; read fewer pages, or use fewer or simpler skip and stop patterns.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = TableCapture.TooSlow });

    /// <summary>A <c>skipRows</c> or <c>stopAt</c> pattern that ran out of time on a line: refused under its key.</summary>
    private static ValidationProblem PatternTooSlow(ReadTableRequest request, TablePatternTimeoutException ex)
    {
        var key = request.SkipRows?.ToList().IndexOf(ex.Pattern) is >= 0 and var skip ? $"skipRows[{skip}]"
            : request.StopAt?.ToList().IndexOf(ex.Pattern) is >= 0 and var stop ? $"stopAt[{stop}]"
            : "skipRows";
        return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [key] = [ex.Message] });
    }

    /// <summary>A table read that ran past <see cref="TableToolOptions.ReadBudget"/>.</summary>
    private sealed class TableReadTooSlowException : Exception;

    private static async Task<Results<Ok<IReadOnlyList<DocumentTableReportDto>>, ProblemHttpResult>> GetTableReportsAsync(Guid id, [FromServices] DocProcDbContext db, CancellationToken ct)
    {
        if (!await db.Documents.AsNoTracking().AnyAsync(d => d.Id == id, ct))
        {
            return NotFound(id);
        }

        var stored = await db.DocumentTableReports.AsNoTracking().Where(r => r.DocumentId == id).OrderBy(r => r.Path).ToListAsync(ct);
        IReadOnlyList<DocumentTableReportDto> reports =
        [
            .. stored.Select(r => new DocumentTableReportDto(r.Path, r.Origin, r.CorrectionId, r.ProducedAtVersion, r.CreatedAt, JsonSerializer.Deserialize<TableReportDto>(r.ReportJson, ReportJson)!)),
        ];
        return TypedResults.Ok(reports);
    }

    /// <summary>The locale the request names, or the one of the tenant's country (the supplier's locale comes with supplier settings, P4).</summary>
    private static async Task<ValueLocale> LocaleAsync(DocProcDbContext db, Guid tenantId, string? requested, Dictionary<string, string[]> errors, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (ValueLocale.TryParse(requested, out var parsed))
            {
                return parsed;
            }

            errors["locale"] = ["A locale such as de-DE, en-GB or hr."];
            return ValueLocale.German;
        }

        var country = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.CountryCode).FirstOrDefaultAsync(ct);
        return ValueLocale.ForCountry(country);
    }

    private static async Task<TextLayer?> TextLayerOrNullAsync(IContainerBlobStore blobs, Document document, CancellationToken ct)
    {
        try
        {
            return await PageSources.ReadTextLayerAsync(blobs, document, ct);
        }
        catch (BlobNotFoundException)
        {
            return null;
        }
    }
}
