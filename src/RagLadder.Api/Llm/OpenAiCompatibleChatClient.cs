using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;

namespace RagLadder.Api.Llm;

/// <summary>
/// Chat against any endpoint speaking the OpenAI chat-completions shape.
///
/// This exists because "we block ollama.com" is common and usually comes with a sanctioned
/// alternative — Azure OpenAI, an internal gateway, a self-hosted vLLM or llama.cpp server. The
/// wire format is near-universal, so one client covers all of them; the differences are the auth
/// header, the path, and whether JSON mode is honoured, and all three are configuration.
///
/// Enforces the same rate-limit discipline as the Ollama client: bounded concurrency and
/// exponential backoff at 1s/2s/4s on 429 and 503.
/// </summary>
public sealed class OpenAiCompatibleChatClient : IChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly OpenAiCompatibleOptions _options;
    private readonly ILogger<OpenAiCompatibleChatClient> _log;
    // Bulk extraction and interactive questions queue separately; see OllamaChatClient.
    private readonly SemaphoreSlim _bulkGate;
    private readonly SemaphoreSlim _interactiveGate;
    private int _liveCalls;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Kind => "openai-compatible";
    public string ChatModel => _options.ChatModel;
    public string ExtractionModel => _options.ExtractionModel;
    public int LiveCallCount => Volatile.Read(ref _liveCalls);

    public OpenAiCompatibleChatClient(HttpClient http, IOptions<RagLadderOptions> options,
        ILogger<OpenAiCompatibleChatClient> log)
    {
        _options = options.Value.OpenAiCompatible;
        _log = log;
        _bulkGate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        _interactiveGate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");

        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return;

        if (_options.AuthHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    string.IsNullOrWhiteSpace(_options.AuthScheme) ? "Bearer" : _options.AuthScheme,
                    _options.ApiKey);
        else
            _http.DefaultRequestHeaders.Add(_options.AuthHeader, _options.ApiKey);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (_http.BaseAddress is null)
            return Failure(request, Stopwatch.StartNew(), "No OpenAiCompatible:BaseUrl configured.");

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            })]),
        };
        if (_options.SendTemperature) body["temperature"] = request.Temperature;
        if (request.JsonOnly && _options.SupportsJsonMode)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };

        // Azure OpenAI puts the deployment in the path rather than the payload.
        var path = _options.ChatPath.Replace("{model}", Uri.EscapeDataString(request.Model), StringComparison.Ordinal);

        var delays = new[] { 1000, 2000, 4000 };
        var attempts = Math.Max(1, _options.MaxRetries);
        var watch = Stopwatch.StartNew();
        Exception? last = null;
        var gate = ChatPurpose.IsBulk(request.Purpose) ? _bulkGate : _interactiveGate;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await gate.WaitAsync(ct);
            try
            {
                Interlocked.Increment(ref _liveCalls);
                using var response = await _http.PostAsJsonAsync(path, body, Json, ct);

                if (IsTransient(response.StatusCode) && attempt < attempts - 1)
                {
                    var delay = RetryAfter(response) ?? delays[Math.Min(attempt, delays.Length - 1)];
                    _log.LogWarning("Chat endpoint returned {Status}; retrying in {Delay}ms (attempt {Attempt}/{Total}).",
                        (int)response.StatusCode, delay, attempt + 1, attempts);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    return Failure(request, watch, $"{(int)response.StatusCode} {response.StatusCode}: {Trim(detail)}");
                }

                var payload = await response.Content.ReadFromJsonAsync<CompletionResponse>(Json, ct);
                var content = payload?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
                return new ChatResult
                {
                    Content = content,
                    Model = request.Model,
                    ElapsedMs = watch.ElapsedMilliseconds,
                    PromptTokens = payload?.Usage?.PromptTokens,
                    CompletionTokens = payload?.Usage?.CompletionTokens,
                };
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"Chat call timed out after {_options.TimeoutSeconds}s.");
                if (attempt < attempts - 1) await Task.Delay(delays[Math.Min(attempt, delays.Length - 1)], ct);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                if (attempt < attempts - 1) await Task.Delay(delays[Math.Min(attempt, delays.Length - 1)], ct);
            }
            finally
            {
                gate.Release();
            }
        }

        return Failure(request, watch, last?.Message ?? "Chat call failed.");
    }

    public async Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return new ProviderHealth("chat", ProviderHealth.NotConfigured, "No OpenAiCompatible:BaseUrl configured.");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return new ProviderHealth("chat", ProviderHealth.NotConfigured, "No OpenAiCompatible:ApiKey configured.");

        // A one-token completion is the only probe that works everywhere: /models is optional and
        // Azure deployments do not expose it at the same path.
        var probe = await CompleteAsync(new ChatRequest
        {
            Model = _options.ChatModel,
            Messages = [ChatMessage.User("ping")],
            Temperature = 0,
            Purpose = "health",
            BypassCache = true,
        }, ct);

        return probe.Failed
            ? new ProviderHealth("chat", ProviderHealth.Unreachable,
                $"{_options.BaseUrl} did not answer: {probe.Warning}")
            : new ProviderHealth("chat", ProviderHealth.Ok,
                $"{_options.BaseUrl} reachable; chat '{_options.ChatModel}', extraction '{_options.ExtractionModel}'.");
    }

    private static ChatResult Failure(ChatRequest request, Stopwatch watch, string message) => new()
    {
        Content = "",
        Model = request.Model,
        ElapsedMs = watch.ElapsedMilliseconds,
        Failed = true,
        Warning = message,
    };

    private static int? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalMilliseconds : null;

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose() { _bulkGate.Dispose(); _interactiveGate.Dispose(); }

    private sealed class CompletionResponse
    {
        public List<Choice>? Choices { get; init; }
        public Usage? Usage { get; init; }
    }

    private sealed class Choice
    {
        public ResponseMessage? Message { get; init; }
    }

    private sealed class ResponseMessage
    {
        public string? Content { get; init; }
    }

    private sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; init; }
    }
}
