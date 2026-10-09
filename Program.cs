using LearnRagApp;
using Microsoft.AspNetCore.Antiforgery;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<RagService>();
builder.Services.AddTransient<DocumentIndexer>();
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/indexer", (IWebHostEnvironment environment) =>
    Results.File(Path.Combine(environment.WebRootPath, "indexer.html"), "text/html"));

app.MapGet("/api/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Ok(new { requestToken = antiforgery.GetAndStoreTokens(context).RequestToken }));

app.MapPost("/api/chat", async (ChatRequest request, RagService rag) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return (IResult)Results.BadRequest(new { error = "質問を入力してください" });
    }

    try
    {
        var answer = await rag.AnswerAsync(request.Question);
        return (IResult)Results.Ok(new ChatResponse(answer));
    }
    catch (Exception exception)
    {
        return (IResult)Results.Problem(
            title: "回答の生成に失敗しました",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/index", async (IFormFile file, DocumentIndexer indexer, CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
    {
        return (IResult)Results.BadRequest(new { error = "TXTファイルを選択してください" });
    }

    if (!string.Equals(Path.GetExtension(file.FileName), ".txt", StringComparison.OrdinalIgnoreCase))
    {
        return (IResult)Results.BadRequest(new { error = "TXTファイルを選択してください" });
    }

    try
    {
        await using var stream = file.OpenReadStream();
        var chunkCount = await indexer.IndexTextAsync(stream, file.FileName, cancellationToken);
        return (IResult)Results.Ok(new IndexResponse($"インデックスの作成が完了しました（{chunkCount}個のチャンク）", chunkCount));
    }
    catch (Exception exception)
    {
        return (IResult)Results.Problem(
            title: "TXTのインデックス作成に失敗しました",
            detail: exception.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

internal sealed record ChatRequest(string Question);
internal sealed record ChatResponse(string Answer);
internal sealed record IndexResponse(string Message, int ChunkCount);
