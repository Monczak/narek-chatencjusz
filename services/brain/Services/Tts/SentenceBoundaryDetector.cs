using System.Text;

namespace BrainService.Services.Tts;

public sealed class SentenceBoundaryDetector
{
    private readonly StringBuilder _buf = new();

    // Words (lowercased, without trailing dot) that should NOT end a sentence
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "etc",
        "eg", "ie", "fig", "vol", "approx", "dept", "est", "inc", "ltd",
        "e.g", "i.e"
    };
    
    public IEnumerable<string> Feed(string token)
    {
        _buf.Append(token);
        return DrainSentences();
    }
    
    public string Flush()
    {
        var remaining = _buf.ToString().Trim();
        _buf.Clear();
        return remaining;
    }

    private IEnumerable<string> DrainSentences()
    {
        while (true)
        {
            var boundary = FindNextBoundary();
            if (boundary < 0) yield break;

            var sentence = _buf.ToString(0, boundary).Trim();
            _buf.Remove(0, boundary);

            if (!string.IsNullOrEmpty(sentence))
                yield return sentence;
        }
    }

    private int FindNextBoundary()
    {
        var s = _buf.ToString();

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\n' && i + 1 < s.Length && s[i + 1] == '\n')
                return i + 2;

            if (c is '!' or '?')
            {
                // Need a whitespace (or end of buffer) after it
                if (i + 1 < s.Length && char.IsWhiteSpace(s[i + 1]))
                    return i + 1;
                // At end of buffer - don't flush yet; wait for next token
                continue;
            }

            if (c == '.')
            {
                // Skip ellipsis (two or more consecutive dots)
                var dotCount = 1;
                while (i + dotCount < s.Length && s[i + dotCount] == '.')
                    dotCount++;
                if (dotCount >= 2) { i += dotCount - 1; continue; }

                // Need whitespace after the dot
                if (i + 1 >= s.Length) continue;
                if (!char.IsWhiteSpace(s[i + 1])) continue;

                // Check if the preceding token is an abbreviation
                var wordEnd = i;
                var wordStart = wordEnd - 1;
                while (wordStart > 0 && !char.IsWhiteSpace(s[wordStart - 1]) && s[wordStart - 1] != '.')
                    wordStart--;
                var word = s[wordStart..wordEnd].ToLowerInvariant();

                if (Abbreviations.Contains(word)) continue;

                return i + 1;
            }
        }

        return -1;
    }
}
