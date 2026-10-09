using System.Net.Http.Json;
using System.Text.Json;

namespace LearnRagApp;

internal sealed class RagService
{
    private readonly HttpClient httpClient;
    private readonly RagSettings settings;

    public RagService(HttpClient httpClient, IConfiguration configuration)
    {
        this.httpClient = httpClient;
        settings = RagSettings.FromConfiguration(configuration);
    }

    private const string SearchApiVersion = "2024-07-01";

    private const string SystemMessage = "\n" + """
        あなたはユーザーの質問に回答するチャットボットです。
        回答については、「Sources:」以下に記載されている内容に基づいて回答してください。回答は簡潔にしてください。
        「Sources:」に記載されている情報以外の回答はしないでください。
        情報が複数ある場合は「Sources:」のあとに[Source1]、[Source2]、[Source3]のように記載されますので、それに基づいて回答してください。
        また、ユーザーの質問に対して、Sources:以下に記載されている内容に基づいて適切な回答ができない場合は、「すみません。わかりません。」と回答してください。
        回答の中に情報源の提示は含めないでください。例えば、回答の中に「[Source1]」や「Sources:」という形で情報源を示すことはしないでください。
        """ + "\n";

    public async Task<string> AnswerAsync(string question, CancellationToken cancellationToken = default)
    {
        var embedding = await CreateEmbeddingAsync(question, cancellationToken);
        var results = await SearchAsync(embedding, cancellationToken);
        var source = string.Join("\n", results.Select(result => $"[Source{result.Id}]: {result.Content}"));

        var userMessage = $"\n    {question}\n\n    Sources:\n    {source}\n    ";
        return await CreateChatCompletionAsync(userMessage, cancellationToken);
    }

    public async Task<float[]> CreateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        var uri = AzureOpenAiUri(settings, settings.EmbeddingModelName, "embeddings");
        using var request = CreateAzureOpenAiRequest(settings, uri, new { input = text });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Azure OpenAI の埋め込み生成", cancellationToken);

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return json.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(value => value.GetSingle())
            .ToArray();
    }

    public async Task IndexDocumentAsync(
        string id,
        string content,
        float[] contentVector,
        CancellationToken cancellationToken = default)
    {
        var endpoint = $"{settings.SearchEndpoint.TrimEnd('/')}/indexes/{Uri.EscapeDataString(settings.SearchIndexName)}/docs/index?api-version={SearchApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("api-key", settings.SearchApiKey);
        request.Content = JsonContent.Create(new
        {
            value = new Dictionary<string, object>[]
            {
                new()
                {
                    ["@search.action"] = "upload",
                    ["id"] = id,
                    ["content"] = content,
                    ["contentVector"] = contentVector
                }
            }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Azure AI Search への登録", cancellationToken);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] vector,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{settings.SearchEndpoint.TrimEnd('/')}/indexes/{Uri.EscapeDataString(settings.SearchIndexName)}/docs/search?api-version={SearchApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("api-key", settings.SearchApiKey);
        request.Content = JsonContent.Create(new
        {
            vectorQueries = new[]
            {
                new
                {
                    kind = "vector",
                    vector,
                    k = 3,
                    fields = "contentVector"
                }
            },
            select = new[] { "id", "content" }
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Azure AI Search の検索", cancellationToken);

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(item => new SearchResult(
                ReadAsString(item.GetProperty("id")),
                item.TryGetProperty("content", out var content) ? content.GetString() ?? "" : ""))
            .ToArray();
    }

    private async Task<string> CreateChatCompletionAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var uri = AzureOpenAiUri(settings, settings.ChatModelName, "chat/completions");
        using var request = CreateAzureOpenAiRequest(settings, uri, new
        {
            messages = new[]
            {
                new { role = "system", content = SystemMessage },
                new { role = "user", content = userMessage }
            }
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Azure OpenAI の回答生成", cancellationToken);

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content");
        return content.ValueKind == JsonValueKind.Null ? "" : content.GetString() ?? "";
    }

    private static string AzureOpenAiUri(RagSettings settings, string deployment, string operation)
    {
        var endpoint = settings.OpenAiEndpoint.TrimEnd('/');
        var encodedDeployment = Uri.EscapeDataString(deployment);
        var encodedApiVersion = Uri.EscapeDataString(settings.OpenAiApiVersion);
        return $"{endpoint}/openai/deployments/{encodedDeployment}/{operation}?api-version={encodedApiVersion}";
    }

    private static HttpRequestMessage CreateAzureOpenAiRequest(
        RagSettings settings,
        string uri,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("api-key", settings.OpenAiApiKey);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static string ReadAsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => value.ToString()
    };

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{operation}に失敗しました ({(int)response.StatusCode}): {body}",
            null,
            response.StatusCode);
    }

    private sealed record SearchResult(string Id, string Content);
}

internal sealed record RagSettings(
    string SearchEndpoint,
    string SearchApiKey,
    string SearchIndexName,
    string OpenAiEndpoint,
    string OpenAiApiVersion,
    string OpenAiApiKey,
    string EmbeddingModelName,
    string ChatModelName)
{
    public static RagSettings FromConfiguration(IConfiguration configuration) => new(
        Required(configuration, "Rag:Search:Endpoint"),
        Required(configuration, "Rag:Search:ApiKey"),
        Required(configuration, "Rag:Search:IndexName"),
        Required(configuration, "Rag:OpenAI:Endpoint"),
        Required(configuration, "Rag:OpenAI:ApiVersion"),
        Required(configuration, "Rag:OpenAI:ApiKey"),
        Required(configuration, "Rag:OpenAI:EmbeddingModelName"),
        Required(configuration, "Rag:OpenAI:ChatModelName"));

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"設定値 {key} が appsettings.Development.json にありません。");
}
