using System.Globalization;
using System.Text;

namespace Quiver.Embedding.Text;

/// <summary>絵文字の正規化方針を制御する。</summary>
public sealed class EmojiPolicy
{
    /// <summary>絵文字の扱いを指定するモード。</summary>
    public EmojiHandling Mode { get; init; } = EmojiHandling.PassThrough;

    /// <summary>
    /// アクティブなプロバイダが <c>EmojiTokenizationQuality.Poor</c> を報告し、モードが
    /// <c>PassThrough</c> のときに、一度だけ警告ログを出力する。パイプライン側で接続される — ロガーが
    /// 設定されていない場合は無視される。
    /// </summary>
    public bool LogWarningOnPoorProvider { get; init; } = true;

    /// <summary>ポリシーに従って入力テキストを変換する。</summary>
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

    // ヒューリスティック: 先頭 rune が emoji 系コードポイントである grapheme を「絵文字クラスタ」と見なす。
    // 既定ポリシー用途には十分。完全な UAX #51 準拠には NeoSmart.Unicode のテーブルが必要で、
    // VEC-4 のスコープ外。
    private static bool IsEmojiCluster(string grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes())
        {
            int v = rune.Value;
            if (v >= 0x1F300 && v <= 0x1FAFF) return true; // misc symbols & pictographs / emoticons / supplemental
            if (v >= 0x2600  && v <= 0x27BF)  return true; // dingbats / misc symbols
            if (v == 0x200D) continue;
            if (v >= 0x1F1E6 && v <= 0x1F1FF) return true; // regional indicators
            if (v == 0xFE0F) continue;
            if (v >= 0x1F3FB && v <= 0x1F3FF) return true; // skin-tone modifiers
            return false;
        }
        return false;
    }
}

/// <summary>絵文字処理モード。</summary>
public enum EmojiHandling : byte
{
    /// <summary>そのまま透過させる。</summary>
    PassThrough = 0,
    /// <summary>絵文字を除去する。</summary>
    Strip = 1,
    /// <summary>絵文字を 1 個のスペースに置換する。</summary>
    ReplaceWithSpace = 2,
    /// <summary>プロバイダのケイパビリティに応じて自動選択する。</summary>
    Auto = 3,
}
