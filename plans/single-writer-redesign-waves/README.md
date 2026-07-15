# Wave 指示書の運用

> 効力宣言: 本ディレクトリの指示書と `plans/single-writer-redesign.md`(設計正本)が食い違う場合は正本を優先し、食い違いをユーザに報告する。

## 1. 指示書とは何か / 何でないか

各 `wave-NN.md` は、`plans/single-writer-redesign.md` §9 の該当 Wave 節を**複製したもの**ではない。
`.agents/skills/quiver-implement/SKILL.md` §2「Wave 実行プロトコル」手順 3(Wave 完了時のあるべき姿、影響境界、検証 gate をユーザに提示し、承認を得る)の**導出結果を永続化する場所**である。

正本 §9 の「対象/削除変更追加/テスト/Build/完了条件」は転記せず、節番号への参照だけを書く。転記すると正本改訂のたびに二重管理・drift が発生するため。

このディレクトリは `plans/` 配下、つまり git 管理下に置く。`.agents/` `.claude/` 配下(skill 本体)は丸ごと `.gitignore` 対象で push バックアップも専用 worktree への複製もされないため、Wave ごとに増える可変情報の置き場としては使えない。

## 2. ライフサイクル

1. 前 Wave が合格する(`redesign-wave-(N-1)` タグが push される)。Wave 0 は `redesign-baseline` タグを前提とする。
2. 次の Wave N の指示書 `wave-NN.md` を、本 README のテンプレートに従って新規作成する(既存 draft があれば正本との整合を確認して更新)。
3. 目標状態と検証計画(§3 節)をユーザに提示し、承認を得る。
4. 対応する正本の version を実 commit hash に更新する。承認された指示書を `ステータス: 承認済み(日付)` にして、**doc のみの単独コミット**として topic branch へ push する(コード変更と混ぜない)。
5. 実装に入る。以後、指示書は Wave N の完了までは基本的に不変(正本が forward-fix された場合のみ改訂し、改訂も doc 単独コミット)。

Wave 2 以降の指示書は、対応する Wave の直前になるまで作成しない。事前に書き切ると、レビュー指摘(特に C-1/C-2/C-3、M-1)の反映によって下流 Wave の対象・スコープが変わったときに drift するため。

## 3. Codex 向け共通注意

- Codex は `.claude/` 配下を明示されない限り自発的に読まない。委譲プロンプトには `.agents/skills/quiver-implement/SKILL.md` §4 の起動テンプレートをそのまま使う。
- `SKILL.md` はメインツリー(`develop` 固定, 例 `D:\csharp\Quiver`)側にしかない(git 管理外のため専用 worktree には複製されない)。read-only で参照する。
- 本ディレクトリの `wave-NN.md` は git 管理下なので、専用 worktree(例 `D:/csharp/Quiver-sw`)側に通常どおり存在する。Codex への読み込み指示はこちらを worktree 側パスで渡してよい。
- `csharp-xml-comment` skill や Claude Code の PostToolUse フックは Codex 環境では動かない前提で、SKILL.md §3.3 の手動 guardrail スクリプトをコミット前に実行する。

## 4. テンプレート

新しい `wave-NN.md` を作るときは次の構成に従う(`NN` はゼロ埋め 2 桁)。

```markdown
# Wave N 着手指示書: <正本 §9 の Wave タイトル>

> 効力宣言: 本書と設計正本が食い違う場合は正本を優先し、食い違いをユーザに報告する。
> 作成日: YYYY-MM-DD
> 対応する正本のバージョン: <commit hash、または「正本が未 commit のため作成時点の内容に基づく」>
> ステータス: draft | 承認済み(YYYY-MM-DD)

## 1. 着手前チェック

- 前提タグ `redesign-wave-(N-1)` の存在確認(`git tag -l 'redesign-wave-*'`)。Wave 0 は `redesign-baseline` を確認する。
- worktree とセッション再開確認(`plans/single-writer-redesign-process.md` §3.1)を実施済みか。
- 本 Wave に効くレビュー指摘(`plans/single-writer-redesign-review.md`)とその対応状況。
- 前 Wave 成果物への依存が実在するか(無ければ実装せず停止して報告)。

## 2. 読む順序(正本ポインタのみ、転記しない)

1. 正本 §9 Wave N 節
2. §7.x の該当 disposition 表
3. 関連する設計節(該当する §番号を列挙)
4. レビューの該当指摘

## 3. 目標状態と検証計画(承認対象の本体)

- あるべき姿: <Wave 完了時に成立する contract と削除済みであるべき旧構造>
- 一括変更範囲: <影響する module / public boundary / durable boundary>
- 補修方法: solution build と focused test の失敗を、どの順で不足一覧へ変換するか
- 契約保証: <追加・更新する回帰 test と as-built>

設計内容を再定義せず、設計判断は正本 §7 disposition 表を正とする。
ファイル別、小タスク別、disposition 別のコミット列は書かない。
最初に対象全体をあるべき姿へ一括変更し、その後に build/test で不足を補修する。

## 4. 本 Wave 固有の落とし穴

- テストスコープの限定、依存・順序の罠、該当する性能ゲート(§10.4)など。

## 5. 完了確認

- 正本 §9 の完了条件。
- §13 の四条件を次の表で `適用` または `N/A` に分類する。`N/A` には対象挙動を変更しないことを示す差分根拠を書く。

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 |  |  |
| crash test |  |  |  |
| baseline gate |  |  |  |
| as-built 更新 | 適用 |  |  |

- 完成・補修 commit は build と focused test が成功した状態だけで作る。中断用 WIP は後続の成功 commit で supersede する。
- タグ付与は SKILL.md §2 手順 5 に従い、ユーザの明示承認後にのみ行う。
```
