# DOC-3 — XML ドキュメント改善 (欠落補完 + TASK 番号ノイズ除去)

> 作成: 2026-06-08 / 対象ブランチ: develop
> 起点: ユーザ指示。public ユーザが触れる型/メンバに XML コメント欠落があり、また既存コメントに
> 開発時 TASK 番号 (ARCH/FT/GC 等) が紛れ込み難解。ただし着手タスクの追跡性は重要なので、
> 追跡情報は通常の `//` ソースコメントへ退避してよい。

## 目的

1. **(A) 欠落補完**: public でユーザが触れる型/メソッド/プロパティに XML doc (`/// <summary>`) を付ける。
2. **(B) ノイズ除去**: XML doc (`///`) 内の開発 TASK 番号 (`ARCH-N`/`FT-N`/`GC-N`/`PW-N`/`VEC-N`/`BA-N`/
   `OP-N`/`TS-N`/`OB-N`/`DOC-N`/`CI-N`) を読み手向けの説明に書き換える。**追跡が有用な箇所は
   同義の `//` 通常コメントへ退避**してよい (XML doc からは消すが履歴は残す)。

## 現状計測 (2026-06-08)

- **(B) TASK 番号を含む `///` 行**: **586 箇所 / 167 ファイル** (`src/Quiver`)。
  - 検出: `grep -rnE "///.*\b(ARCH|FT|GC|PW|VEC|BA|OP|TS|OB|DOC|CI)-[0-9]" --include=*.cs src/Quiver`
- **(A) 欠落 (CS1591)**: `Directory.Build.props` の `<NoWarn>` に `1591` があり「段階導入のため抑制」中。
  正確な列挙は **一時的に NoWarn から `1591` を外してビルド**し `warning CS1591` を集計する
  (コマンドラインの `-p:NoWarn=` 上書きは props が再付与するため効かない → props を直接編集して計測)。

## 方針 / スコープ

- **優先順位**: 安定 public サーフェス (`tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt` に載る型)
  を最優先。internal は対象外 (XML doc 不要)。
- **(B) の書き換え判断 (機械置換しない・1 行ずつ判断)**:
  - 番号が説明の本質でない → 番号だけ削り説明は残す/読みやすく整える。
  - 番号に追跡価値がある (なぜその実装かの来歴) → XML doc からは外し、直近に `// <TASK>: 理由` の
    通常コメントを残す。
  - 「`GC-1:` の前置き」のような体裁は除去し、機能説明として書き直す。
- **(A)**: 1〜2 文の簡潔な日本語 summary。引数/戻り値が自明でなければ `<param>`/`<returns>`。
  既存スタイル (周囲の doc) に合わせる。
- **不変条件**: 挙動・シグネチャ不変。**PublicApi baseline 不変** (doc のみ)。日本語コメントは
  **byte 安全ツール (perl)** で扱い、PowerShell `Set-Content` は使わない (mojibake 回避)。

## 進め方 (増分)

大きいので分割推奨。例:
1. 増分1: 公開 facade (`GraphDatabase` / `IGraphTransaction` / `GraphTraversal` / `TypedGraphTraversal` /
   `P` / 属性 / `IGraphNode`/`IGraphRelationship`) の (A)欠落 + (B)ノイズ除去。
2. 増分2: `Quiver.Storage.Records` / `ISchemaApi` / 例外型など公開型。
3. 増分3: 残りの public サーフェス。
4. 最終: 可能なら `Directory.Build.props` の NoWarn から `1591` を外し、欠落ゼロを CI で固定 (DOC-2/CI-1 連動)。

## 読むべきファイル

- `Directory.Build.props` (`NoWarn` の `1591`)
- `tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt` (対象サーフェスの正本)
- `docs/api-stability.md` (安定対象の定義) / FT-14 で整備した docfx 設定
- 番号ノイズの多いファイル: `grep -rlE "///.*\b(ARCH|FT|GC|...)-[0-9]" src/Quiver` の上位

## 完了条件 (案)

- 安定 public サーフェスの XML doc 欠落が解消 (CS1591 = 0、または NoWarn から 1591 を外せる)。
- public 型/メンバの `///` から開発 TASK 番号が除去され、読み手向けの説明になっている
  (追跡が要る箇所は `//` に退避済)。
- `dotnet build` 緑、`PublicApi` baseline 不変、全テスト緑。
