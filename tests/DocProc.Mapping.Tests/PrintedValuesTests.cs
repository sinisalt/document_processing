using System.Text.Json.Nodes;
using DocProc.Definitions;
using DocProc.Mapping.Values;

namespace DocProc.Mapping.Tests;

/// <summary>Printed values read by kind and locale (docs/11 §12.3 "28.10.2026 → 2026-10-28"), culture-free.</summary>
public sealed class PrintedValuesTests
{
    [Theory]
    [InlineData("1.234,56", "de-DE", "1234.56")]
    [InlineData("1,234.56", "de-DE", "1234.56")]
    [InlineData("1.234", "de-DE", "1234")]
    [InlineData("1,250", "de-DE", "1.250")]
    [InlineData("1,250", "en-US", "1250")]
    [InlineData("12,99", "en-US", "12.99")]
    [InlineData("12.99", "de-DE", "12.99")]
    [InlineData("1.234.567", "de-DE", "1234567")]
    [InlineData("1'234.50", "de-CH", "1234.50")]
    [InlineData("1 234,50", "fr-FR", "1234.50")]
    [InlineData("-3,50", "de-DE", "-3.50")]
    [InlineData("3,50-", "de-DE", "-3.50")]
    [InlineData("(3.50)", "en-US", "-3.50")]
    public void Numbers_read_by_their_separators_and_the_locale_where_they_do_not_decide(string text, string locale, string expected)
    {
        ValueLocale.TryParse(locale, out var l).ShouldBeTrue();
        PrintedValues.TryDecimal(text, l, out var value).ShouldBeTrue();
        value.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("12,9x")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1,2,3,4a")]
    public void Text_that_is_no_number_does_not_read(string text) =>
        PrintedValues.TryDecimal(text, ValueLocale.German, out _).ShouldBeFalse();

    [Theory]
    [InlineData("28.10.2026", "de-DE", "2026-10-28")]
    [InlineData("28.10.26", "de-DE", "2026-10-28")]
    [InlineData("2026-10-28", "en-US", "2026-10-28")]
    [InlineData("10/28/2026", "en-US", "2026-10-28")]
    [InlineData("28/10/2026", "en-GB", "2026-10-28")]
    [InlineData("03.11.2026", "en-US", "2026-11-03")]
    [InlineData("28. Oktober 2026", "de-DE", "2026-10-28")]
    [InlineData("28 October 2026", "en-GB", "2026-10-28")]
    [InlineData("October 28, 2026", "en-US", "2026-10-28")]
    [InlineData("1. März 2027", "de-DE", "2027-03-01")]
    public void Dates_read_in_the_locales_order(string text, string locale, string expected)
    {
        ValueLocale.TryParse(locale, out var l).ShouldBeTrue();
        PrintedValues.Parse(FieldKinds.Date, text, l).Value!.GetValue<string>().ShouldBe(expected);
    }

    [Theory]
    [InlineData("31.02.2026")]
    [InlineData("13/13/2026")]
    [InlineData("gestern")]
    public void Impossible_dates_do_not_read(string text) =>
        PrintedValues.Parse(FieldKinds.Date, text, ValueLocale.German).Ok.ShouldBeFalse();

    [Fact]
    public void Amounts_take_a_printed_currency_and_are_bare_numbers_without_one()
    {
        var withSymbol = PrintedValues.Parse(FieldKinds.Money, "12,99 €", ValueLocale.German).Value.ShouldBeOfType<JsonObject>();
        withSymbol["amount"]!.GetValue<decimal>().ShouldBe(12.99m);
        withSymbol["currency"]!.GetValue<string>().ShouldBe("EUR");
        var withCode = PrintedValues.Parse(FieldKinds.Money, "EUR 1.234,50", ValueLocale.German).Value.ShouldBeOfType<JsonObject>();
        withCode["amount"]!.GetValue<decimal>().ShouldBe(1234.50m);
        PrintedValues.Parse(FieldKinds.Money, "12,99", ValueLocale.German).Value!.GetValue<decimal>().ShouldBe(12.99m);
        PrintedValues.Parse(FieldKinds.Money, "zwölf", ValueLocale.German).Ok.ShouldBeFalse();
    }

    [Fact]
    public void Other_kinds_read_as_their_shape()
    {
        PrintedValues.Parse(FieldKinds.Integer, "1.000", ValueLocale.German).Value!.GetValue<long>().ShouldBe(1000);
        PrintedValues.Parse(FieldKinds.Integer, "2,5", ValueLocale.German).Ok.ShouldBeFalse();
        PrintedValues.Parse(FieldKinds.Percent, "19 %", ValueLocale.German).Value!.GetValue<decimal>().ShouldBe(19m);
        PrintedValues.Parse(FieldKinds.Currency, "eur", ValueLocale.German).Value!.GetValue<string>().ShouldBe("EUR");
        PrintedValues.Parse(FieldKinds.Currency, "€", ValueLocale.German).Value!.GetValue<string>().ShouldBe("EUR");
        PrintedValues.Parse(FieldKinds.Boolean, "Ja", ValueLocale.German).Value!.GetValue<bool>().ShouldBeTrue();
        PrintedValues.Parse(FieldKinds.Enum, "full", ValueLocale.German, ["Full", "Changes"]).Value!.GetValue<string>().ShouldBe("Full");
        PrintedValues.Parse(FieldKinds.Enum, "Teil", ValueLocale.German, ["Full", "Changes"]).Ok.ShouldBeFalse();
        PrintedValues.Parse(FieldKinds.Identifier, "  DE 811  111 ", ValueLocale.German).Value!.GetValue<string>().ShouldBe("DE 811 111");
        PrintedValues.Parse(FieldKinds.Text, "   ", ValueLocale.German).Value.ShouldBeNull();
        PrintedValues.Parse(FieldKinds.Party, "Nordwind", ValueLocale.German).Ok.ShouldBeFalse();
    }

    [Fact]
    public void Candidates_offer_every_reading_of_a_selection()
    {
        PrintedValues.Candidates("28.10.2026", ValueLocale.German).Select(c => c.Kind).ShouldBe([FieldKinds.Date, FieldKinds.Identifier, FieldKinds.Text]);
        var amount = PrintedValues.Candidates("1.234,50 €", ValueLocale.German);
        amount.Select(c => c.Kind).ShouldBe([FieldKinds.Money, FieldKinds.Identifier, FieldKinds.Text]);
        PrintedValues.Candidates("1.234,50", ValueLocale.German).Select(c => c.Kind).ShouldBe([FieldKinds.Decimal, FieldKinds.Identifier, FieldKinds.Text]);
        PrintedValues.Candidates("412", ValueLocale.German).Select(c => c.Kind).ShouldBe([FieldKinds.Decimal, FieldKinds.Integer, FieldKinds.Identifier, FieldKinds.Text]);
        PrintedValues.Candidates(" ", ValueLocale.German).ShouldBeEmpty();
    }

    [Fact]
    public void Locales_come_from_a_name_or_the_tenants_country()
    {
        ValueLocale.TryParse("de-AT", out var austria).ShouldBeTrue();
        austria.DecimalSeparator.ShouldBe(',');
        ValueLocale.TryParse("en", out var english).ShouldBeTrue();
        english.Name.ShouldBe("en-GB");
        ValueLocale.TryParse("en-US", out var us).ShouldBeTrue();
        us.DateOrder.ShouldBe(DateOrder.MonthDayYear);
        ValueLocale.TryParse("not a locale", out _).ShouldBeFalse();
        ValueLocale.ForCountry("HR").ShouldBe(new ValueLocale("hr-HR", ',', DateOrder.DayMonthYear));
        ValueLocale.ForCountry("US").DecimalSeparator.ShouldBe('.');
        ValueLocale.ForCountry(null).ShouldBe(ValueLocale.German);
    }
}
