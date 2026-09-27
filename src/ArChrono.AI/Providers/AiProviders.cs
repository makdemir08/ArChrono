using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArChrono.Localization;

namespace ArChrono.AI.Providers;

public enum AiProviderKind
{
    OpenAI,
    Anthropic,
    Ollama,
    AzureOpenAI,
    OpenAICompatible,
}

public sealed record AiProviderSettings
{
    public AiProviderKind Kind { get; init; } = AiProviderKind.Anthropic;

    /// <summary>Boşsa sağlayıcının varsayılan uç noktası.</summary>
    public string? Endpoint { get; init; }

    public string Model { get; init; } = DefaultModel(AiProviderKind.Anthropic);

    /// <summary>Azure OpenAI deployment adı.</summary>
    public string? Deployment { get; init; }

    public string AzureApiVersion { get; init; } = "2024-10-21";

    public static string DefaultModel(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "claude-sonnet-5",
        AiProviderKind.OpenAI => "gpt-5-mini",
        AiProviderKind.Ollama => "qwen2.5-coder",
        AiProviderKind.AzureOpenAI => "gpt-5-mini",
        _ => "",
    };

    public static IReadOnlyList<string> SuggestedModels(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => ["claude-sonnet-5", "claude-opus-5", "claude-haiku-4-5"],
        AiProviderKind.OpenAI => ["gpt-5-mini", "gpt-5", "gpt-5-nano"],
        AiProviderKind.Ollama => ["qwen2.5-coder", "llama3.2", "deepseek-coder-v2"],
        _ => [],
    };

    public static string DefaultEndpoint(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "https://api.anthropic.com",
        AiProviderKind.OpenAI => "https://api.openai.com/v1",
        AiProviderKind.Ollama => "http://localhost:11434",
        _ => "",
    };

    public string EffectiveEndpoint => string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint(Kind) : Endpoint!.TrimEnd('/');

    public bool RequiresApiKey => Kind is AiProviderKind.OpenAI or AiProviderKind.Anthropic or AiProviderKind.AzureOpenAI;

    /// <summary>Repository içeriğinin bu makineden çıkıp çıkmadığı (localhost uç noktaları hariç).</summary>
    public bool IsLocal => Uri.TryCreate(EffectiveEndpoint, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    public string CredentialKey => "ai." + Kind.ToString().ToLowerInvariant();
}

public sealed record AiPrompt(string System, string User, int MaxOutputTokens = 1024);

public sealed record AiCompletion(string Text, string Model, int? InputTokens, int? OutputTokens, TimeSpan Duration);

public sealed class AiProviderException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

public interface IAiProvider
{
    AiProviderKind Kind { get; }

    Uri Endpoint { get; }

    string Model { get; }

    Task<AiCompletion> CompleteAsync(AiPrompt prompt, CancellationToken cancellationToken = default);
}

public abstract class HttpAiProvider(HttpClient http, AiProviderSettings settings, string? apiKey) : IAiProvider
{
    protected HttpClient Http { get; } = http;
    protected AiProviderSettings Settings { get; } = settings;
    protected string? ApiKey { get; } = apiKey;

    public AiProviderKind Kind => Settings.Kind;
    public abstract Uri Endpoint { get; }
    public string Model => Settings.Model;

    public async Task<AiCompletion> CompleteAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        if (Settings.RequiresApiKey && string.IsNullOrWhiteSpace(ApiKey))
            throw new AiProviderException(Loc.T("No API key is saved for this provider. Add one in Settings → AI & Privacy.", "Bu sağlayıcı için kayıtlı API anahtarı yok. Ayarlar → AI ve Gizlilik bölümünden ekleyin."));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildBody(prompt).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddHeaders(request);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Loc.T($"Could not reach {Endpoint.Host}: {ex.Message}", $"{Endpoint.Host} adresine ulaşılamadı: {ex.Message}"), inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderException(DescribeError((int)response.StatusCode, text), (int)response.StatusCode);

            JsonNode? json;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch (JsonException ex)
            {
                throw new AiProviderException(Loc.T("The AI provider returned an unexpected response.", "AI sağlayıcısı beklenmeyen bir yanıt döndürdü."), inner: ex);
            }
            var (content, input, output) = ParseResponse(json ?? throw new AiProviderException(Loc.T("Empty response from the AI provider.", "AI sağlayıcısından boş yanıt geldi.")));
            return new AiCompletion(content.Trim(), Model, input, output, stopwatch.Elapsed);
        }
    }

    protected abstract JsonObject BuildBody(AiPrompt prompt);

    protected abstract void AddHeaders(HttpRequestMessage request);

    protected abstract (string Text, int? InputTokens, int? OutputTokens) ParseResponse(JsonNode json);

    private string DescribeError(int status, string body)
    {
        var detail = TryReadErrorMessage(body);
        return status switch
        {
            401 or 403 => Loc.T("The AI provider rejected the API key.", "AI sağlayıcısı API anahtarını reddetti.") + Suffix(detail),
            404 => Loc.T($"Model or endpoint not found ({Model}).", $"Model veya endpoint bulunamadı ({Model}).") + Suffix(detail),
            429 => Loc.T("The AI provider is rate limiting requests. Try again shortly.", "AI sağlayıcısı istekleri sınırlıyor. Biraz sonra tekrar deneyin.") + Suffix(detail),
            >= 500 => Loc.T("The AI provider had a server error.", "AI sağlayıcısında bir sunucu hatası oluştu.") + Suffix(detail),
            _ => Loc.T($"The AI request failed ({status}).", $"AI isteği başarısız oldu ({status}).") + Suffix(detail),
        };

        static string Suffix(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : " " + detail;
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            var json = JsonNode.Parse(body);
            return json?["error"]?["message"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? json?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    protected static JsonArray Messages(AiPrompt prompt, bool includeSystem) =>
        includeSystem
            ? [new JsonObject { ["role"] = "system", ["content"] = prompt.System }, new JsonObject { ["role"] = "user", ["content"] = prompt.User }]
            : [new JsonObject { ["role"] = "user", ["content"] = prompt.User }];
}

/// <summary>OpenAI ve OpenAI-uyumlu uç noktalar (Chat Completions).</summary>
public sealed class OpenAiCompatibleProvider(HttpClient http, AiProviderSettings settings, string? apiKey) : HttpAiProvider(http, settings, apiKey)
{
    public override Uri Endpoint => new(Settings.EffectiveEndpoint + "/chat/completions");

    protected override JsonObject BuildBody(AiPrompt prompt) => new()
    {
        ["model"] = Settings.Model,
        ["messages"] = Messages(prompt, includeSystem: true),
    };

    protected override void AddHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    protected override (string, int?, int?) ParseResponse(JsonNode json) =>
        (json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new AiProviderException(Loc.T("The response did not contain a message.", "Yanıtta bir mesaj yoktu.")),
         json["usage"]?["prompt_tokens"]?.GetValue<int>(), json["usage"]?["completion_tokens"]?.GetValue<int>());
}

public sealed class AzureOpenAiProvider(HttpClient http, AiProviderSettings settings, string? apiKey) : HttpAiProvider(http, settings, apiKey)
{
    public override Uri Endpoint =>
        new($"{Settings.EffectiveEndpoint}/openai/deployments/{Uri.EscapeDataString(Settings.Deployment ?? Settings.Model)}/chat/completions?api-version={Uri.EscapeDataString(Settings.AzureApiVersion)}");

    protected override JsonObject BuildBody(AiPrompt prompt) => new() { ["messages"] = Messages(prompt, includeSystem: true) };

    protected override void AddHeaders(HttpRequestMessage request) => request.Headers.Add("api-key", ApiKey);

    protected override (string, int?, int?) ParseResponse(JsonNode json) =>
        (json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new AiProviderException(Loc.T("The response did not contain a message.", "Yanıtta bir mesaj yoktu.")),
         json["usage"]?["prompt_tokens"]?.GetValue<int>(), json["usage"]?["completion_tokens"]?.GetValue<int>());
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider(HttpClient http, AiProviderSettings settings, string? apiKey) : HttpAiProvider(http, settings, apiKey)
{
    public override Uri Endpoint => new(Settings.EffectiveEndpoint + "/v1/messages");

    protected override JsonObject BuildBody(AiPrompt prompt) => new()
    {
        ["model"] = Settings.Model,
        ["max_tokens"] = prompt.MaxOutputTokens,
        ["system"] = prompt.System,
        ["messages"] = Messages(prompt, includeSystem: false),
    };

    protected override void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("x-api-key", ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    protected override (string, int?, int?) ParseResponse(JsonNode json)
    {
        var parts = json["content"]?.AsArray()
            .Where(p => p?["type"]?.GetValue<string>() == "text")
            .Select(p => p!["text"]!.GetValue<string>())
            .ToList();
        if (parts is null || parts.Count == 0) throw new AiProviderException(Loc.T("The response did not contain text.", "Yanıtta metin yoktu."));
        return (string.Concat(parts), json["usage"]?["input_tokens"]?.GetValue<int>(), json["usage"]?["output_tokens"]?.GetValue<int>());
    }
}

/// <summary>Yerel Ollama (/api/chat). Veri makineden çıkmaz.</summary>
public sealed class OllamaProvider(HttpClient http, AiProviderSettings settings) : HttpAiProvider(http, settings, null)
{
    public override Uri Endpoint => new(Settings.EffectiveEndpoint + "/api/chat");

    protected override JsonObject BuildBody(AiPrompt prompt) => new()
    {
        ["model"] = Settings.Model,
        ["stream"] = false,
        ["messages"] = Messages(prompt, includeSystem: true),
    };

    protected override void AddHeaders(HttpRequestMessage request)
    {
    }

    protected override (string, int?, int?) ParseResponse(JsonNode json) =>
        (json["message"]?["content"]?.GetValue<string>() ?? throw new AiProviderException(Loc.T("The response did not contain a message.", "Yanıtta bir mesaj yoktu.")),
         json["prompt_eval_count"]?.GetValue<int>(), json["eval_count"]?.GetValue<int>());
}

public static class AiProviderFactory
{
    public static IAiProvider Create(HttpClient http, AiProviderSettings settings, string? apiKey) => settings.Kind switch
    {
        AiProviderKind.Anthropic => new AnthropicProvider(http, settings, apiKey),
        AiProviderKind.Ollama => new OllamaProvider(http, settings),
        AiProviderKind.AzureOpenAI => new AzureOpenAiProvider(http, settings, apiKey),
        _ => new OpenAiCompatibleProvider(http, settings, apiKey),
    };
}
