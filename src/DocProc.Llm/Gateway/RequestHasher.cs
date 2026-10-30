using System.Security.Cryptography;
using System.Text;

namespace DocProc.Llm.Gateway;

/// <summary>
/// Stable hash of everything that determines a provider's answer: model, prompt, document bytes, schema, options.
/// Used to key the replay store and to reconcile a crash between the call and the write (docs/03 "Idempotency").
/// </summary>
public static class RequestHasher
{
    public static string Hash(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(sha, request.Model);
        Append(sha, request.Prompt.CacheKey);
        Append(sha, request.Prompt.System);
        foreach (var example in request.Prompt.Examples)
        {
            Append(sha, example.Input);
            Append(sha, example.Output);
        }

        Append(sha, request.Prompt.Task);
        Append(sha, request.OutputSchema?.Json ?? string.Empty);
        Append(sha, $"{request.Options.Effort}|{request.Options.MaxOutputTokens}");

        if (request.Document is { } doc)
        {
            if (doc.Pdf is { } pdf)
            {
                sha.AppendData(pdf.Span);
            }

            foreach (var image in doc.PageImages)
            {
                sha.AppendData(image.Png.Span);
            }

            foreach (var text in doc.PageTexts)
            {
                Append(sha, text);
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static void Append(IncrementalHash sha, string value)
    {
        sha.AppendData(Encoding.UTF8.GetBytes(value));
        sha.AppendData([0]);
    }
}
