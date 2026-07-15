# Single Writer 再設計の実行手順

> 本書は `plans/single-writer-redesign.md` のプロセス正本である。
> 設計内容は設計正本、Wave 固有の目標状態と検証計画は `plans/single-writer-redesign-waves/` を参照する。
> 物理コピーした別ライブラリは作らない。

## 1. 採用する作業モデル

Single Writer 再設計は、同じ Git リポジトリのトピックブランチ `redesign/single-writer` と専用 worktree で進める。
専用 worktree は別フォルダに置くが、履歴、タグ、index、remote は元リポジトリと共有する。
リポジトリのハードコピー、成果物の後日差し替え、別ライブラリとしての並行開発は行わない。

`develop` は合格済み Wave の高水位とする。
トピックブランチには進行中 Wave だけを置く。
各 Wave は合格後に `develop` へ `--no-ff` でマージする。

## 2. 初回だけ行う bootstrap

bootstrap はトピックブランチを作る前に、メインツリーの `develop` で一度だけ行う。
bootstrap 完了後、Single Writer 再設計の tracked file をメインツリーで直接編集しない。

### 2.1 計画文書の登録

現在地と未追跡ファイルを確認する。

```powershell
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short
```

最初のコミットには次の tracked 文書をすべて含める。

```powershell
git add -- `
  plans/single-writer-redesign.md `
  plans/single-writer-redesign-review.md `
  plans/single-writer-redesign-process.md `
  plans/single-writer-redesign-baseline.md `
  plans/single-writer-redesign-waves/README.md `
  plans/single-writer-redesign-waves/wave-00.md `
  plans/single-writer-redesign-waves/wave-01.md
git diff --cached --stat
git diff --cached
git commit -m "docs(redesign): register single-writer redesign track"
git push origin develop
```

Critical 指摘 C-1 から C-3 の修正は、この登録コミットに含める。
修正前の未追跡版を再現するための空コミットは作らない。

### 2.2 baseline の固定

`plans/single-writer-redesign-baseline.md` に記載したコマンドを同じマシン、runtime、構成、seed で実行する。
同文書へ commit under test、環境、コマンド、未加工の結果、判定を記録してからコミットする。
測定できない新構造の gate は、同文書が参照する既存の `clean-slate` または HYP baseline を使い、基点 commit の実測値として捏造しない。

```powershell
git add -- plans/single-writer-redesign-baseline.md <生成した docs/benchmarks の明示 pathspec>
git diff --cached --stat
git diff --cached
git commit -m "perf(redesign): record pre-redesign baseline"
git push origin develop
git tag -a redesign-baseline -m "single-writer redesign baseline"
git push origin redesign-baseline
```

`redesign-baseline` は baseline 結果を含むコミットへ付ける。
この時点では production code が基点 commit `ee811d1` と同一であることを `git diff ee811d1 -- src tests benchmarks` で確認する。

### 2.3 専用 worktree の作成

既存 worktree を確認し、未保存作業のない不要な worktree だけを整理する。

```powershell
git worktree list
git branch redesign/single-writer develop
git worktree add D:/csharp/Quiver-sw redesign/single-writer
git -C D:/csharp/Quiver-sw push -u origin redesign/single-writer
```

以後の例では専用 worktree を `D:/csharp/Quiver-sw` と表記する。
実際の場所は `git rev-parse --show-toplevel` で取得し、スクリプトや実装へ固定パスを埋め込まない。

### 2.4 ignored skill mirror の扱い

`.agents/` と `.claude/` は `.gitignore` 対象なので、Git コミット計画へ含めない。
両ディレクトリは実行環境のローカル設定であり、tracked な設計正本や進行状態の置き場には使わない。
運用規則を変えたときだけメインツリー側の両 mirror を同時に更新し、byte-for-byte 一致を確認する。

```powershell
git check-ignore -v .agents/skills/quiver-implement/SKILL.md
git check-ignore -v .claude/skills/quiver-implement/SKILL.md
```

mirror の不一致は Wave の tracked commit へ混ぜない。
不一致を解消できない場合は Wave に着手せず、ローカル環境の blocker として報告する。

## 3. Wave の開始

### 3.1 セッション再開時の確認

各セッションの冒頭で、専用 worktree と topic branch を確認する。

```powershell
git status --short
git branch --show-current
git log --oneline -3
git tag -l 'redesign-wave-*' --sort=v:refname
git log develop..redesign/single-writer --oneline
```

branch が `redesign/single-writer` でない場合は編集を開始しない。
前セッションの最終 commit hash と `HEAD` が一致しない場合は、差分の所有者と内容を確認する。

### 3.2 Wave 指示書の承認

未タグの最小 Wave を着手対象とする。
対応する `wave-NN.md` の正本 version を `git log -1 --format=%H -- plans/single-writer-redesign.md` が返す commit hash へ更新し、Wave 完了時の目標状態、影響境界、検証 gate をユーザへ提示する。
ユーザの着手承認後に `ステータス: 承認済み(YYYY-MM-DD)` へ変え、doc-only commit として push する。

```powershell
git add -- plans/single-writer-redesign-waves/wave-NN.md
git diff --cached --stat
git diff --cached
git commit -m "docs(redesign): approve Wave N execution plan"
git push origin redesign/single-writer
```

draft の指示書、未 commit の正本を参照する指示書、未解決 blocker を持つ指示書ではコードを変更しない。

## 4. Wave 内の実装

Wave をファイル別、disposition 別、小タスク別のコミット列へ分解しない。
まず正本と Wave 指示書が定める対象全体を、完了時のあるべき contract へ作業ツリー上で一括変更する。
この置換途中では compile error と test failure を許容し、旧・新 contract を同時に成立させるための shim や部分移行用 API は作らない。

一括変更後に solution build を実行し、compiler error を不足 call site の一覧として補修する。
build 成功後に focused test と Wave 対象 project の test を実行し、失敗から契約漏れと回帰を補修する。
テストは部分移行の挙動ではなく、最終 contract と再発防止を保証する形へ追加・更新する。

コミット境界は作業項目の数ではなく、次の状態だけに置く。

- Wave の目標状態を一括反映し、solution build と focused test が成功した完成状態。
- その後に判明した不足を補修し、同じ検証が成功した状態。
- セッション中断時の退避が必要な WIP 状態。

完成・補修 commit は明示 pathspec で stage し、solution build と対象 test の成功を確認してから作成する。

```powershell
git rev-parse HEAD
git diff --stat
git add -- <確認済みの明示 pathspec>
git diff --cached --stat
git diff --cached
dotnet build Quiver.slnx -v minimal
dotnet test <対象 test project> --no-build
git commit -m "<summary>" -m "<why と正本参照>"
git push origin redesign/single-writer
git rev-parse HEAD
```

内部管理表記の監査は、Wave 0 で導入する差分モードを使う。
Wave 0 完了前は staged path を明示して検査し、既存 debt を理由に新規漏出を見逃さない。
full repository の `-Scan` は既存候補を含むため Wave 10 の cleanup gate とし、通常 commit の合否には使わない。

### 4.1 中断用 WIP commit

セッション中断時に未コミット変更を失う可能性がある場合だけ、`wip:` commit を同じ topic branch へ pushしてよい。
WIP は完成・補修 commit でも Wave 合格点でもない。
次のセッションは WIP を forward-fix し、Wave の目標状態を一括完成させた build/test 成功 commit で supersede する。
公開済み WIP 履歴は rebase で消さず、後続 commit と gate 結果によって未完成状態ではないことを示す。
stash、rebase、force-push、別 worktree へのコピーは使わない。

## 5. 設計不備が見つかった場合

設計正本の矛盾、未決定の durability 規則、過去 Wave の contract 変更が必要な問題を見つけたら、推測で実装を続けない。

1. 未コミットのコード変更があれば、中断用 WIP commit として `redesign/single-writer` へ pushする。破棄はユーザが明示承認した場合だけ許す。
2. 選択肢、推奨決定、根拠、検証方法を decision log の追記案としてチャットへ提示する。この時点では tracked docs を編集しない。
3. 新しい設計選択が public contract、永続形式、durability、Wave 境界を変える場合は、追記案へのユーザ承認を得る。
4. 承認後、設計正本 §16 decision log、レビューの対応状態、該当 Wave 指示書を同じ doc-only commit で更新し、`redesign/single-writer` へ pushする。
5. 新しいレビュー項目を追加する場合は severity ごとの次の連番、発見日、発見者、影響 Wave、対応条件を記録する。正本へ反映するまで「対応済み」にしない。
6. 直前の合格タグと `HEAD` の差分で影響範囲を確認する。
7. 過去タグや公開済み履歴を動かさず、同じ topic branch に forward-fix commit を積む。

レビュー指摘を「対応済み」にするのは、正本内の矛盾がなくなり、検証方法と実装 Wave が確定した後だけである。
見出しのラベルだけを着手判定に使わない。

## 6. Wave 境界の gate

各 Wave 指示書は、次の四条件を `適用` または `N/A` として列挙する。
`N/A` には、その Wave が対象の挙動を変更しないことを示す差分根拠を付ける。

| gate | 合格条件 |
|---|---|
| 機能 test | Wave 固有 test と solution build が成功する |
| crash test | durability、WAL、page flush、recovery を変更する Wave は kill matrix が成功する。変更しない Wave は `N/A` の根拠を記録する |
| baseline gate | 該当 hot path を変更する Wave は比較値を測定する。変更しない Wave は `N/A` の根拠を記録する |
| as-built 更新 | 変更した利用者 contract と実装 map を同じ Wave で更新する |

共通の境界条件は次のとおりである。

- `dotnet build Quiver.slnx -v minimal` が 0 errors かつ 0 warnings。
- Wave 指示書が定めた test、監査、性能測定が成功している。
- branch tip が WIP ではなく、過去の WIP が後続の build/test 成功 commit で supersede されている。
- topic branch の commit がすべて origin へ push 済みである。
- ユーザが gate 結果を確認し、`develop` への merge を明示的に承認している。

merge 承認は、提示した topic commit hash `T` に対して得る。
承認後は remote を fetchし、`origin/develop` を統合基点 `D`、`origin/redesign/single-writer` を `T` として固定する。
local topic、origin topic、承認対象 `T` が一致しない場合は承認を取り直す。

main worktreeを直接merge途中にしない。
同じ Git repository に一時 integration branch/worktree を作り、`D` を第一親、`T` を第二親とする merge candidate `C` を検証する。
これは物理コピーではない。
実行前に次の例の `N`、`<...>`、gate check、追加条件 check を対象 Wave の指示書からすべて具体化し、placeholder が一つでも残る間は実行しない。

```powershell
function Invoke-NativeChecked {
    param([scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "native command failed with exit code $LASTEXITCODE"
    }
}

function Resolve-GitReportedPath {
    param([string]$WorktreeRoot, [string]$ReportedPath)
    if ([IO.Path]::IsPathRooted($ReportedPath)) {
        return [IO.Path]::GetFullPath($ReportedPath)
    }
    return [IO.Path]::GetFullPath((Join-Path $WorktreeRoot $ReportedPath))
}

$ApprovedT = "<ユーザが承認した topic commit hash>"
$MainRoot = (Resolve-Path "<検証対象 repository の main worktree root>").Path
$TopicWorktree = (Resolve-Path "<redesign/single-writer worktree root>").Path
$ResolvedRepositoryRoot = Invoke-NativeChecked { git -C $MainRoot rev-parse --show-toplevel }
if ([IO.Path]::GetFullPath($ResolvedRepositoryRoot) -ne [IO.Path]::GetFullPath($MainRoot)) {
    throw "MainRoot が検証対象 repository の root ではない。"
}
$ResolvedTopicWorktree = Invoke-NativeChecked { git -C $TopicWorktree rev-parse --show-toplevel }
$TopicBranch = Invoke-NativeChecked { git -C $TopicWorktree branch --show-current }
if ([IO.Path]::GetFullPath($ResolvedTopicWorktree) -ne [IO.Path]::GetFullPath($TopicWorktree) -or $TopicBranch -ne "redesign/single-writer") {
    throw "TopicWorktree が redesign/single-writer の worktree root ではない。"
}
$MainCommonDir = Resolve-GitReportedPath $MainRoot (Invoke-NativeChecked { git -C $MainRoot rev-parse --git-common-dir })
$TopicCommonDir = Resolve-GitReportedPath $TopicWorktree (Invoke-NativeChecked { git -C $TopicWorktree rev-parse --git-common-dir })
if ($MainCommonDir -ne $TopicCommonDir) {
    throw "main worktree と topic worktree が同じ repository に属していない。"
}
$MainBranch = Invoke-NativeChecked { git -C $MainRoot branch --show-current }
if ($MainBranch -ne "develop") {
    throw "main worktree が develop ではない。"
}
Invoke-NativeChecked { git -C $MainRoot fetch origin }
$CurrentD = Invoke-NativeChecked { git -C $MainRoot rev-parse origin/develop }
$CurrentT = Invoke-NativeChecked { git -C $MainRoot rev-parse origin/redesign/single-writer }
$LocalT = Invoke-NativeChecked { git -C $TopicWorktree rev-parse HEAD }
if ($CurrentT -ne $ApprovedT -or $LocalT -ne $ApprovedT) {
    throw "topic hash が承認対象と一致しないため、merge 承認を取り直す。"
}
$D = $CurrentD
$T = $ApprovedT
$Attempt = Get-Date -Format yyyyMMdd-HHmmss
$IntegrationBranch = "redesign/integration-wave-N-$Attempt"
$IntegrationPath = "D:/csharp/Quiver-integration-$Attempt"
Invoke-NativeChecked { git -C $MainRoot branch $IntegrationBranch $D }
Invoke-NativeChecked { git -C $MainRoot worktree add $IntegrationPath $IntegrationBranch }
$ResolvedIntegrationRoot = Invoke-NativeChecked { git -C $IntegrationPath rev-parse --show-toplevel }
$ResolvedIntegrationBranch = Invoke-NativeChecked { git -C $IntegrationPath branch --show-current }
$IntegrationCommonDir = Resolve-GitReportedPath $IntegrationPath (Invoke-NativeChecked { git -C $IntegrationPath rev-parse --git-common-dir })
if ([IO.Path]::GetFullPath($ResolvedIntegrationRoot) -ne [IO.Path]::GetFullPath($IntegrationPath) -or $ResolvedIntegrationBranch -ne $IntegrationBranch -or $IntegrationCommonDir -ne $MainCommonDir) {
    throw "integration worktree の root または branch が期待値と一致しない。"
}
Invoke-NativeChecked { git -C $IntegrationPath merge --no-ff --no-commit $T }
Push-Location $IntegrationPath
try {
    Invoke-NativeChecked { dotnet build Quiver.slnx -v minimal }
    $RequiredGateNames = @("機能 test", "crash test", "baseline gate", "as-built 更新")
    $WaveGateChecks = @{
        # 実行前に四つの gate 名をkeyとし、各コマンドと合格条件のassertionをvalueのscriptblockとして列挙する。
        # scriptblock内のnative commandも一件ずつInvoke-NativeCheckedで実行し、中間失敗を後続成功で覆い隠さない。
        # N/Aも、差分根拠が合格条件を満たさなければthrowするscriptblockにする。
    }
    $MissingGateNames = @($RequiredGateNames | Where-Object { -not $WaveGateChecks.ContainsKey($_) })
    if ($WaveGateChecks.Count -ne $RequiredGateNames.Count -or $MissingGateNames.Count -ne 0) {
        throw "四つの Wave gate が完全には設定されていないため candidate を commit しない。"
    }
    foreach ($GateName in $RequiredGateNames) {
        Invoke-NativeChecked $WaveGateChecks[$GateName]
    }
    $ExpectedAdditionalConditionCount = <Wave指示書に記載された追加条件数>
    $AdditionalConditionChecks = @(
        # Wave 指示書の追加条件を記載順に一件ずつassertするscriptblockを列挙する。
        # native commandを使うcheckは、その各commandをInvoke-NativeCheckedで実行する。
    )
    if ($AdditionalConditionChecks.Count -ne $ExpectedAdditionalConditionCount) {
        throw "Wave の追加条件が完全には設定されていないため candidate を commit しない。"
    }
    foreach ($Check in $AdditionalConditionChecks) {
        Invoke-NativeChecked $Check
    }
    Invoke-NativeChecked { git -C $IntegrationPath commit -m "Merge Wave N: <summary>" }
    $C = Invoke-NativeChecked { git -C $IntegrationPath rev-parse HEAD }
    Invoke-NativeChecked { git -C $IntegrationPath merge-base --is-ancestor $T $C }
}
finally {
    Pop-Location
}
```

candidate の全 gate が成功するまで commitしない。
失敗時は integration worktree で `git merge --abort` し、topic branchでforward-fixする。
解決が承認済み Wave 指示書の範囲を種類によらず超える場合は §5 の再承認を先に行う。

`C` を pushする直前に再度 fetchし、`origin/develop == D`、`origin/redesign/single-writer == T` を確認する。
どちらかが進んでいれば `C` を pushせず、新しい `D` / `T` で integration candidate、全 gate、merge 承認を作り直す。

```powershell
Invoke-NativeChecked { git -C $MainRoot fetch origin }
$CurrentD = Invoke-NativeChecked { git -C $MainRoot rev-parse origin/develop }
$CurrentT = Invoke-NativeChecked { git -C $MainRoot rev-parse origin/redesign/single-writer }
$LocalT = Invoke-NativeChecked { git -C $TopicWorktree rev-parse HEAD }
if ($CurrentD -ne $D -or $CurrentT -ne $T -or $LocalT -ne $ApprovedT -or $T -ne $ApprovedT) {
    throw "remote hash が candidate 検証時から変化したため、push せず candidate と承認を作り直す。"
}
Invoke-NativeChecked {
    git -C $MainRoot push --atomic "--force-with-lease=refs/heads/redesign/single-writer:$T" origin `
        "$($C):refs/heads/redesign/single-writer" "$($C):refs/heads/develop"
}
```

この atomic push は topic ref が `T` のままであることを lease で保証し、`T` の子孫である `C` へ進める。
`develop` には force を使わない。
remote `develop` が `D` のままなら fast-forwardし、進んでいれば non-fast-forward で全体が失敗する。
remote が atomic push を提供しない場合も失敗として停止し、非 atomic な二段階 push へ切り替えない。
push が失敗しても main worktree と remote `develop` は変更しない。
失敗時は操作を停止して remote 状態を報告し、新しい `D` からcandidateを作り直す。

push 成功後に main worktreeをremoteへfast-forwardし、merge commit `C` と検証結果をユーザへ提示する。

```powershell
Invoke-NativeChecked { git -C $MainRoot fetch origin }
Invoke-NativeChecked { git -C $MainRoot merge --ff-only origin/develop }
Invoke-NativeChecked { git -C $TopicWorktree merge --ff-only origin/redesign/single-writer }
```

merge 承認はタグ承認を兼ねない。
Wave 完了とタグ付与の明示承認後にだけ、`C` へ annotated tag を付けてpushする。
性能 gate 未達の Wave はマージもタグ付与も行わない。

## 7. ロールバックと再試行

公開済み topic branch は rebase しない。
進行中 Wave が破綻した場合は、最後の合格タグから新しい take ブランチを作り、元の失敗履歴を保持する。

```powershell
git branch redesign/wave-N-take2 redesign-wave-(N-1)
git worktree add D:/csharp/Quiver-sw-take2 redesign/wave-N-take2
```

この操作も物理コピーではない。
同じ Git リポジトリの履歴上で再試行する。
