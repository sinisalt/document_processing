using System.Text.RegularExpressions;
using DocProc.Definitions;
using DocProc.Domain.Fields;
using DocProc.Mapping.Tables;
using DocProc.Mapping.Text;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tests;

/// <summary>
/// The table engine of the review table tool (docs/11 §12.4 "apply to all pages", the <c>column-table</c> strategy of
/// §8.6) on synthetic price-list layouts: one mapping drawn on page 1, applied to every page, with header repetition,
/// pages without a header, page breaks, continuation lines, group headings, tier columns, rejected rows, skipped and
/// stop lines, page overrides and the table report.
/// </summary>
public sealed class ColumnTableEngineTests
{
    private static decimal Amount(TableCell? cell) => cell.ShouldNotBeNull().Value.ShouldNotBeNull().GetValue<decimal>();

    private static TableResult Read(IReadOnlyList<WordPage> pages, ColumnTableSpec spec) => ColumnTableEngine.Extract(pages, spec, TestContext.Current.CancellationToken);

    [Fact]
    public void Reads_the_rows_of_the_mapping_page_with_values_by_kind_and_their_boxes()
    {
        var page = new PageBuilder(1).Text(55, 40, "Nordwind Preisliste 2027").Header(100)
            .Article(120, "100234", "Rioja Crianza", "12,99", "11,50", "10,90")
            .Article(135, "100235", "Rioja Reserva", "1.234,50")
            .Text(55, 960, "Seite 1 von 1")
            .Words();

        var result = Read([page], PriceList.Spec());

        result.Rows.Count.ShouldBe(2);
        var first = result.Rows[0];
        first.Cell("supplierSku").ShouldNotBeNull().Value!.GetValue<string>().ShouldBe("100234");
        first.Cell("description").ShouldNotBeNull().Text.ShouldBe("Rioja Crianza");
        first.Cell("packDescription").ShouldNotBeNull().Text.ShouldBe("6 x 0,75 l");
        Amount(first.Cell("price")).ShouldBe(12.99m);
        Amount(result.Rows[1].Cell("price")).ShouldBe(1234.50m);
        first.Page.ShouldBe(1);
        var sku = first.Cell("supplierSku")!;
        sku.Box.ShouldBe(new BoundingBox(55, 120, 91, 130));
        first.Box.Y0.ShouldBe(120);

        // Tier columns: "ab 10" and "ab 50" become tiers with the quantity from the header; an empty cell gives none.
        first.Tiers.Select(t => (t.MinQuantity, Amount(t.Price))).ShouldBe([(10m, 11.50m), (50m, 10.90m)]);
        result.Rows[1].Tiers.ShouldBeEmpty();

        // The title above the box and the footer below it are not rows.
        var report = result.Report;
        report.Rows.ShouldBe(2);
        report.RejectedRows.ShouldBe(0);
        report.Pages.ShouldBe([1]);
        var p1 = report.PerPage.ShouldHaveSingleItem();
        p1.HeaderFound.ShouldBeTrue();
        p1.ColumnsFrom.ShouldBe(ColumnsFrom.Mapping);
        p1.FirstRows[0]["supplierSku"].ShouldBe("100234");
        p1.FirstRows[0]["tiers:10"].ShouldBe("11,50");
        p1.Columns.Count.ShouldBe(7);
    }

    [Fact]
    public void Applies_one_mapping_to_every_page_re_measuring_shifted_and_scaled_headers_and_reusing_them_on_pages_without_one()
    {
        var page1 = new PageBuilder(1).Text(55, 40, "Preisliste").Header(100).Article(120, "1001", "Alpha", "1,00").Article(135, "1002", "Beta", "2,00");

        // Page 2 is printed 22 units further right; its header repeats at another height.
        var page2 = new PageBuilder(2).Text(400, 20, "Nordwind Preisliste Seite 2").Header(60, shift: 22).Article(80, "2001", "Gamma", "3,00", shift: 22).Article(95, "2002", "Delta", "4,00", shift: 22);

        // Page 3 has no header and the same shift: the dividers of page 2 carry over.
        var page3 = new PageBuilder(3).Article(40, "3001", "Epsilon", "5,00", shift: 22).Article(55, "3002", "Zeta", "6,00", shift: 22);

        // Page 4 is scaled down to 95 % and shifted: the fit of the header centres takes both.
        var page4 = new PageBuilder(4).Header(60, shift: 10, scale: 0.95).Article(80, "4001", "Eta", "7,00", shift: 10, scale: 0.95);

        var spec = PriceList.Spec(pages: [1, 2, 3, 4]);
        var result = Read([page1.Words(), page2.Words(), page3.Words(), page4.Words()], spec);

        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "1002", "2001", "2002", "3001", "3002", "4001"]);
        result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta"]);
        result.Rows.Select(r => Amount(r.Cell("price"))).ShouldBe([1m, 2m, 3m, 4m, 5m, 6m, 7m]);
        result.Rows.All(r => r.Cell("unit")!.Text == "KRT").ShouldBeTrue();

        var report = result.Report;
        report.PerPage.Select(p => (p.Page, p.HeaderFound, p.ColumnsFrom, p.Rows)).ShouldBe(
        [
            (1, true, ColumnsFrom.Mapping, 2),
            (2, true, ColumnsFrom.Header, 2),
            (3, false, ColumnsFrom.PreviousHeader, 2),
            (4, true, ColumnsFrom.Header, 1),
        ]);
        report.PerPage[1].Columns[0].X0.ShouldBe(72, 0.5);
        report.PerPage[2].Columns.ShouldBe(report.PerPage[1].Columns);
        report.PerPage[3].Columns[4].X0.ShouldBe((700 * 0.95) + 10, 1.5);

        // The running page title above page 2's header is above the header, so it is not read.
        report.RejectedRows.ShouldBe(0);
    }

    [Fact]
    public void Continuation_lines_are_appended_also_across_a_page_break()
    {
        var page1 = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Grauburgunder trocken", "8,50")
            .Line(135, (PriceList.Description, "Baden, Kaiserstuhl"))
            .Article(150, "1002", "Spätburgunder", "9,50");
        var page2 = new PageBuilder(2).Header(60)
            .Line(80, (PriceList.Description, "Holzfass gereift"))
            .Article(95, "2001", "Riesling", "7,00");

        var result = Read([page1.Words(), page2.Words()], PriceList.Spec(pages: [1, 2]));

        result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Grauburgunder trocken Baden, Kaiserstuhl", "Spätburgunder Holzfass gereift", "Riesling"]);
        result.Rows[1].Page.ShouldBe(1, "a row counts for the page it starts on");
        result.Report.PerPage.Select(p => (p.Rows, p.ContinuationLines)).ShouldBe([(2, 1), (1, 1)]);

        // The cell's box stays on the page the row starts on.
        result.Rows[1].Cell("description")!.Box.Y0.ShouldBe(150);
    }

    [Fact]
    public void Group_headings_fill_the_category_of_the_rows_below_them()
    {
        var page = new PageBuilder(1).Header(100)
            .Text(PriceList.Sku, 120, "Rotweine aus Spanien")
            .Article(135, "1001", "Rioja Crianza", "12,99")
            .Line(150, (PriceList.Description, "Barrique"))
            .Article(165, "1002", "Rioja Reserva", "18,99")
            .Text(470, 180, "Weißweine")
            .Article(195, "2001", "Albariño", "11,49");

        var result = Read([page.Words()], PriceList.Spec());

        result.Rows.Count.ShouldBe(3);
        result.Rows.Select(r => r.Cell("category")?.Text).ShouldBe(["Rotweine aus Spanien", "Rotweine aus Spanien", "Weißweine"]);
        result.Rows[0].Cell("description")!.Text.ShouldBe("Rioja Crianza Barrique", "a description line under a row is a continuation, not a heading");
        result.Rows[0].Cell("category")!.Box.Y0.ShouldBe(120);
        result.Report.PerPage[0].GroupHeadings.ShouldBe(2, "a heading printed across a divider is a heading even under a row");
        result.Report.Rows.ShouldBe(3);
    }

    [Fact]
    public void A_row_with_a_value_that_does_not_read_is_rejected_with_its_text_and_reason()
    {
        var page = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00")
            .Article(135, "1002", "Beta", "12,9x")
            .Article(150, "1003", "Gamma", "3,00", tier10: "zwei")
            .Article(165, "1004", "Delta", "4,00");

        var result = Read([page.Words()], PriceList.Spec());

        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "1004"]);
        var p1 = result.Report.PerPage.ShouldHaveSingleItem();
        p1.Rejected.ShouldBe(2);
        result.Report.RejectedRows.ShouldBe(2);
        p1.RejectedLines[0].Reason.ShouldBe(RejectionReasons.Unreadable);
        p1.RejectedLines[0].Field.ShouldBe("price");
        p1.RejectedLines[0].Text.ShouldContain("12,9x");
        p1.RejectedLines[0].Message.ShouldContain("not an amount");
        p1.RejectedLines[0].Box.Y0.ShouldBe(135);
        p1.RejectedLines[1].Field.ShouldBe("tiers");
    }

    [Fact]
    public void A_field_pattern_and_enum_options_reject_rows_that_do_not_fit()
    {
        var fields = new Dictionary<string, TableField>(PriceList.Fields, StringComparer.Ordinal)
        {
            ["supplierSku"] = new("supplierSku", FieldKinds.Identifier, new Regex("^[0-9]{4}$", RegexOptions.None, TimeSpan.FromMilliseconds(100))),
        };
        var page = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00").Article(135, "AB-12", "Beta", "2,00");

        var result = Read([page.Words()], PriceList.Spec() with { Fields = fields });

        result.Rows.ShouldHaveSingleItem();
        result.Report.PerPage[0].RejectedLines.ShouldHaveSingleItem().Message.ShouldContain("pattern");
    }

    [Fact]
    public void Skip_rows_are_dropped_and_stop_at_ends_the_table()
    {
        var page1 = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00")
            .Text(PriceList.Description, 135, "Übertrag 1,00");
        var page2 = new PageBuilder(2).Header(60)
            .Text(PriceList.Description, 80, "Übertrag 1,00")
            .Article(95, "2001", "Beta", "2,00")
            .Text(PriceList.Sku, 110, "Ende der Preisliste")
            .Article(125, "2002", "Not a row", "3,00");
        var page3 = new PageBuilder(3).Header(60).Article(80, "3001", "Never read", "4,00");
        var spec = PriceList.Spec(pages: [1, 2, 3]) with
        {
            SkipRows = [new Regex("^Übertrag", RegexOptions.None, TimeSpan.FromMilliseconds(100))],
            StopAt = [new Regex("Ende der Preisliste", RegexOptions.None, TimeSpan.FromMilliseconds(100))],
        };

        var result = Read([page1.Words(), page2.Words(), page3.Words()], spec);

        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "2001"]);
        result.Report.StoppedOnPage.ShouldBe(2);
        result.Report.Pages.ShouldBe([1, 2], "page 3 is never read after the stop line");
        result.Report.PerPage.Select(p => p.SkippedLines).ShouldBe([1, 1]);
        result.Report.RejectedRows.ShouldBe(0);
    }

    [Fact]
    public void Pages_without_rows_are_reported()
    {
        var page1 = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00");
        var page2 = new PageBuilder(2).Text(300, 500, "Diese Seite ist absichtlich leer").Words();
        var page3 = new PageBuilder(3).Header(60).Article(80, "3001", "Gamma", "3,00");
        var spec = PriceList.Spec(pages: [1, 2, 3]) with { PageMargins = new PageMargins(null, 900), Continuation = Continuation.None, GroupHeader = null };

        var result = Read([page1.Words(), page2, page3.Words()], spec);

        result.Report.PagesWithoutRows.ShouldBe([2]);
        result.Report.PerPage[1].HeaderFound.ShouldBeFalse();
        result.Report.PerPage[1].RejectedLines.ShouldHaveSingleItem().Reason.ShouldBe(RejectionReasons.NoAnchor);
        result.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void Page_margins_cut_off_running_headers_and_footers_on_pages_without_a_header()
    {
        var page1 = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00");
        var page2 = new PageBuilder(2)
            .Text(PriceList.Description, 30, "Nordwind Großhandel GmbH")
            .Article(80, "2001", "Beta", "2,00")
            .Text(PriceList.Description, 970, "Seite 2 von 2");
        var spec = PriceList.Spec(pages: [1, 2], bottom: 990) with { PageMargins = new PageMargins(60, 950) };

        var result = Read([page1.Words(), page2.Words()], spec);

        result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Alpha", "Beta"], "the letterhead and the footer are outside the margins, not continuation lines");
    }

    [Fact]
    public void Continuation_none_rejects_lines_without_a_row_anchor_and_a_continuation_with_a_second_price_is_rejected()
    {
        var page = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00")
            .Line(135, (PriceList.Description, "second line"));

        var none = Read([page.Words()], PriceList.Spec() with { GroupHeader = null, Continuation = Continuation.None });
        none.Rows.ShouldHaveSingleItem().Cell("description")!.Text.ShouldBe("Alpha");
        none.Report.PerPage[0].RejectedLines.ShouldHaveSingleItem().Reason.ShouldBe(RejectionReasons.NoAnchor);

        var conflict = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00")
            .Line(135, (PriceList.Description, "Nachlass"), (PriceList.Price, "0,50"))
            .Article(150, "1002", "Beta", "2,00");
        var result = Read([conflict.Words()], PriceList.Spec());
        result.Rows.ShouldHaveSingleItem().Cell("supplierSku")!.Text.ShouldBe("1002");
        var rejected = result.Report.PerPage[0].RejectedLines.ShouldHaveSingleItem();
        rejected.Reason.ShouldBe(RejectionReasons.ContinuationConflict);
        rejected.Field.ShouldBe("price");
        rejected.Text.ShouldContain("Nachlass");
    }

    [Fact]
    public void A_page_override_sets_the_dividers_of_that_page_only()
    {
        var page1 = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00");

        // Page 2 is printed far off and without a header: only the override reads it.
        var page2 = new PageBuilder(2).Article(80, "2001", "Beta", "2,00", shift: 30);
        var page3 = new PageBuilder(3).Article(80, "3001", "Gamma", "3,00");
        var shifted = PriceList.Dividers.Select(d => new ColumnBounds(d.X0 + 30, Math.Min(1000, d.X1 + 30))).ToList();
        var spec = PriceList.Spec(pages: [1, 2, 3]) with { PageOverrides = [new PageOverride(2, shifted)] };

        var result = Read([page1.Words(), page2.Words(), page3.Words()], spec);

        result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Alpha", "Beta", "Gamma"]);
        result.Report.PerPage.Select(p => p.ColumnsFrom).ShouldBe([ColumnsFrom.Mapping, ColumnsFrom.Override, ColumnsFrom.Mapping]);
    }

    [Fact]
    public void Repeat_header_none_reads_a_repeated_header_as_a_rejected_line()
    {
        var page1 = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00");
        var page2 = new PageBuilder(2).Header(60).Article(80, "2001", "Beta", "2,00");
        var spec = PriceList.Spec(pages: [1, 2]) with { RepeatHeader = RepeatHeader.None, Continuation = Continuation.None, GroupHeader = null };

        var result = Read([page1.Words(), page2.Words()], spec);

        result.Rows.Count.ShouldBe(2);
        result.Report.PerPage[1].HeaderFound.ShouldBeFalse();
        result.Report.PerPage[1].RejectedLines.ShouldHaveSingleItem().Text.ShouldStartWith("Art.-Nr.");
    }

    [Fact]
    public void A_fourteen_page_price_list_of_412_rows_is_read_with_one_mapping_and_nothing_rejected()
    {
        // The P2 demo of docs/11 §23: 412 rows over 14 pages, repeated headers, group headings, continuation lines,
        // tier prices, a page footer and a carry-over line on every page, one mapping drawn on page 1.
        var pages = new List<WordPage>();
        var sku = 100000;
        // Group headings are numbered through the list (a heading that printed the page number on every page at the
        // same height would be a running title).
        var group = 0;
        for (var p = 1; p <= 14; p++)
        {
            var page = new PageBuilder(p);
            double y;
            if (p == 1)
            {
                page.Text(PriceList.Sku, 30, "Nordwind Großhandel GmbH").Text(PriceList.Sku, 50, "Preisliste gültig ab 01.01.2027").Header(100);
                y = 120;
            }
            else
            {
                page.Text(PriceList.Description, 20, $"Preisliste 2027 Seite {p}").Header(50, shift: p % 3);
                page.Text(PriceList.Description + (p % 3), 70, "Übertrag");
                y = 85;
            }

            var rowsOnPage = p == 14 ? 412 - (13 * 30) : 30;
            for (var r = 0; r < rowsOnPage; r++)
            {
                if (r % 10 == 0)
                {
                    page.Text(PriceList.Sku + (p % 3), y, $"Gruppe {++group}");
                    y += 13;
                }

                sku++;
                var tiers = r % 2 == 0;
                page.Article(y, sku.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Artikel {sku}", $"{(r % 50) + 1},{r % 100:00}", tiers ? "1,00" : null, tiers ? "0,90" : null, shift: p == 1 ? 0 : p % 3);
                y += 13;
                if (r % 7 == 3)
                {
                    page.Text(PriceList.Description + (p == 1 ? 0 : p % 3), y, "zweite Zeile");
                    y += 13;
                }
            }

            page.Text(PriceList.Description, 960, $"Seite {p} von 14");
            pages.Add(page.Words());
        }

        var spec = PriceList.Spec(pages: [.. Enumerable.Range(1, 14)], bottom: 950) with
        {
            SkipRows = [new Regex("^Übertrag", RegexOptions.None, TimeSpan.FromMilliseconds(100))],
            PageMargins = new PageMargins(null, 950),
        };

        var result = Read(pages, spec);

        result.Report.Rows.ShouldBe(412);
        result.Report.RejectedRows.ShouldBe(0);
        result.Report.PagesWithoutRows.ShouldBeEmpty();
        result.Report.PerPage.Count.ShouldBe(14);
        result.Report.PerPage.All(p => p.HeaderFound).ShouldBeTrue();
        result.Report.PerPage.Take(13).All(p => p.Rows == 30).ShouldBeTrue();
        result.Report.PerPage[13].Rows.ShouldBe(22);
        result.Rows.Select(r => r.Cell("supplierSku")!.Text).Distinct().Count().ShouldBe(412);
        result.Rows.Count(r => r.Cell("description")!.Text.EndsWith("zweite Zeile", StringComparison.Ordinal)).ShouldBeGreaterThan(50);
        result.Rows.All(r => r.Cell("category") is not null).ShouldBeTrue();
        result.Rows.Count(r => r.Tiers.Count == 2).ShouldBe(result.Rows.Count(r => r.Tiers.Count > 0));
        result.Report.PerPage.Sum(p => p.SkippedLines).ShouldBe(13);
    }

    [Fact]
    public void A_mapping_without_a_column_for_the_row_anchor_or_its_tier_spec_is_refused()
    {
        var page = new PageBuilder(1).Header(100).Words();
        Should.Throw<ArgumentException>(() => Read([page], PriceList.Spec() with { RowAnchor = "gtin" }));
        Should.Throw<ArgumentException>(() => Read([page], PriceList.Spec() with { TierColumns = null }), "a tier column needs tierColumns");
        var untiered = PriceList.Spec() with { TierColumns = null };
        untiered = untiered with { Columns = [.. untiered.Columns.Select(c => c with { TierMinQuantity = null })] };
        Read([page], untiered).Rows.ShouldBeEmpty("without tierColumns a tier header is an ignored column");
        Should.Throw<ArgumentException>(() => Read([page], PriceList.Spec(page: 2)));
    }

    [Fact]
    public void The_proposer_finds_the_columns_from_gaps_and_names_them_from_the_header()
    {
        var page = new PageBuilder(1).Text(55, 40, "Preisliste").Header(100)
            .Article(120, "100234", "Rioja Crianza", "12,99", "11,50", "10,90")
            .Article(135, "100235", "Rioja Reserva DOCa", "18,99", "17,50", "16,90")
            .Text(PriceList.Description, 150, "lange zweite Zeile")
            .Article(165, "100236", "Cava Brut", "7,99", "7,50", "6,90")
            .Words();
        var fields = new List<ProposableField>
        {
            new("supplierSku", ["Art.-Nr.", "Article no."], FieldKinds.Identifier),
            new("description", ["Bezeichnung", "Description"]),
            // The template labels the numeric pack size "Inhalt" too; the column prints "6 x 0,75 l", so it is not proposed.
            new("packSize", ["Inhalt"], FieldKinds.Decimal),
            new("packDescription", ["Inhalt"], FieldKinds.String),
            new("unit", ["VE", "Bestelleinheit"], FieldKinds.Unit),
            new("price", ["Preis", "Price"], FieldKinds.Money),
        };

        var proposal = ColumnProposer.Propose(page, new BoundingBox(40, 95, 995, 200), null, fields, PriceList.TierPattern);

        proposal.HeaderRow.ShouldNotBeNull().Y0.ShouldBe(100);
        proposal.Columns.Select(c => c.Header).ShouldBe(["Art.-Nr.", "Bezeichnung", "Inhalt", "VE", "Preis", "ab 10", "ab 50"]);
        proposal.Columns.Select(c => c.Field).ShouldBe(["supplierSku", "description", "packDescription", "unit", "price", null, null]);
        proposal.Columns.Select(c => c.TierMinQuantity).ShouldBe([null, null, null, null, null, 10m, 50m]);

        // Every printed cell lies inside its proposed column, so the proposal reads the rows as they are.
        var spec = PriceList.Spec(top: 95, bottom: 200) with
        {
            Columns = [.. proposal.Columns.Select(c => new TableColumn(c.X0, c.X1, c.Field, c.Header, c.TierMinQuantity))],
            HeaderRow = proposal.HeaderRow,
        };
        var rows = Read([page], spec).Rows;
        rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Rioja Crianza", "Rioja Reserva DOCa lange zweite Zeile", "Cava Brut"]);
        rows.Select(r => r.Tiers.Count).ShouldBe([2, 2, 2]);
    }

    [Fact]
    public void Header_text_is_compared_without_case_punctuation_or_spaces()
    {
        ColumnTableEngine.NormalizeHeader("Art.-Nr.").ShouldBe("artnr");
        ColumnTableEngine.NormalizeHeader(" Ab 10 ").ShouldBe("ab10");
        ColumnTableEngine.TierQuantity(PriceList.TierPattern, "ab 50").ShouldBe(50m);
        ColumnTableEngine.TierQuantity(PriceList.TierPattern, "Preis").ShouldBeNull();
    }

    [Theory]
    [InlineData("ab 10", "de-DE", 10)]
    [InlineData("ab 1.000", "de-DE", 1000)]
    [InlineData("ab 1 000 Stück", "de-DE", 1000)]
    [InlineData("ab 2,5 kg", "de-DE", 2.5)]
    [InlineData("from 1,000", "en-US", 1000)]
    [InlineData("from 1.5", "en-US", 1.5)]
    [InlineData("ab 1.000", "en-US", 1)]
    public void A_tier_header_with_a_thousands_separator_is_read_as_a_whole_number_in_the_locale(string header, string locale, double expected)
    {
        // The full-number capture of the API's default pattern (TableCapture.DefaultTierPattern).
        var pattern = new Regex(@"^(?:ab|from|von)\s*(\d(?:[\d.,\s]*\d)?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        ValueLocale.TryParse(locale, out var parsed).ShouldBeTrue();
        ColumnTableEngine.TierQuantity(pattern, header, parsed).ShouldBe((decimal)expected);
    }

    [Fact]
    public void A_tier_column_the_reviewer_left_out_yields_no_tiers_whatever_its_header_says()
    {
        var page = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "12,99", "11,50", "10,90").Words();
        var spec = PriceList.Spec(bottom: 200);
        Read([page], spec).Rows.ShouldHaveSingleItem().Tiers.Select(t => t.MinQuantity).ShouldBe([10m, 50m]);

        // "(leave out)" in the portal: no field and no minimum quantity; the header "ab 10" / "ab 50" is still printed and named.
        var leftOut = spec with { Columns = [.. spec.Columns.Select(c => c.Field is null ? c with { TierMinQuantity = null, Header = c.X0 < 900 ? "ab 10" : "ab 50" } : c)] };
        var row = Read([page], leftOut).Rows.ShouldHaveSingleItem();
        row.Tiers.ShouldBeEmpty();
        row.Cell("price")!.Text.ShouldBe("12,99");

        // One left out, the other kept.
        var one = spec with { Columns = [.. spec.Columns.Select(c => c.TierMinQuantity == 50 ? c with { TierMinQuantity = null } : c)] };
        Read([page], one).Rows.ShouldHaveSingleItem().Tiers.Select(t => t.MinQuantity).ShouldBe([10m]);
    }

    [Fact]
    public void A_continuation_conflict_in_a_tier_column_names_the_column_by_its_number_and_a_left_out_column_has_none()
    {
        var page = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00", "0,90")
            .Line(135, (PriceList.Description, "Nachlass"), (PriceList.Tier10, "0,50"))
            .Article(150, "1002", "Beta", "2,00");

        // The tier column "ab 10" has no field: a second price in it on a continuation line is a conflict there.
        var rejected = Read([page.Words()], PriceList.Spec(bottom: 200)).Report.PerPage[0].RejectedLines.ShouldHaveSingleItem();
        rejected.Reason.ShouldBe(RejectionReasons.ContinuationConflict);
        rejected.Message.ShouldBe("A continuation line has a second value in column 6.");
        rejected.Field.ShouldBeNull();

        // Left out (no field, no tier), the column counts for nothing: the continuation line only adds its description.
        var leftOut = PriceList.Spec(bottom: 200) with { Columns = [.. PriceList.Spec().Columns.Select(c => c with { TierMinQuantity = null })] };
        var result = Read([page.Words()], leftOut);
        result.Report.RejectedRows.ShouldBe(0);
        result.Rows.Select(r => (r.Cell("supplierSku")!.Text, r.Cell("description")!.Text)).ShouldBe([("1001", "Alpha Nachlass"), ("1002", "Beta")]);

        var named = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00").Line(135, (PriceList.Description, "Nachlass"), (PriceList.Price, "0,50"));
        Read([named.Words()], PriceList.Spec(bottom: 200)).Report.PerPage[0].RejectedLines.ShouldHaveSingleItem()
            .Message.ShouldBe("A continuation line has a second value in the price column.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_wrapped_line_of_a_left_out_column_is_neither_a_heading_nor_a_continuation_nor_a_conflict(bool groupHeadings)
    {
        // The reviewer leaves the pack column out; row 1001 prints "Restposten" there and wraps "solange Vorrat" onto
        // the next line, in that column only.
        var page = new PageBuilder(1).Header(100)
            .Article(120, "1001", "Alpha", "1,00", content: "Restposten")
            .Line(135, (PriceList.Content, "solange Vorrat"))
            .Article(150, "1002", "Beta", "2,00");
        var spec = PriceList.Spec(bottom: 200) with
        {
            Columns = [.. PriceList.Spec().Columns.Select(c => c.Field == "packDescription" ? c with { Field = null } : c)],
            GroupHeader = groupHeadings ? new GroupHeaderSpec("category") : null,
        };

        var result = Read([page.Words()], spec);

        result.Report.RejectedRows.ShouldBe(0);
        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "1002"]);
        result.Rows.ShouldAllBe(r => r.Cell("category") == null, "the wrapped line is no heading");
        result.Rows.ShouldAllBe(r => r.Cell("packDescription") == null);
        result.Rows[0].Cell("description")!.Text.ShouldBe("Alpha");
        var report = result.Report.PerPage.ShouldHaveSingleItem();
        report.GroupHeadings.ShouldBe(0);
        report.ContinuationLines.ShouldBe(0);
        report.LeftOutLines.ShouldBe(1);
    }

    [Fact]
    public void A_footer_printed_at_the_same_place_on_every_page_is_left_out_by_itself_with_a_box_around_the_header_only()
    {
        // A box drawn only around the header row reads to the foot of the page, footers included.
        PageBuilder Page(int n, double footerX, double headerY, string sku) =>
            new PageBuilder(n).Header(headerY).Article(headerY + 20, sku, "Alpha", "1,00").Article(headerY + 35, $"{sku}9", "Beta", "2,00").Text(footerX, 960, $"Seite {n} von 2");

        foreach (var footerX in new[] { PriceList.Sku, 450d })
        {
            foreach (var headings in new[] { true, false })
            {
                var pages = new[] { Page(1, footerX, 100, "1001").Words(), Page(2, footerX, 60, "2001").Words() };
                var spec = PriceList.Spec(pages: [1, 2], top: 95, bottom: 112) with { GroupHeader = headings ? new GroupHeaderSpec("category") : null };

                var result = Read(pages, spec);

                result.Report.RejectedRows.ShouldBe(0);
                result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "10019", "2001", "20019"], $"footer at x={footerX}, headings {headings}");
                result.Rows.ShouldAllBe(r => r.Cell("category") == null);
                result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(["Alpha", "Beta", "Alpha", "Beta"]);
                result.Rows.Select(r => r.Cell("packDescription")!.Text).ShouldAllBe(t => t == "6 x 0,75 l");
                result.Report.PerPage.Select(p => p.RepeatedLines).ShouldBe([1, 1]);
                result.Report.PerPage.Select(p => p.RepeatedTexts!.Single()).ShouldBe(["Seite 1 von 2", "Seite 2 von 2"], "the page report names what it left out");
            }
        }

        // Off, the footer is read like any other line: in the article-number column it is a lone anchor value, rejected.
        var keep = Read([Page(1, PriceList.Sku, 100, "1001").Words(), Page(2, PriceList.Sku, 60, "2001").Words()], PriceList.Spec(pages: [1, 2], top: 95, bottom: 112) with { GroupHeader = null, SkipRepeatedLines = false });
        keep.Rows.Count.ShouldBe(4);
        keep.Report.PerPage.Select(p => p.RejectedLines.ShouldHaveSingleItem().Reason).ShouldBe([RejectionReasons.AnchorOnly, RejectionReasons.AnchorOnly]);
    }

    [Fact]
    public void A_value_only_in_the_anchor_column_is_rejected_not_taken_for_an_article()
    {
        // One page: nothing repeats, so the footer in the article-number column reaches the rows.
        var page = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00").Text(PriceList.Sku, 960, "Seite 1 von 1").Words();

        var result = Read([page], PriceList.Spec(top: 95, bottom: 112) with { GroupHeader = null });

        result.Rows.ShouldHaveSingleItem().Cell("supplierSku")!.Text.ShouldBe("1001");
        var rejected = result.Report.PerPage[0].RejectedLines.ShouldHaveSingleItem();
        rejected.Reason.ShouldBe(RejectionReasons.AnchorOnly);
        rejected.Text.ShouldBe("Seite 1 von 1");
        rejected.Field.ShouldBe("supplierSku");

        // A table that maps only the anchor column has nothing else to read: its rows are anchor values.
        var only = PriceList.Spec(top: 95, bottom: 112) with { Columns = [.. PriceList.Spec().Columns.Select(c => c.Field == "supplierSku" ? c : c with { Field = null, TierMinQuantity = null })], GroupHeader = null };
        Read([page], only).Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void Lines_alike_between_rows_are_never_running_lines()
    {
        // Rows printed at a fixed pitch: the continuation line "zweite Zeile" sits at the same height on every page.
        var pages = Enumerable.Range(1, 4).Select(n => new PageBuilder(n).Header(100)
            .Article(120, $"{n}001", "Alpha", "1,00").Text(PriceList.Description, 135, "zweite Zeile")
            .Article(150, $"{n}002", "Beta", "2,00").Words()).ToList();

        var result = Read(pages, PriceList.Spec(pages: [1, 2, 3, 4], bottom: 940));

        result.Rows.Count.ShouldBe(8);
        result.Rows.Where(r => r.Cell("supplierSku")!.Text.EndsWith("001", StringComparison.Ordinal)).ShouldAllBe(r => r.Cell("description")!.Text == "Alpha zweite Zeile");
        result.Report.PerPage.ShouldAllBe(p => p.RepeatedLines == 0);
    }

    [Fact]
    public void Running_lines_match_a_number_token_by_token_the_same_on_both_pages_or_each_page_s_own_number()
    {
        ColumnTableEngine.RepeatTokens("Stand  01.11.2026").ShouldBe(["stand", " ", "01", ".", "11", ".", "2026"]);
        ColumnTableEngine.SameRunningLine("Seite 2 von 14", 2, "Seite  13 von 14", 13).ShouldBeTrue();
        ColumnTableEngine.SameRunningLine("Seite 14 von 14", 14, "Seite 1 von 14", 1).ShouldBeTrue();
        ColumnTableEngine.SameRunningLine("Warengruppe 12", 5, "Warengruppe 13", 6).ShouldBeFalse();
        ColumnTableEngine.SameRunningLine("Stand 01.11.2026", 3, "Stand 01.11.2026", 4).ShouldBeTrue();

        // A date whose day is one page's number is the same date on every page; it is never rewritten for that page only.
        ColumnTableEngine.SameRunningLine("Preise gültig ab 01.04.2026 Seite 1 von 4", 1, "Preise gültig ab 01.04.2026 Seite 2 von 4", 2).ShouldBeTrue();
        ColumnTableEngine.SameRunningLine("Preise gültig ab 01.04.2026 Seite 4 von 4", 4, "Preise gültig ab 01.04.2026 Seite 1 von 4", 1).ShouldBeTrue();
        ColumnTableEngine.SameRunningLine("Preise gültig ab 01.04.2026", 1, "Preise gültig ab 02.04.2026", 2).ShouldBeTrue("each is its page's own number");
        ColumnTableEngine.SameRunningLine("Preise gültig ab 01.04.2026", 1, "Preise gültig ab 03.04.2026", 2).ShouldBeFalse();
        ColumnTableEngine.SameRunningLine("Preisliste", 1, "Preisliste 2", 2).ShouldBeFalse();
    }

    [Fact]
    public void A_dated_footer_whose_day_is_a_page_number_is_a_running_line_on_that_page_too()
    {
        // The footer's "01" is page 1's number: compared token by token it is still the footer of pages 2 to 4.
        var pages = Enumerable.Range(1, 4).Select(n => new PageBuilder(n).Header(100)
            .Article(120, $"{n}001", $"Wein {n}a", "1,00").Article(135, $"{n}002", $"Wein {n}b", "2,00")
            .Text(PriceList.Description, 920, $"Preise gültig ab 01.04.2026 Seite {n} von 4").Words()).ToList();

        var result = Read(pages, PriceList.Spec(pages: [1, 2, 3, 4], bottom: 990));

        result.Report.RejectedRows.ShouldBe(0);
        result.Rows.Count.ShouldBe(8);
        result.Rows.Select(r => r.Cell("description")!.Text).ShouldBe(Enumerable.Range(1, 4).SelectMany(n => new[] { $"Wein {n}a", $"Wein {n}b" }));
        result.Report.PerPage.Select(p => p.RepeatedLines).ShouldBe([1, 1, 1, 1]);
        result.Report.PerPage.Select(p => p.ContinuationLines).ShouldBe([0, 0, 0, 0]);
        result.Report.PerPage[0].RepeatedTexts.ShouldBe(["Preise gültig ab 01.04.2026 Seite 1 von 4"]);
    }

    [Fact]
    public void A_heading_that_opens_every_page_at_the_same_height_is_read_as_a_heading_not_left_out_as_a_running_line()
    {
        // Each page: "Rotweine trocken" centred at its top (across a divider), two articles, "Weißweine trocken", one more.
        PageBuilder Page(int n)
        {
            var page = new PageBuilder(n);
            var top = n == 1 ? 120 : 40;
            if (n == 1)
            {
                page.Header(100);
            }

            return page.Text(460, top, "Rotweine trocken")
                .Article(top + 15, $"{n}001", "Rot A", "1,00").Article(top + 30, $"{n}002", "Rot B", "2,00")
                .Text(460, top + 45, "Weißweine trocken")
                .Article(top + 60, $"{n}003", "Weiß A", "3,00");
        }

        var pages = Enumerable.Range(1, 4).Select(n => Page(n).Words()).ToList();
        var spec = PriceList.Spec(pages: [1, 2, 3, 4], bottom: 990);

        var result = Read(pages, spec);

        result.Rows.Count.ShouldBe(12);
        result.Rows.ShouldAllBe(r => r.Cell("category")!.Text == (r.Cell("description")!.Text.StartsWith("Rot", StringComparison.Ordinal) ? "Rotweine trocken" : "Weißweine trocken"));
        result.Report.PerPage.ShouldAllBe(p => p.Headings!.SequenceEqual(new[] { "Rotweine trocken", "Weißweine trocken" }));
        result.Report.PerPage.ShouldAllBe(p => p.RepeatedLines == 0);
        result.Rows.Single(r => r.Cell("supplierSku")!.Text == "2001").Cell("category")!.Box.Y0.ShouldBe(40, "the heading of page 2 itself");

        // The same with repeated lines kept: nothing changes.
        Read(pages, spec with { SkipRepeatedLines = false }).Rows.Select(r => r.Cell("category")!.Text).ShouldBe(result.Rows.Select(r => r.Cell("category")!.Text));
    }

    [Fact]
    public void A_repeated_heading_of_the_current_category_is_left_out_and_listed_by_its_text()
    {
        static WordPage Page(int n, string heading)
        {
            var page = new PageBuilder(n);
            var y = n == 1 ? 135 : 55;
            if (n == 1)
            {
                page.Header(100).Text(460, 120, "Rotweine trocken");
            }
            else
            {
                page.Text(460, 40, heading);
            }

            return page.Article(y, $"{n}001", "Rot A", "1,00").Article(y + 15, $"{n}002", "Rot B", "2,00").Words();
        }

        // Another text on pages 2 and 3 than page 1's heading: read as a heading on page 2; on page 3 it is the current
        // category already, so it is left out as the running line it is, and the rows keep it.
        var other = Read([.. Enumerable.Range(1, 3).Select(n => Page(n, "Rotweine trocken, Fortsetzung"))], PriceList.Spec(pages: [1, 2, 3], bottom: 990));
        other.Rows.Count.ShouldBe(6);
        other.Report.PerPage.Select(p => (string.Join(" | ", p.Headings!), p.RepeatedLines)).ShouldBe([("Rotweine trocken", 0), ("Rotweine trocken, Fortsetzung", 0), (string.Empty, 1)]);
        other.Rows.Select(r => r.Cell("category")!.Text).ShouldBe(["Rotweine trocken", "Rotweine trocken", .. Enumerable.Repeat("Rotweine trocken, Fortsetzung", 4)]);

        // The category the rows already have: leaving it out changes nothing, and the page names it.
        var same = Read([.. Enumerable.Range(1, 3).Select(n => Page(n, "Rotweine trocken"))], PriceList.Spec(pages: [1, 2, 3], bottom: 990));
        same.Rows.Count.ShouldBe(6);
        same.Rows.ShouldAllBe(r => r.Cell("category")!.Text == "Rotweine trocken");
        same.Report.PerPage.Select(p => p.RepeatedLines).ShouldBe([0, 1, 1]);
        same.Report.PerPage.Skip(1).ShouldAllBe(p => p.RepeatedTexts!.Single() == "Rotweine trocken" && p.Headings!.Count == 0);
    }

    [Fact]
    public void A_pattern_that_runs_out_of_time_stops_the_run_and_so_does_the_cancellation_token()
    {
        var page = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00").Text(PriceList.Description, 135, new string('a', 40) + "!").Words();
        var slow = PriceList.Spec(bottom: 200) with { SkipRows = [new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1))] };

        Should.Throw<TablePatternTimeoutException>(() => Read([page], slow)).Pattern.ShouldBe("^(a+)+$");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Should.Throw<OperationCanceledException>(() => ColumnTableEngine.Extract([page], PriceList.Spec(bottom: 200), cancelled.Token));
    }

    [Fact]
    public void A_box_around_the_header_row_only_reads_each_page_down_to_its_foot_or_the_bottom_margin()
    {
        var page1 = new PageBuilder(1).Header(100).Article(120, "1001", "Alpha", "1,00").Article(700, "1002", "Beta", "2,00");
        var page2 = new PageBuilder(2).Header(60).Article(80, "2001", "Gamma", "3,00").Article(960, "2002", "Delta", "4,00");
        var headerOnly = PriceList.Spec(pages: [1, 2], top: 95, bottom: 115) with { GroupHeader = null };

        var result = Read([page1.Words(), page2.Words()], headerOnly);

        result.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "1002", "2001", "2002"], "nothing is printed below the header inside the box, so the page is read to its foot");
        result.Report.PerPage.Select(p => p.Bottom).ShouldBe([1000d, 1000d]);
        result.Report.PerPage[0].Top.ShouldBe(111);

        var margin = Read([page1.Words(), page2.Words()], headerOnly with { PageMargins = new PageMargins(null, 900) });
        margin.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "1002", "2001"], "the bottom margin ends every page");

        // A box with rows in it still ends at its bottom on the mapping page, and on the other pages without a margin.
        var boxed = Read([page1.Words(), page2.Words()], PriceList.Spec(pages: [1, 2], bottom: 600) with { GroupHeader = null });
        boxed.Rows.Select(r => r.Cell("supplierSku")!.Text).ShouldBe(["1001", "2001"]);
        boxed.Report.PerPage.Select(p => p.Bottom).ShouldBe([600d, 600d]);
    }

    [Fact]
    public void Skip_lines_and_a_top_margin_keep_a_running_title_and_a_footer_out_of_the_rows_when_the_header_is_printed_on_page_1_only()
    {
        // docs/11 §8.6 "lists that print it only on page 1": page 2 has a running title and no header, and a footer
        // printed in the description column inside the box.
        var page1 = new PageBuilder(1).Header(100).Text(PriceList.Sku, 115, "Weißweine").Article(130, "1001", "Alpha", "1,00").Text(PriceList.Description, 900, "Seite 1 von 2");
        var page2 = new PageBuilder(2).Text(PriceList.Sku, 30, "Nordwind Preisliste 2027").Article(80, "2001", "Beta", "2,00").Text(PriceList.Description, 900, "Seite 2 von 2");
        var spec = PriceList.Spec(pages: [1, 2], bottom: 940);

        var plain = Read([page1.Words(), page2.Words()], spec);
        // The footer is printed at the same place on both pages: left out by itself. The running title is printed on
        // page 2 only, so nothing tells it from a heading but the reviewer: it reads as one, and the page report lists it.
        plain.Rows.Single(r => r.Cell("supplierSku")!.Text == "1001").Cell("description")!.Text.ShouldBe("Alpha");
        plain.Report.PerPage.Select(p => p.RepeatedLines).ShouldBe([1, 1]);
        plain.Rows.Single(r => r.Cell("supplierSku")!.Text == "2001").Cell("category")!.Text.ShouldBe("Nordwind Preisliste 2027", "without the reviewer's help a title on one page reads as a heading");
        plain.Report.PerPage[1].Headings.ShouldBe(["Nordwind Preisliste 2027"]);
        plain.Report.PerPage[0].Headings.ShouldBe(["Weißweine"]);

        // What the portal sends for "Leave out lines that start with" "Seite 1 von 2" and a top margin of 5 %.
        var helped = spec with { SkipRows = [new Regex(@"^Seite\s+\d+\s+von\s+\d+", RegexOptions.NonBacktracking)], PageMargins = new PageMargins(50, null) };
        var rows = Read([page1.Words(), page2.Words()], helped).Rows;
        rows.Select(r => (r.Cell("supplierSku")!.Text, r.Cell("description")!.Text, r.Cell("category")!.Text)).ShouldBe([("1001", "Alpha", "Weißweine"), ("2001", "Beta", "Weißweine")]);
    }

    [Fact]
    public void Lines_group_words_by_their_vertical_band_and_read_left_to_right()
    {
        var words = new PageBuilder(1).Text(300, 100, "rechts").Text(50, 102, "links").Text(50, 130, "unten").Words().Words;
        var lines = PageGeometry.Lines(words);
        lines.Select(l => l.Text).ShouldBe(["links rechts", "unten"]);
        PageGeometry.ReadingText(words).ShouldBe("links rechts unten");
        PageGeometry.Hit(words, new BoundingBox(0, 90, 200, 140)).Select(w => w.Text).ShouldBe(["links", "unten"]);
    }
}
