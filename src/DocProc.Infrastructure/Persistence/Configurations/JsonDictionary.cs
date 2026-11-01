using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocProc.Infrastructure.Persistence.Configurations;

/// <summary>Maps a string-keyed dictionary to a json column; EF has no built-in mapping for dictionaries.</summary>
internal static class JsonDictionary
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<Dictionary<string, TValue>> AsJson<TValue>(this PropertyBuilder<Dictionary<string, TValue>> property)
    {
        var converter = new ValueConverter<Dictionary<string, TValue>, string>(
            v => JsonSerializer.Serialize(v, Options),
            v => JsonSerializer.Deserialize<Dictionary<string, TValue>>(v, Options) ?? new Dictionary<string, TValue>());

        var comparer = new ValueComparer<Dictionary<string, TValue>>(
            (a, b) => DictionariesEqual(a, b),
            d => d.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key, kv.Value)),
            d => new Dictionary<string, TValue>(d));

        return property.HasConversion(converter, comparer).HasColumnType("json");
    }

    private static bool DictionariesEqual<TValue>(Dictionary<string, TValue>? a, Dictionary<string, TValue>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }
}
