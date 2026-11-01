using System.Globalization;

namespace DocProc.Domain.Common;

/// <summary>A decimal amount with an explicit ISO 4217 currency code.</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public Money Abs() => this with { Amount = Math.Abs(Amount) };

    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount, RequireSameCurrency(a, b));

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount, RequireSameCurrency(a, b));

    public static Money operator -(Money a) => a with { Amount = -a.Amount };

    public static Money operator *(Money a, decimal factor) => a with { Amount = a.Amount * factor };

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Negate() => -this;

    public Money Multiply(decimal factor) => this * factor;

    /// <summary>True when the amounts differ by at most <paramref name="tolerance"/> and the currencies agree.</summary>
    public bool IsApproximately(Money other, decimal tolerance) =>
        string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase)
        && Math.Abs(Amount - other.Amount) <= tolerance;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");

    private static string RequireSameCurrency(Money a, Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot combine {a.Currency} with {b.Currency}.");
        }

        return a.Currency;
    }
}

/// <summary>Minimal ISO 4217 checks. Full validation against a currency table belongs in configuration, not the domain.</summary>
public static class CurrencyCode
{
    public static bool IsWellFormed(string? code) =>
        code is { Length: 3 } && code.All(c => c is >= 'A' and <= 'Z');

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
