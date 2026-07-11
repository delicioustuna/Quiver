# Wave 0 着手指示書: 設計正本、as-built、運用規則の整合化

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-10
> 対応する正本のバージョン: 正本が未 commit のため、現在の draft に基づく。bootstrap 後の承認 commit で実 commit hash へ更新する。
> ステータス: draft

## 1. 着手前チェック

Wave 0 は bootstrap 完了後の専用 worktree で実行する。
メインツリーの `develop` へ直接 Wave 0 の tracked file をコミットしない。

- [ ] `plans/single-writer-redesign-process.md` §2.1 の計画文書7件が `develop` へ commit、push 済みである。
- [ ] `plans/single-writer-redesign-baseline.md` に未測定欄がなく、生出力が commit、push 済みである。
- [ ] `redesign-baseline` annotated tag が baseline 結果 commit に存在する。
- [ ] branch が `redesign/single-writer`、作業場所が専用 worktree である。
- [ ] 正本の C-1〜C-3、M-1〜M-6、m-1〜m-7 に残存矛盾がないことを review の参照先で確認した。
- [ ] `.agents` と `.claude` の `quiver-implement/SKILL.md` が byte-for-byte 一致する。historical task が redirect の場合は参照先が存在し、循環しない。ignored mirror は commit 対象にしない。
- [ ] 本書の正本 version を `git log -1 --format=%H -- plans/single-writer-redesign.md` の結果へ更新し、ユーザがコミット計画を承認した。

本書の version と status を更新する承認用 doc commit は、このチェックの唯一の例外である。
それ以外は一つでも満たさない場合に編集を開始しない。

## 2. 読む順序

1. 正本 §9 Wave 0 節。
2. 正本 §12「quiver-implement 矛盾監査」。
3. 正本 §11「学習用 docs とソースコメント」。
4. `plans/single-writer-redesign-process.md` §3から§6。
5. `docs/spec/00_overview.md`〜`08_known_limits.md`。
6. `docs/design/00_conventions.md` と `docs/design/development.md`。

## 3. コミット計画

通常 commit は各時点で `dotnet build Quiver.slnx -v minimal` を成功させる。
`.agents` と `.claude` 配下のローカル変更は、次の commit 数へ含めない。

1. as-built の current/target 区分を追加する。
   `docs/spec/00_overview.md`〜`08_known_limits.md` は現行実装を保持し、冒頭に target 正本への参照と実装済み Wave 境界を明記する。
2. 開発者向け規範を更新する。
   `docs/design/00_conventions.md` と `docs/design/development.md` に、本トラック、historical record、ignored skill mirror、worktree、Wave gate の運用を記載する。
3. 監査スクリプトを実行可能にする。
   `check-track-markers.ps1 -DiffAgainst <ref>` を追加し、基準 ref からの追加行だけを検査して既存候補212件と新規漏出を分離する。
   tracked Markdown の相対 link と存在するはずの path を検証する `check-markdown-links.ps1 -Roots <paths...>` を追加する。
   ignored historical redirect の参照先と非循環を検査する `check-skill-redirects.ps1 -AgentsRoot <path> -ClaudeRoot <path>` を追加する。
   両スクリプトへ正常系と違反系の自己テストを用意する。
4. Wave 0 監査結果を記録する。
   差分 guardrail、Markdown link、build、ローカル mirror の結果を `docs/design/development.md` の運用節へ記録する。

各 commit は明示 pathspec、staged diff、build、focused check、commit、topic branch への即 push の順で処理する。

## 4. ローカル mirror

active instruction のローカル正本は `.agents/skills/quiver-implement/SKILL.md` 側とする。
両方の `SKILL.md` は byte-for-byte 同一にする。
historical task は本文を二重管理せず、`.claude` 側から `.agents` 側への相対 redirect にできる。
redirect は参照先の存在と非循環を監査する。
historical task の本文、commit hash、当時の実測値は改変しない。
現行指示と誤認させないための先頭注記だけを両 mirror へ同じ内容で追加できる。

mirror 更新はローカル環境設定であり、`git add -f` で追跡しない。
一致確認に失敗した場合は Wave 0 を合格にしない。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | Markdown link audit、guardrail self-test、`dotnet build Quiver.slnx -v minimal` | 全コマンド成功、0 errors、0 warnings |
| crash test | N/A | `git diff redesign-baseline -- src/Quiver/Transactions src/Quiver/Wal src/Quiver/Storage` | durability production code の変更0件 |
| baseline gate | N/A | `git diff redesign-baseline -- src tests benchmarks` | production、test、benchmark code の性能変更0件。監査 script は除く |
| as-built 更新 | 適用 | `git diff redesign-baseline -- docs/spec docs/design` | current と target の区分、現行実装 map、運用規則が更新済み |

追加条件は次のとおりである。
integration candidate で設定すべき追加条件数は5件である。

- ローカルの両 `SKILL.md` が byte-for-byte 一致し、historical redirect がすべて解決する。
- guardrail の差分モードが本 Wave の新規漏出0件を返す。
- full `-Scan` の既存候補数を記録し、Wave 10 の cleanup baseline とする。
- topic branch の planned commit がすべて origin へ push 済みで、WIP が残っていない。
- 正本 §9 Wave 0 の完了条件を満たす。

gate 結果と topic hash `T` に対するユーザの merge 承認後、process §6 の integration worktree で remote `develop` hash `D` との candidate を作り、本表の全 gate と追加条件を再実行する。
remote の `D` と `T` を push 直前にも照合し、一致時だけ candidate を `develop` へ push する。
ユーザが Wave 0 完了とタグ付与を明示承認した後にだけ、candidate merge commit へ `redesign-wave-0` annotated tag を付ける。
