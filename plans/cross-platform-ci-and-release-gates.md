# クロスプラットフォームCIとリリースゲート計画

起票日: 2026-07-02

状態: 設計案

優先度: P1

## 背景

通常CIとNativeAOT smoke testはWindowsだけで実行されている。

共通規約はWindows、Linux、macOSを対応OSとしているため、現状のCIは公開契約を検証しきれていない。

タグリリースはpackだけを実行し、タグが指すcommit自身のbuildとtestを再確認しない。

## 目的

WindowsとLinuxの差分をPR時に検出し、タグが指す正確なcommitを検証してからNuGetへ公開する。

macOSは定期実行で互換性を監視する。

## 非目標

- すべてのOSとCPUアーキテクチャをPRごとに実行しない。
- OS固有機能を新設しない。
- リリースworkflowからmainへの変更を行わない。

## CI階層

| 階層 | 実行契機 | 対象 |
|---|---|---|
| PR必須 | main/developへのPRとpush | Windows x64、Linux x64 |
| 定期 | nightly | macOS x64、chaos、property、fuzz smoke |
| AOT | PRとpush | win-x64、linux-x64 |
| リリース | version tag | restore、build、test、pack、package smoke、push |

## Phase 1: WindowsとLinuxの通常CI

### 対象ファイル

- `.github/workflows/ci.yml`
- OS依存で失敗したテストと実装

matrixを `windows-latest` と `ubuntu-latest` へ拡張する。

一時ファイル、path separator、ファイル削除、排他open、flush、renameの挙動差を修正する。

OS差分を理由にプロジェクト単位でテストを除外しない。

機能として非対応なケースだけを個別にskipし、理由と追跡issueを記載する。

## Phase 2: macOS定期検証

macOSはrunnerコストと実行時間を抑えるため、nightlyまたは手動実行にする。

通常テストに加え、単一ファイルopen、WAL recovery、snapshot、NativeAOT非依存のサンプル起動を検証する。

失敗は通知対象とするが、導入初期はmainのbranch protectionには含めない。

2週間連続で安定した後、release gateへ追加するか判断する。

## Phase 3: NativeAOT matrix

### 対象ファイル

- `.github/workflows/aot.yml`
- `samples/Quiver.Samples.Crud/Quiver.Samples.Crud.csproj`

`win-x64`と`linux-x64`をmatrix化する。

両環境で次を確認する。

- publish成功
- IL2xxxとIL3xxx warningが0件
- native binaryの起動成功
- 基本CRUDと再オープン成功

macOS AOTはSDKとrunnerの安定性を確認した後の追加候補とする。

## Phase 4: タグリリースの自己完結

### 対象ファイル

- `.github/workflows/release.yml`
- 必要に応じて再利用可能workflow `.github/workflows/verify.yml`

release jobは、タグが指すcommitに対して次の順序で実行する。

1. version tagの形式と`Version`の一致を検証する。
2. locked restoreを実行する。
3. Release buildを実行する。
4. 全テストを実行する。
5. public API approval testを確認する。
6. packを実行する。
7. 生成した各packageを一時consumer projectへ追加する。
8. consumer projectをbuildし、Quiverの基本CRUDを実行する。
9. package validationを通す。
10. 検証済みの同一artifactだけをnuget.orgへpushする。

push jobは検証jobのartifactを受け取り、再packしない。

## Phase 5: branch protection

mainの必須checkを次に固定する。

- build and test / Windows
- build and test / Linux
- NativeAOT / Windows
- NativeAOT / Linux
- public API approval

workflowのjob名はbranch protectionから参照されるため、導入後は安易に変更しない。

## 検証

- pathの大文字小文字だけが異なる参照をLinuxで検出できる。
- WindowsとLinuxで全テストが成功する。
- タグを未検証commitへ付けてもrelease job内のtest失敗でpushされない。
- package smoke testがローカルproject referenceを参照せず、生成nupkgだけで成功する。
- push jobが検証前に起動しない。
- `dotnet build Quiver.slnx` を実行する。

## 完了条件

- WindowsとLinuxがPR必須checkになる。
- macOS定期検証がartifactと結果を残す。
- win-x64とlinux-x64のNativeAOT smokeが成功する。
- release workflowがタグcommitを自己完結で検証する。
- NuGetへ送るpackageが検証済みartifactと同一である。

## 導入順

Linux通常CI、Linux AOT、タグリリース自己検証、macOS定期検証の順で導入する。

一度にmatrixを広げて原因の異なる失敗を混在させない。
