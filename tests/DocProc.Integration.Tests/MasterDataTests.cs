using System.Text.Json.Nodes;
using DocProc.Infrastructure.MasterData;

namespace DocProc.Integration.Tests;

/// <summary>
/// The pieces of the master-data import and sync (docs/11 §6.8, §14) that need no database: the cron schedule of a
/// source (UTC or <c>CRON_TZ=</c>, ranges, lists, steps, either day field), the CSV reader of an import, the records
/// of a JSON import and the conflict policy per attribute.
/// </summary>
public sealed class MasterDataTests
{
    [Theory]
    [InlineData("15 3 * * *", "2026-11-05T03:00:00Z", "2026-11-05T03:15:00Z")]
    [InlineData("15 3 * * *", "2026-11-05T03:15:00Z", "2026-11-06T03:15:00Z")]
    [InlineData("CRON_TZ=Europe/Berlin 15 3 * * *", "2026-11-05T03:00:00Z", "2026-11-06T02:15:00Z")]
    [InlineData("CRON_TZ=Europe/Berlin 0 6 * * 1-5", "2026-11-06T06:00:00Z", "2026-11-09T05:00:00Z")]
    [InlineData("*/30 8-9 * * *", "2026-11-05T08:40:00Z", "2026-11-05T09:00:00Z")]
    [InlineData("0 0 1 * 0", "2026-11-03T12:00:00Z", "2026-11-08T00:00:00Z")]
    [InlineData("0 12 29 2 *", "2026-03-01T00:00:00Z", null)]
    public void A_schedule_names_its_next_run(string cron, string after, string? next)
    {
        var schedule = CronSchedule.Parse(cron);

        schedule.Next(DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(next is null ? null : DateTimeOffset.Parse(next, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("CRON_TZ=Mars/Olympus 0 0 * * *")]
    [InlineData("CRON_TZ=Europe/Berlin")]
    [InlineData("a b c d e")]
    public void A_schedule_that_does_not_read_says_why(string cron)
    {
        CronSchedule.TryParse(cron, out var schedule, out var error).ShouldBeFalse();
        schedule.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_shortest_gap_tells_how_often_a_schedule_runs()
    {
        var from = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        CronSchedule.Parse("* * * * *").ShortestGap(from).ShouldBe(TimeSpan.FromMinutes(1));
        CronSchedule.Parse("*/15 * * * *").ShortestGap(from).ShouldBe(TimeSpan.FromMinutes(15));
        CronSchedule.Parse("0 3 * * *").ShortestGap(from).ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void The_csv_reader_finds_the_delimiter_and_keeps_quoted_cells_whole()
    {
        var (header, rows) = CsvTable.Parse("Kreditor;Name;IBAN\r\nV-1;\"Müller; Söhne\";DE44 2005 0550 1234 5678 90\r\n\r\nV-2;\"Say \"\"hi\"\"\";\n");

        header.ShouldBe(["Kreditor", "Name", "IBAN"]);
        rows.Count.ShouldBe(2, "an empty line is no record");
        rows[0][1].ShouldBe("Müller; Söhne");
        rows[1][1].ShouldBe("Say \"hi\"");
        Should.Throw<CsvFormatException>(() => CsvTable.Parse("a,b\n\"open"));
    }

    [Fact]
    public void Json_records_are_read_in_our_shape_and_cleaned()
    {
        var records = SupplierImporter.FromJson(JsonNode.Parse("""{ "records": [ { "name": " Nordwind ", "vatId": "de 312 456 789", "iban": "DE44 2005 0550 1234 5678 90", "attributes": { "erpVendorId": "V-1" } } ] }"""), out var problem);

        problem.ShouldBeNull();
        var record = SupplierImporter.Clean(records.ShouldHaveSingleItem(), out var invalid);
        invalid.ShouldBeNull();
        record.Name.ShouldBe("Nordwind");
        record.VatId.ShouldBe("DE312456789");
        record.Ibans.ShouldBe(["DE44200505501234567890"]);
        record.Attributes!["erpVendorId"]!.GetValue<string>().ShouldBe("V-1");

        SupplierImporter.Clean(new SupplierRecord("x", null, null, ["NOT AN IBAN"], null, null), out var bad);
        bad.ShouldNotBeNull().ShouldContain("IBAN");
        SupplierImporter.FromJson(JsonNode.Parse("""{ "name": "x" }"""), out var notList);
        notList.ShouldNotBeNull();
    }

    [Fact]
    public void The_conflict_policy_of_an_attribute_is_its_own_then_its_group_then_the_default()
    {
        var policies = new Dictionary<string, string> { ["name"] = ConflictPolicies.Flag, ["attributes"] = ConflictPolicies.LocalWins, ["*"] = ConflictPolicies.ExternalWins };

        SupplierImporter.PolicyOf(policies, "name").ShouldBe(ConflictPolicies.Flag);
        SupplierImporter.PolicyOf(policies, "attributes.erpVendorId").ShouldBe(ConflictPolicies.LocalWins);
        SupplierImporter.PolicyOf(policies, "vatId").ShouldBe(ConflictPolicies.ExternalWins);
        SupplierImporter.PolicyOf(null, "vatId").ShouldBe(ConflictPolicies.ExternalWins, "the default: the ERP's value wins");
    }
}
