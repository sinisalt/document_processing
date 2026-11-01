namespace DocProc.Domain.Fields;

/// <summary>
/// Axis-aligned box in page coordinates. The text layer normalises every page to 1000 × 1000 units
/// (docs/04-known-format-automation.md) so boxes survive dpi and page-size changes.
/// </summary>
public readonly record struct BoundingBox(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;

    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;

    public bool Intersects(BoundingBox other) =>
        X0 <= other.X1 && other.X0 <= X1 && Y0 <= other.Y1 && other.Y0 <= Y1;
}

/// <summary>Where a value was read from: the method and, when known, the page, box and raw text.</summary>
public sealed record FieldSource(
    SourceMethod Method,
    int? Page = null,
    BoundingBox? BBox = null,
    string? RawText = null)
{
    public static readonly FieldSource Xml = new(SourceMethod.StructuredXml);
    public static readonly FieldSource Human = new(SourceMethod.Human);
}
