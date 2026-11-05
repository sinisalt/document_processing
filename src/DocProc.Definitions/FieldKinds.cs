using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace DocProc.Definitions;

/// <summary>
/// The field kinds of the definition format (docs/11 §5.2). Each carries the typed semantics of
/// <c>src/DocProc.Domain/Common</c>; the composite kinds are shipped shapes whose members are fixed here and mirror
/// the domain records (<see cref="CompositeMembers"/>, held to the records by the template parity tests).
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The constants are named after the kind names of the format.")]
public static class FieldKinds
{
    public const string String = "string";
    public const string Text = "text";
    public const string Identifier = "identifier";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Percent = "percent";
    public const string Money = "money";
    public const string Currency = "currency";
    public const string Date = "date";
    public const string Period = "period";
    public const string Boolean = "boolean";
    public const string Enum = "enum";
    public const string Unit = "unit";
    public const string Party = "party";
    public const string Address = "address";
    public const string TaxRow = "taxRow";
    public const string PaymentTerms = "paymentTerms";
    public const string ChargeOrAllowance = "chargeOrAllowance";
    public const string TemperatureReading = "temperatureReading";
    public const string Object = "object";
    public const string Array = "array";
    public const string Bag = "bag";

    /// <summary>
    /// The members of each composite value, as the canonical JSON names them (camelCase). <c>identifier</c> is the
    /// <c>DocumentIdentifier</c> wire shape, <c>period</c> is <c>DatePeriod</c>, <c>taxRow</c> a VAT summary row,
    /// <c>paymentTerms</c>, <c>chargeOrAllowance</c> and <c>temperatureReading</c> the records of the same names.
    /// </summary>
    public static readonly FrozenDictionary<string, IReadOnlyList<string>> CompositeMembers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Identifier] = ["verbatim", "normalized"],
        [Money] = ["amount", "currency"],
        [Period] = ["from", "to"],
        [Party] = ["name", "vatId", "taxId", "registrationNumber", "address", "email", "phone", "iban", "bic"],
        [Address] = ["line1", "line2", "postalCode", "city", "region", "countryCode"],
        [TaxRow] = ["rate", "taxableAmount", "taxAmount", "category"],
        [PaymentTerms] = ["dueDate", "discountPercent", "discountUntil", "paymentMeans", "paymentReference", "iban", "bic", "rawText"],
        [ChargeOrAllowance] = ["description", "amount", "taxRate", "isCharge"],
        [TemperatureReading] = ["celsius", "location", "rawText"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The kind of each member of a composite value, in <see cref="CompositeMembers"/> order: what the compiler
    /// describes the member as in the JSON schema, the LLM schema and the form (docs/11 §5.5). A composite is one
    /// <c>FieldValue</c> envelope; its members are parts of that value, not fields of their own.
    /// </summary>
    public static readonly FrozenDictionary<string, IReadOnlyList<(string Member, string Kind)>> CompositeMemberKinds = new Dictionary<string, IReadOnlyList<(string Member, string Kind)>>(StringComparer.Ordinal)
    {
        [Identifier] = [("verbatim", String), ("normalized", String)],
        [Money] = [("amount", Decimal), ("currency", Currency)],
        [Period] = [("from", Date), ("to", Date)],
        [Party] = [("name", String), ("vatId", String), ("taxId", String), ("registrationNumber", String), ("address", Address), ("email", String), ("phone", String), ("iban", String), ("bic", String)],
        [Address] = [("line1", String), ("line2", String), ("postalCode", String), ("city", String), ("region", String), ("countryCode", String)],
        [TaxRow] = [("rate", Percent), ("taxableAmount", Money), ("taxAmount", Money), ("category", String)],
        [PaymentTerms] = [("dueDate", Date), ("discountPercent", Percent), ("discountUntil", Date), ("paymentMeans", String), ("paymentReference", String), ("iban", String), ("bic", String), ("rawText", Text)],
        [ChargeOrAllowance] = [("description", String), ("amount", Money), ("taxRate", Percent), ("isCharge", Boolean)],
        [TemperatureReading] = [("celsius", Decimal), ("location", String), ("rawText", Text)],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The shipped composites whose members are inlined at compile time (not <c>identifier</c>, <c>money</c> or <c>period</c>, which are value shapes).</summary>
    public static bool IsComposite(string kind) => kind is Party or Address or TaxRow or PaymentTerms or ChargeOrAllowance or TemperatureReading;

    /// <summary>Every kind the format knows.</summary>
    public static readonly FrozenSet<string> All = new[]
    {
        String, Text, Identifier, Integer, Decimal, Percent, Money, Currency, Date, Period, Boolean, Enum, Unit,
        Party, Address, TaxRow, PaymentTerms, ChargeOrAllowance, TemperatureReading, Object, Array, Bag,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Kinds whose value is a single scalar (string, number, boolean or code).</summary>
    public static bool IsScalar(string kind) => kind is String or Text or Integer or Decimal or Percent or Currency or Date or Boolean or Enum or Unit;
}
