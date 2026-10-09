using System.Text;

namespace LearnRagApp;

internal sealed class DocumentIndexer
{
    private readonly RagService rag;

    public DocumentIndexer(RagService rag)
    {
        this.rag = rag;
    }

    public async Task<int> IndexTextAsync(
        Stream textStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var content = await ReadTextAsync(textStream, fileName, cancellationToken);
        var chunks = RecursiveTextSplitter.SplitText(content);

        for (var index = 0; index < chunks.Count; index++)
        {
            Console.WriteLine($"{index + 1}個目のチャンクを処理中...");
            var chunk = chunks[index];
            var vector = await rag.CreateEmbeddingAsync(chunk, cancellationToken);
            await rag.IndexDocumentAsync(index.ToString(), chunk, vector, cancellationToken);
        }

        Console.WriteLine("インデックスの作成が完了しました");
        return chunks.Count;
    }

    private static async Task<string> ReadTextAsync(
        Stream textStream,
        string fileName,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"{fileName}を読み込み中...");
        using var reader = new StreamReader(textStream, Encoding.UTF8, true, 1024, true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        Console.WriteLine("テキストの読み込みが完了しました");
        return content;
    }
}
