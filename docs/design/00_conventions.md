# 開発規約

> 開発者向けの規約である。
> 現行実装の仕様は `docs/spec/` を正本とする。

## 正本の優先順位

`docs/spec/` は current as-built 仕様である。

target の API、永続形式、並行性を current の実装済み契約として記述してはならない。

各変更では、どちらに基づく記述かを明示する。

## 現行トラックと historical record

現在の計画は [plans/README.md](../../plans/README.md) から辿る。

過去トラックの完了記録、commit hash、当時の API、実測値は historical record として保存する。

historical record は再設計の実装順序や再実装禁止の根拠ではない。

再設計計画の disposition が Delete または Rewrite を指定するとき、過去に完了と記録された実装も対象になる。

tracked 文書から historical record を参照するときは、現行指示ではないことを明記する。

## ローカルの作業モデル

開発は `develop` を基準に行う。

物理コピーした別ライブラリや、成果物を後から差し替える並行開発は行わない。

## skill mirror と可変状態

`.agents/` と `.claude/` は git 管理外のローカル設定である。

`quiver-implement` の両 mirror は byte-for-byte で一致させる。

mirror の更新は tracked commit に混ぜない。

承認済み計画と decision log は tracked な `plans/` に置く。

## 変更の検証

各変更は機能 test、必要な crash test、性能への影響がある場合の baseline gate、as-built 更新を満たしてから統合候補に進む。

crash test または baseline gate を `N/A` とするには、対象挙動を変更していないことを差分で示す。

solution build、変更した contract の as-built 更新、変更固有の機能 test は `N/A` にできない。

性能 gate が未達なら、統合せず、原因と再設計案を decision log に記録してユーザー判断を得る。

## コメントと内部管理表記

コードは How、テストは What、commit message は Why、source comment は Why not を担う。

公開 API には利用者向けの日本語 XML doc を付ける。

`csharp-xml-comment` skill が利用できない場合は、通常の XML doc と source comment の review で確認する。

Wave 番号や代替案などの内部管理表記は、`plans/` と `docs/design/` 以外の source、test、benchmark、公開 docs に残さない。

検査の設計と対象パターンは [development.md のエージェント運用ガードレール](development.md#エージェント運用ガードレール) を正本とする。
