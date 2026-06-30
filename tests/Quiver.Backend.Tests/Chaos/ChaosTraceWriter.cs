namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// chaos verification 失敗時に <c>chaos-trace.log</c> を artifact 化するための薄いライタ。
/// 同名ファイルが既にあれば append し、ファイル先頭に scenario ヘッダを書く。
///
/// 既定パスは <c>%TEMP%/quiver-chaos-trace.log</c>。CI ジョブはこのファイルを artifact
/// として保存することを想定。
/// </summary>
internal static class ChaosTraceWriter
{
    public static string DefaultPath { get; } =
        Path.Combine(Path.GetTempPath(), "quiver-chaos-trace.log");

    public static void Append(string trace)
    {
        try
        {
            File.AppendAllText(
                DefaultPath,
                $"=== {DateTimeOffset.UtcNow:O} ==={Environment.NewLine}{trace}{Environment.NewLine}");
        }
        catch
        {
            // テスト失敗を artifact 書込み失敗で覆い隠さない: 失敗例外は呼び出し側の Assert に任せる。
        }
    }
}
