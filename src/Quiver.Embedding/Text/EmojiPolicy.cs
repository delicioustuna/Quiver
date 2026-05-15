using System.Globalization;
using System.Text;

namespace Quiver.Embedding.Text;

public sealed class EmojiPolicy
{
    public EmojiHandling Mode { get; init; } = EmojiHandling.PassThrough;

    /// <summary>
    /// Log a one-shot warning when the active provider reports
    /// <c>EmojiTokenizationQuality.Poor</c> and mode is <c>PassThrough</c>.
    /// Hooked up by the pipeline; ignored if no logger is attached.
    /// </summary>
    public bool LogWarningOnPoorProvider { get; init; } = true;

    public string Apply(string input)
    {
        if (Mode == EmojiHandling.PassThrough) return input;

        var e = StringInfo.GetTextElementEnumerator(input);
        var sb = new StringBuilder(input.Length);
        while (e.MoveNext())
        {
            string g = (string)e.Current;
            if (IsEmojiCluster(g))
            {
                switch (Mode)
                {
                    case EmojiHandling.Strip: continue;
                    case EmojiHandling.ReplaceWithSpace: sb.Append(' '); continue;
                    default: sb.Append(g); continue;
                }
            }
            sb.Append(g);
        }
        return sb.ToString();
    }

    // Heuristic: an "emoji cluster" is any grapheme whose first rune is
    // an emoji-ish code point. Good enough for default policy work — full
    // UAX #51 conformance would require the NeoSmart.Unicode tables and is
    // out of scope for VEC-4.
    private static bool IsEmojiCluster(string grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes())
        {
            int v = rune.Value;
            if (v >= 0x1F300 && v <= 0x1FAFF) return true; // misc symbols & pictographs, emoticons, supplemental
            if (v >= 0x2600  && v <= 0x27BF)  return true; // dingbats, misc symbols
            if (v == 0x200D) continue;
            if (v >= 0x1F1E6 && v <= 0x1F1FF) return true; // regional indicators
            if (v == 0xFE0F) continue;
            if (v >= 0x1F3FB && v <= 0x1F3FF) return true; // skin-tone modifiers
            return false;
        }
        return false;
    }
}

public enum EmojiHandling : byte
{
    PassThrough = 0,
    Strip = 1,
    ReplaceWithSpace = 2,
    Auto = 3,
}
