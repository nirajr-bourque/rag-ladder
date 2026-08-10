using System.Text.Json;
using Microsoft.Extensions.Options;
using RagLadder.Api.Configuration;
using RagLadder.Api.Infrastructure;
using RagLadder.Api.Models;

namespace RagLadder.Api.Llm;

/// <summary>
/// Content-hash cache in front of the model. The cache key folds in the request's
/// <see cref="ChatRequest.CacheScope"/>, which answer generation sets to the resolved stage flag
/// signature — so two rungs of the ladder can never share a completion (spec §7.4).
/// </summary>
public sealed class CachingChatClient(IChatClient inner, CacheRepository cache) : IChatClient
{
    public string Kind => inner.Kind + "+cache";
    public string ChatModel => inner.ChatModel;
    public string ExtractionModel => inner.ExtractionModel;
    public int LiveCallCount => inner.LiveCallCount;

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        if (request.BypassCache) return await inner.CompleteAsync(request, ct);

        var key = CacheRepository.ChatKey(
            request.Model,
            request.Purpose + "|" + request.CacheScope,
            JsonText.Fingerprint(request.Messages),
            request.Temperature);

        if (cache.GetChat(key) is { } hit)
            return new ChatResult { Content = hit, Model = request.Model, FromCache = true };

        var result = await inner.CompleteAsync(request, ct);
        if (!result.Failed && !string.IsNullOrWhiteSpace(result.Content))
            cache.PutChat(key, request.Purpose, result.Content);
        return result;
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default) => inner.HealthAsync(ct);
}

/// <summary>
/// Writes every live completion to the recordings directory so a full pass over the golden set
/// can be replayed offline on demo day (spec §12).
/// </summary>
public sealed class RecordingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly string _directory;
    private readonly ILogger<RecordingChatClient> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RecordingChatClient(IChatClient inner, IOptions<RagLadderOptions> options, ILogger<RecordingChatClient> log)
    {
        _inner = inner;
        _log = log;
        _directory = options.Value.Storage.RecordingsDirectory;
        Directory.CreateDirectory(_directory);
    }

    public string Kind => _inner.Kind + "+record";
    public string ChatModel => _inner.ChatModel;
    public string ExtractionModel => _inner.ExtractionModel;
    public int LiveCallCount => _inner.LiveCallCount;

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var result = await _inner.CompleteAsync(request, ct);
        if (result.Failed || result.FromCache) return result;

        try
        {
            var key = ReplayChatClient.KeyFor(request);
            var record = new Recording
            {
                Key = key,
                Purpose = request.Purpose,
                Model = request.Model,
                CacheScope = request.CacheScope,
                Messages = [.. request.Messages],
                Response = result.Content,
                RecordedUtc = DateTimeOffset.UtcNow
            };
            var path = Path.Combine(_directory, $"{request.Purpose}-{key[..12]}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, Json), ct);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Failed to write recording for purpose {Purpose}.", request.Purpose);
        }
        return result;
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default) => _inner.HealthAsync(ct);
}

public sealed class Recording
{
    public string Key { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Model { get; set; } = "";
    public string CacheScope { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
    public string Response { get; set; } = "";
    public DateTimeOffset RecordedUtc { get; set; }
}

/// <summary>
/// --replay: serves recorded responses from ./recordings/*.json and never touches the network.
/// An unrecorded prompt returns a flagged failure rather than a silent fabrication.
/// </summary>
public sealed class ReplayChatClient : IChatClient
{
    private readonly Dictionary<string, Recording> _byKey = new(StringComparer.Ordinal);
    private readonly ILogger<ReplayChatClient> _log;
    private readonly string _directory;
    private readonly string _chatModel;
    private readonly string _extractionModel;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ReplayChatClient(IOptions<RagLadderOptions> options, ILogger<ReplayChatClient> log)
    {
        _log = log;
        _directory = options.Value.Storage.RecordingsDirectory;
        _chatModel = options.Value.Ollama.ChatModel;
        _extractionModel = options.Value.Ollama.ExtractionModel;
        Load();
    }

    public string Kind => "replay";
    public string ChatModel => _chatModel;
    public string ExtractionModel => _extractionModel;
    public int LiveCallCount => 0;
    public int RecordingCount => _byKey.Count;
    public int MissCount { get; private set; }

    private void Load()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = JsonSerializer.Deserialize<Recording>(File.ReadAllText(file), Json);
                if (record is { Key.Length: > 0 }) _byKey[record.Key] = record;
            }
            catch (JsonException ex)
            {
                _log.LogWarning("Skipping malformed recording {File}: {Message}", file, ex.Message);
            }
        }
        _log.LogInformation("Replay mode loaded {Count} recordings from {Directory}.", _byKey.Count, _directory);
    }

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        var key = KeyFor(request);
        if (_byKey.TryGetValue(key, out var record))
            return Task.FromResult(new ChatResult
            {
                Content = record.Response,
                Model = request.Model,
                FromCache = true,
            });

        MissCount++;
        return Task.FromResult(new ChatResult
        {
            Content = "",
            Model = request.Model,
            Failed = true,
            Warning = $"Replay mode: no recording for purpose '{request.Purpose}' (key {key[..8]}). Re-record with Replay:Record=true."
        });
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct = default) =>
        Task.FromResult(_byKey.Count > 0
            ? new ProviderHealth("chat", ProviderHealth.Ok, $"Replay: {_byKey.Count} recordings, {MissCount} misses.")
            : new ProviderHealth("chat", ProviderHealth.Degraded, $"Replay: no recordings found in '{_directory}'."));

    public static string KeyFor(ChatRequest request) => Hashing.Sha256Hex(
        $"{request.Model}|{request.Purpose}|{request.CacheScope}|{request.Temperature:F2}|{JsonText.Fingerprint(request.Messages)}");
}
