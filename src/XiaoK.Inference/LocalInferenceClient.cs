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

    /// <summary>Emits response sizes and token/finish metadata only; response text is never included.</summary>
    public event Action<LocalInferenceResponseDiagnostics>? ResponseCompleted;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        => await CompleteAsync(systemPrompt, userPrompt, new InferenceRequestOptions(), cancellationToken);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userPrompt);
        ArgumentNullException.ThrowIfNull(options);
        if (Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(userPrompt) > MaximumPromptUtf8Bytes)
            throw new ArgumentException("Local inference prompt exceeds the configured size limit.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = "local-model",
            ["stream"] = false,
            ["messages"] = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };
        if (options.DisableThinking)
            payload["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };
        if (options.JsonSchema is { } jsonSchema)
        {
            if (!options.JsonObject || jsonSchema.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(jsonSchema.GetRawText()) > 16 * 1024)
                throw new ArgumentException("JSON response schema must be a bounded object used with JSON mode.", nameof(options));
            payload["response_format"] = new Dictionary<string, object>
            {
                ["type"] = "json_object",
                ["schema"] = jsonSchema
            };
        }
        else if (options.JsonObject)
        {
            payload["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        var root = json.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString()?.Trim()
                : null;
        var reasoningCharacters = message.TryGetProperty("reasoning_content", out var reasoningElement)
            && reasoningElement.ValueKind == JsonValueKind.String
                ? reasoningElement.GetString()?.Length ?? 0
                : 0;
        var finishReason = choice.TryGetProperty("finish_reason", out var finishElement)
            && finishElement.ValueKind == JsonValueKind.String
                ? finishElement.GetString()
                : null;
        var promptTokens = ReadTokenCount(root, "prompt_tokens");
        var completionTokens = ReadTokenCount(root, "completion_tokens");
        var diagnostics = new LocalInferenceResponseDiagnostics(content?.Length ?? 0, reasoningCharacters,
            promptTokens, completionTokens, finishReason);
        try { ResponseCompleted?.Invoke(diagnostics); }
        catch (Exception) { /* Diagnostics must never change inference behavior. */ }

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("Local inference returned empty text.");
        return content;
    }

    public void Dispose() => _http.Dispose();
    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private static int ReadTokenCount(JsonElement root, string property) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(property, out var value) && value.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
}

public sealed record LocalInferenceResponseDiagnostics(int ContentCharacters, int ReasoningCharacters,
    int PromptTokens, int CompletionTokens, string? FinishReason);
