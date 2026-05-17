using Quiver.Core;
using Quiver.Embedding.Text;

namespace Quiver.Embedding;

/// <summary>
/// <see cref="TextEmbeddingPipeline"/> の起動オプション。キュー容量・バックプレッシャ方針・
/// 絵文字処理ポリシーを指定する。
/// </summary>
public sealed class TextEmbeddingPipelineOptions
{
    /// <summary>パイプラインキューの容量。既定 10000。</summary>
    public int QueueCapacity { get; init; } = 10_000;

    /// <summary>キュー満杯時のバックプレッシャ方針。</summary>
    public BackpressureMode Backpressure { get; init; } = BackpressureMode.Wait;

    /// <summary>絵文字処理ポリシー。</summary>
    public EmojiPolicy EmojiPolicy { get; init; } = new();
}

/// <summary>キュー満杯時の挙動を表す列挙体。</summary>
public enum BackpressureMode : byte
{
    /// <summary>空きが出るまで待機する。</summary>
    Wait = 0,
    /// <summary>古いエントリから捨てる。</summary>
    DropOldest = 1,
    /// <summary>新規エントリを捨てる。</summary>
    DropNewest = 2,
    /// <summary>満杯時に例外を投げる。</summary>
    ThrowOnFull = 3,
}

/// <summary>
/// <c>ScanAndEnqueueAsync</c> (Z') への入力: 走査対象のエンティティ種別、source-text を保持する
/// プロパティ、バックフィル先のベクトルインデックスを指定する。残り (content hash や冪等性チェック)
/// はパイプラインが source-text と現在のタスクログ状態から導出する。
/// </summary>
public sealed record EmbeddingScanSpec
{
    /// <summary>バックフィル対象のベクトルインデックス名。</summary>
    public required string TargetIndexName { get; init; }
    /// <summary>source-text を保持するプロパティ名。</summary>
    public required string SourcePropertyName { get; init; }
    /// <summary>走査対象のエンティティ種別。</summary>
    public required EntityKind Kind { get; init; }
}
