using OpenAI.Chat;

namespace DocProc.Llm.OpenAi;

/// <summary>
/// The ordered <c>reasoning_effort</c> values and the "nearest supported value" rule from docs/08 "Reasoning/effort":
/// when a model rejects a value, step down (<c>xhigh → high → medium → low</c>) or, when the error lists what the
/// model accepts, pick the closest listed level. Below <c>low</c> the parameter is dropped.
/// </summary>
public static class ReasoningEffortLadder
{
    /// <summary>Ascending effort levels as they appear on the wire.</summary>
    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high", "xhigh"];

    /// <summary>
    /// The next value to try after <paramref name="rejected"/> was refused: the nearest of <paramref name="supported"/>
    /// when the server named them, otherwise one step down; null means "send no reasoning_effort".
    /// </summary>
    public static ChatReasoningEffortLevel? Degrade(ChatReasoningEffortLevel rejected, IReadOnlyList<string> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);

        var current = IndexOf(rejected.ToString());
        if (supported.Count > 0)
        {
            var candidates = supported
                .Where(value => !string.Equals(value, rejected.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
            {
                return (ChatReasoningEffortLevel?)null;
            }

            var nearest = candidates
                .Select(value => (Value: value, Index: IndexOf(value)))
                .OrderBy(c => c.Index < 0 ? int.MaxValue : Math.Abs(c.Index - current)) // unknown names last
                .ThenBy(c => c.Index > current ? 1 : 0)                                   // prefer the cheaper one on a tie
                .ThenBy(c => c.Index)
                .First();
            return new ChatReasoningEffortLevel(nearest.Value.ToLowerInvariant());
        }

        // The explicit cast matters: a bare null would go through the SDK's implicit string conversion and throw.
        return current > 0 ? new ChatReasoningEffortLevel(Levels[current - 1]) : (ChatReasoningEffortLevel?)null;
    }

    private static int IndexOf(string value)
    {
        for (var i = 0; i < Levels.Count; i++)
        {
            if (string.Equals(Levels[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
