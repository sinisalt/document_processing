using System.Reflection;
using System.Text.Json;
using DocProc.Application.Abstractions;
using DocProc.Pipeline.Layouts;
using DocProc.Pipeline.Routing;

namespace DocProc.Api.Tests.Docs;

/// <summary>
/// Every document event type the backend writes to the timeline has a label in the portal's en and de messages
/// (<c>events.document.*</c>): the Timeline tab otherwise shows the raw key, such as
/// <c>document.supplier_identified_by_source</c>.
/// </summary>
public sealed class TimelineLabelTests
{
    /// <summary>The classes whose public constants are the timeline's event types.</summary>
    private static readonly Type[] EventTypeClasses = [typeof(DocumentEventTypes), typeof(RoutingEventTypes), typeof(LayoutEventTypes)];

    public static TheoryData<string> Locales => new() { "en", "de" };

    [Theory]
    [MemberData(nameof(Locales))]
    public void Every_document_event_type_has_a_timeline_label(string locale)
    {
        using var messages = JsonDocument.Parse(RepositoryFiles.Read("web", "apps", "customer-portal", "src", "i18n", locale, "portal.json"));
        var labels = messages.RootElement.GetProperty("events").GetProperty("document");
        var eventTypes = EventTypeClasses
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.StartsWith("document.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        eventTypes.Count.ShouldBeGreaterThan(20);

        var missing = eventTypes
            .Where(e => !labels.TryGetProperty(e["document.".Length..], out var label) || label.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(label.GetString()))
            .ToList();
        missing.ShouldBeEmpty($"events.document labels missing in {locale}/portal.json");
    }

    [Fact]
    public void The_API_writes_no_document_event_type_outside_the_constant_classes()
    {
        // A literal "document.x" event type in an endpoint escapes the check above; endpoints name the constants.
        var literals = Directory.EnumerateFiles(RepositoryFiles.Path("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(line => (File: f, Line: line)))
            .Where(l => l.Line.Contains("AddEvent(", StringComparison.Ordinal) || l.Line.Contains("Timeline.Record(", StringComparison.Ordinal) || l.Line.Contains("timeline.Record(", StringComparison.Ordinal))
            .Where(l => l.Line.Contains("\"document.", StringComparison.Ordinal))
            .Select(l => $"{Path.GetFileName(l.File)}: {l.Line.Trim()}")
            .ToList();
        literals.ShouldBeEmpty();
    }
}
