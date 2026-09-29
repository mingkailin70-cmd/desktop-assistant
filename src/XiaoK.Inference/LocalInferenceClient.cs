using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Inference;

/// <summary>Talks only to an explicitly configured loopback llama.cpp-compatible endpoint.</summary>
public sealed class LocalInferenceClient : IInferenceClient, IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumPromptUtf8Bytes = 512 * 1024;
    private readonly HttpClient _http;

    public LocalInferenceClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Inference endpoint must be an HTTP loopback URI.", nameof(endpoint));

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        _http = new HttpClient(handler) { BaseAddress = EnsureTrailingSlash(uri), Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);
        if (Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(userPrompt) > MaximumPromptUtf8Bytes)
            throw new ArgumentException("Local inference prompt exceeds the configured size limit.");

        var payload = new
        {
            model = "local-model",
            stream = false,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        using var response = await _http.PostAsJsonAsync("v1/chat/completions", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local inference returned a non-JSON response.");
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Local inference response exceeds the configured size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var boundedContent = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (bytesRead == 0) break;
            if (boundedContent.Length + bytesRead > MaximumResponseBytes)
                throw new InvalidDataException("Local inference response exceeds the configured size limit.");
            await boundedContent.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        boundedContent.Position = 0;
        using var json = await JsonDocument.ParseAsync(boundedContent,
            new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim()
            ?? throw new InvalidDataException("Local inference returned no text.");
    }

    public void Dispose() => _http.Dispose();
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
