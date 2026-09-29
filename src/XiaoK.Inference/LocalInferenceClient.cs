using System.Net.Http.Json;
using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Inference;

/// <summary>Talks only to an explicitly configured loopback llama.cpp-compatible endpoint.</summary>
public sealed class LocalInferenceClient : IInferenceClient, IDisposable
{
    private readonly HttpClient _http;

    public LocalInferenceClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Inference endpoint must be an HTTP loopback URI.", nameof(endpoint));
        _http = new HttpClient { BaseAddress = EnsureTrailingSlash(uri), Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = "local-model",
            stream = false,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        using var response = await _http.PostAsJsonAsync("v1/chat/completions", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
            ?? throw new InvalidDataException("Local inference returned no text.");
    }

    public void Dispose() => _http.Dispose();
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
