using System.Text;
using System.Text.RegularExpressions;

namespace LearnRagApp;

internal static class RecursiveTextSplitter
{
    private static readonly string[] Separators = ["\n\n", "\n", "。", "、", " ", ""];
    private const int ChunkSize = 1000;
    private const int ChunkOverlap = 200;

    public static IReadOnlyList<string> SplitText(string text) => Split(text, 0);

    private static List<string> Split(string text, int separatorStart)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var separatorIndex = separatorStart;
        while (separatorIndex < Separators.Length - 1 &&
               !text.Contains(Separators[separatorIndex], StringComparison.Ordinal))
        {
            separatorIndex++;
        }

        var separator = Separators[separatorIndex];
        var pieces = SplitKeepingSeparator(text, separator);
        var chunks = new List<string>();
        var mergeable = new List<string>();

        foreach (var piece in pieces)
        {
            if (LengthInCodePoints(piece) < ChunkSize)
            {
                mergeable.Add(piece);
                continue;
            }

            chunks.AddRange(Merge(mergeable));
            mergeable.Clear();

            if (separatorIndex + 1 < Separators.Length)
            {
                chunks.AddRange(Split(piece, separatorIndex + 1));
            }
            else
            {
                chunks.Add(piece);
            }
        }

        chunks.AddRange(Merge(mergeable));
        return chunks;
    }

    private static List<string> SplitKeepingSeparator(string text, string separator)
    {
        if (separator.Length == 0)
        {
            var characters = new List<string>();
            foreach (var rune in text.EnumerateRunes())
            {
                characters.Add(rune.ToString());
            }

            return characters;
        }

        var splitParts = Regex.Split(text, $"({Regex.Escape(separator)})");
        var pieces = new List<string>();
        if (splitParts[0].Length > 0)
        {
            pieces.Add(splitParts[0]);
        }

        for (var index = 1; index < splitParts.Length; index += 2)
        {
            var piece = splitParts[index] + (index + 1 < splitParts.Length ? splitParts[index + 1] : "");
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }
        }

        return pieces;
    }

    private static List<string> Merge(IReadOnlyList<string> pieces)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var totalLength = 0;

        foreach (var piece in pieces)
        {
            var pieceLength = LengthInCodePoints(piece);
            if (totalLength + pieceLength > ChunkSize)
            {
                if (current.Count > 0)
                {
                    AddTrimmedChunk(chunks, current);
                }

                while (totalLength > ChunkOverlap ||
                       (totalLength + pieceLength > ChunkSize && totalLength > 0))
                {
                    if (current.Count == 0)
                    {
                        break;
                    }

                    totalLength -= LengthInCodePoints(current[0]);
                    current.RemoveAt(0);
                }
            }

            current.Add(piece);
            totalLength += pieceLength;
        }

        if (current.Count > 0)
        {
            AddTrimmedChunk(chunks, current);
        }

        return chunks;
    }

    private static void AddTrimmedChunk(List<string> chunks, List<string> pieces)
    {
        var chunk = string.Concat(pieces).Trim();
        if (chunk.Length > 0)
        {
            chunks.Add(chunk);
        }
    }

    private static int LengthInCodePoints(string value)
    {
        var length = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            length++;
        }

        return length;
    }
}
