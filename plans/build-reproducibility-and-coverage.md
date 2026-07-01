# 再現可能ビルドとカバレッジゲート計画

起票日: 2026-07-02

状態: 設計案

優先度: P1

## 背景

リポジトリに `global.json` と `packages.lock.json` がなく、一部の`PackageReference`はワイルドカードversionを使っている。

同じcommitでもrestore日時によってSDKや依存packageが変わり、ビルド結果とベンチマーク条件が一致しない可能性がある。

coverletは一部のテストprojectにしか導入されておらず、CIはカバレッジを収集していない。

共通規約はコア層の分岐カバレッジ80%以上を目標としているが、現状値と継続的な判定手段がない。

## 目的

同一commitと同一RIDから同じ依存関係でbuildできる状態を作る。

コアエンジンのline coverageとbranch coverageをCIで計測し、低下を検出する。

## 非目標

- カバレッジ率だけを目的とした価値の低いテストを追加しない。
- 生成物のbit単位一致を、OSを跨いで保証しない。
- StudioなどのUIコードへコアと同じcoverage thresholdを適用しない。

## Phase 1: SDK固定

### 新規ファイル

- `global.json`

使用中の.NET 10 SDK feature bandを固定する。

初期値は検証済みSDKを指定し、`rollForward`は`latestPatch`とする。

```json
{
  "sdk": {
    "version": "10.0.301",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

SDK更新はDependabotまたは専用PRで行い、通常の機能変更と分離する。

## Phase 2: NuGet versionの集中管理

### 新規ファイル

- `Directory.Packages.props`

Central Package Managementを有効にし、全package versionを正確な値で記録する。

各csprojから`Version`属性を除去し、`Directory.Packages.props`をversionの正本にする。

`12.*`、`10.*`、`1.*`などのワイルドカードは残さない。

Quiverの公開packageが利用者へ露出させる依存関係は、pack後のnuspecでも確認する。

## Phase 3: locked restore

各projectの`packages.lock.json`を生成してcommitする。

CIとreleaseでは次を使用する。

```text
dotnet restore Quiver.slnx --locked-mode
```

開発者が依存関係を更新するときだけ`--force-evaluate`を使う。

lock fileの差分を伴わないpackage version変更はCIで失敗させる。

## Phase 4: カバレッジ基盤

### 対象ファイル

- `tests/Directory.Build.props`（新規）
- 各test project
- `.github/workflows/ci.yml`
- `.config/dotnet-tools.json`（必要な場合）

`tests/Directory.Build.props`はrootの`Directory.Build.props`をimportし、全test projectへ同じcollector設定を適用する。

収集形式はCoberturaへ統一する。

生成コード、Source Generator出力、benchmark、sample、toolはコアcoverageの分母から除外する。

CIは全test projectの結果をmergeし、HTMLとCobertura XMLをartifactとして保存する。

## Phase 5: threshold導入

最初のPRで現状値を測定し、次の順でgateを導入する。

1. coverage収集失敗をCI失敗にする。
2. 現状値からの低下を許可しないratchetを入れる。
3. 未到達領域へ正当性テストを追加する。
4. `src/Quiver`のbranch coverageを80%以上にする。
5. 80%到達後は固定thresholdとして維持する。

変更行coverageも計測し、新規または変更したコアコードには80%以上を要求する。

自動生成コードと単純な例外メッセージ分岐は、理由を記録した上で除外できる。

## Phase 6: compilerとanalyzerのゲート

CIのRelease buildではwarningをerrorとして扱う。

```text
dotnet build Quiver.slnx -c Release --no-restore -warnaserror
```

既存の意図したwarning抑制はwarning codeと理由を`Directory.Build.props`へ記載する。

新しい包括的な`NoWarn`追加は認めず、対象projectまたは対象行へ範囲を限定する。

## 再現性の確認

同じcommitをクリーンな二つの作業ディレクトリでrestore、build、packする。

次を比較する。

- lock fileに差分がない。
- nupkg内のファイル一覧とnuspec依存関係が一致する。
- deterministic build対象のassembly hashが同一OS上で一致する。
- SourceLink情報が同じcommitを指す。

timestampや署名など、意図して変動するメタデータは比較対象から除外する。

## 検証

- 未承認のpackage更新が`--locked-mode`で失敗する。
- 指定SDKがない環境で、許可したroll-forward範囲外へ移動しない。
- 全test projectからcoverageが収集される。
- coverageがthreshold未満ならCIが失敗する。
- compiler warningを追加したfixture branchでCIが失敗する。
- `dotnet build Quiver.slnx` を実行する。

## 完了条件

- SDKとNuGet依存versionの正本が一か所ずつ存在する。
- CIとreleaseがlocked restoreを使う。
- コアのline coverageとbranch coverageがPRごとに表示される。
- branch coverageが80%以上になり、低下をCIが拒否する。
- Release buildのwarningが0件で維持される。

## 依存関係

本計画のSDKと依存version固定を、比較ベンチマークの確定値取得より先に実施する。

locked restoreをreleaseへ適用する作業は `plans/cross-platform-ci-and-release-gates.md` と同じPRに混在させず、先に本計画を完了させる。
