# 開発規約

> 開発者向けの規約である。
> 現行実装の仕様は `docs/spec/`、再設計の target は [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md) を正本とする。

## 正本の優先順位

Single Writer 再設計では、設計内容は [再設計計画](../../plans/single-writer-redesign.md)、実行手順は [再設計の実行手順](../../plans/single-writer-redesign-process.md) を正本とする。

`docs/spec/` は current as-built 仕様である。

target の API、永続形式、並行性を current の実装済み契約として記述してはならない。

各変更では、どちらに基づく記述かを明示する。

## 現行トラックと historical record

現在の実装トラックは Single Writer + Snapshot Readers 再設計だけである。

過去トラックの完了記録、commit hash、当時の API、実測値は historical record として保存する。

historical record は再設計の実装順序や再実装禁止の根拠ではない。

再設計計画の disposition が Delete または Rewrite を指定するとき、過去に完了と記録された実装も対象になる。

tracked 文書から historical record を参照するときは、現行指示ではないことを明記する。

## ローカルの作業モデル

bootstrap は `develop` で行う。

bootstrap 後の再設計の tracked file は、`redesign/single-writer` の専用 worktree でだけ編集する。

専用 worktree は同じ Git repository を共有する。

物理コピーした別ライブラリや、成果物を後から差し替える並行開発は行わない。

このローカル運用では、ユーザー指示により外部 push と upstream 設定を保留している。

保留は remote と同期済みであることを意味しない。

外部公開や `develop` への統合を再開する前に、実行手順の remote hash 固定、integration candidate、push の条件を改めて満たす。

## skill mirror と可変状態

`.agents/` と `.claude/` は git 管理外のローカル設定である。

`quiver-implement` の両 mirror は byte-for-byte で一致させる。

mirror の更新は tracked commit に混ぜない。

専用 worktree に mirror は複製されないため、必要なときはメインツリー側を read-only で参照する。

Wave の進行状態、承認済み計画、decision log は tracked な `plans/` に置く。

## Wave の検証

各 Wave は機能 test、crash test、baseline gate、as-built 更新を満たしてから統合候補に進む。

crash test または baseline gate を `N/A` とするには、対象挙動を変更していないことを差分で示す。

solution build、変更した contract の as-built 更新、Wave 固有の機能 test は `N/A` にできない。

性能 gate が未達なら、次の Wave へ進めず、原因と再設計案を decision log に記録してユーザー判断を得る。

## コメントと内部管理表記

コードは How、テストは What、commit message は Why、source comment は Why not を担う。

公開 API には利用者向けの日本語 XML doc を付ける。

`csharp-xml-comment` skill が利用できない場合は、通常の XML doc と source comment の review で確認する。

Wave 番号や代替案などの内部管理表記は、`plans/` と `docs/design/` 以外の source、test、benchmark、公開 docs に残さない。

検査の設計と対象パターンは [development.md のエージェント運用ガードレール](development.md#エージェント運用ガードレール) を正本とする。
