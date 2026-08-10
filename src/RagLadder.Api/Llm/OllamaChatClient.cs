using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;

namespace RagLadder.Api.Llm;

/// <summary>
/// Ollama Cloud is not OpenAI-SDK compatible, so this talks to /api/chat directly (spec §4.2).
/// Enforces the mandated rate-limit mitigations: max two concurrent calls and exponential
/// backoff on 429/503 with three attempts at 1s/2s/4s (spec §4.1).
/// </summary>
public sealed class OllamaChatClient : IChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaChatClient> _log;
    // Two gates, not one. Bulk extraction can be dozens of calls deep; an interactive question
    // sharing that queue would wait for all of them — measured at ~85 minutes mid-processing.
    // Separate gates mean a question waits for at most one in-flight bulk call.
    private readonly SemaphoreSlim _bulkGate;
    private readonly SemaphoreSlim _interactiveGate;
    private int _liveCalls;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Kind => "ollama";
    public string ChatModel => _options.ChatModel;
    public string ExtractionModel => _options.ExtractionModel;
    public int LiveCallCount => Volatile.Read(ref _liveCalls);

    public OllamaChatClient(HttpClient http, IOptions<RagLadderOptions> options, ILogger<OllamaChatClient> log)
    {
        _options = options.Value.Ollama;
        _log = log;
        _bulkGate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        _interactiveGate = new SemaphoreSlim(1);
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var body = new OllamaChatBody
        {
            Model = request.Model,
            Stream = false,
            Format = request.JsonOnly ? "json" : null,
            Messages = [.. request.Messages.Select(m => new OllamaMessage { Role = m.Role, Content = m.Content })],
            Options = new OllamaRuntimeOptions
            {
                Temperature = request.Temperature,
                NumCtx = _options.NumCtx > 0 ? _options.NumCtx : null,
            }
        };

        var delays = new[] { 1000, 2000, 4000 };
        var attempts = Math.Max(1, _options.MaxRetries);
        Exception? last = null;
        var sw = Stopwatch.StartNew();
        var gate = ChatPurpose.IsBulk(request.Purpose) ? _bulkGate : _interactiveGate;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await gate.WaitAsync(ct);
            try
            {
                Interlocked.Increment(ref _liveCalls);
                using var response = await _http.PostAsJsonAsync("api/chat", body, Json, ct);

                if (IsTransient(response.StatusCode) && attempt < attempts - 1)
                {
                    _log.LogWarning("Ollama returned {Status}; retrying in {Delay}ms (attempt {Attempt}/{Total}).",
                        (int)response.StatusCode, delays[Math.Min(attempt, delays.Length - 1)], attempt + 1, attempts);
                    await Task.Delay(delays[Math.Min(attempt, delays.Length - 1)], ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    return Failure(request, sw, $"Ollama {(int)response.StatusCode}: {Trim(detail)}");
                }

                var payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(Json, ct);
                var content = payload?.Message?.Content ?? "";
                return new ChatResult
                {
                    Content = content,
                    Model = request.Model,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    PromptTokens = payload?.PromptEvalCount,
                    CompletionTokens = payload?.EvalCount,
                };
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"Ollama call timed out after {_options.TimeoutSeconds}s.");
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

        return Failure(request, sw, last?.Message ?? "Ollama call failed.");
    }

    private static ChatResult Failure(ChatRequest request, Stopwatch sw, string message) => new()
    {
        Content = "",
        Model = request.Model,
        ElapsedMs = sw.ElapsedMilliseconds,
        Failed = true,
        Warning = message
    };

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
             or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>A local Ollama needs no credentials; only the hosted service does.</summary>
    private bool IsLocal =>
        _http.BaseAddress is { IsLoopback: true } ||
        _http.BaseAddress?.Host is "host.docker.internal" or "ollama";

    public async Task<ProviderHealth> HealthAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) && !IsLocal)
            return new ProviderHealth("ollama", ProviderHealth.NotConfigured,
                "No API key configured. A hosted Ollama needs one; a local instance does not — " +
                "set Ollama:BaseUrl to http://localhost:11434 if you are running it in Docker.");

        try
        {
            using var response = await _http.GetAsync("api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return new ProviderHealth("ollama", ProviderHealth.Unreachable,
                    $"GET /api/tags returned {(int)response.StatusCode}.");

            if (!_options.ValidateTagsAtStartup)
                return new ProviderHealth("ollama", ProviderHealth.Ok, "Reachable (tag validation disabled).");

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(Json, ct);
            var names = tags?.Models?.Select(m => m.Name).Where(n => n is not null).Cast<string>().ToArray() ?? [];
            var missing = new[] { _options.ChatModel, _options.ExtractionModel }
                .Distinct()
                .Where(m => names.Length > 0 && !names.Contains(m, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            // Cloud tags carry a -cloud suffix and the catalog changes often, so a missing tag is
            // reported as degraded rather than fatal — the call may still succeed.
            var where = IsLocal ? $"local Ollama at {_http.BaseAddress}" : "Ollama Cloud";
            return missing.Length == 0
                ? new ProviderHealth("ollama", ProviderHealth.Ok,
                    $"{where}: {names.Length} model tag(s) available; chat '{_options.ChatModel}', extraction '{_options.ExtractionModel}'.")
                : new ProviderHealth("ollama", ProviderHealth.Degraded,
                    $"{where}: configured tag(s) not listed — {string.Join(", ", missing)}. " +
                    (IsLocal ? "Pull it: docker exec ragladder-ollama ollama pull <tag>." : "The cloud catalog changes often — verify the tag."));
        }
        catch (Exception ex)
        {
            return new ProviderHealth("ollama", ProviderHealth.Unreachable, ex.Message);
        }
    }

    public void Dispose() { _bulkGate.Dispose(); _interactiveGate.Dispose(); }

    // ----- wire types -----------------------------------------------------

    private sealed class OllamaChatBody
    {
        public required string Model { get; init; }
        public required List<OllamaMessage> Messages { get; init; }
        public bool Stream { get; init; }
        public string? Format { get; init; }
        public OllamaRuntimeOptions? Options { get; init; }
    }

    private sealed class OllamaRuntimeOptions
    {
        public double Temperature { get; init; }
        [JsonPropertyName("num_ctx")] public int? NumCtx { get; init; }
    }

    private sealed class OllamaMessage
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    private sealed class OllamaChatResponse
    {
        public OllamaMessage? Message { get; init; }
        [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; init; }
        [JsonPropertyName("eval_count")] public int? EvalCount { get; init; }
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaTag>? Models { get; init; }
    }

    private sealed class OllamaTag
    {
        public string? Name { get; init; }
    }
}
