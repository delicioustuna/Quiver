# API 安定性ポリシー (API Stability Policy)

> **いつ読むか**: Quiver を依存ライブラリとして取り込むアプリ/ライブラリの作者が「どのバージョンまで安全にアップグレードできるか」を判断するとき、または Quiver の public API を変更する PR を出す前に「この変更が breaking かどうか」を確認するときに読む。

このドキュメントは Quiver の public API・ファイル/WAL フォーマット・設定について、バージョン間でどこまで互換性を保証するかを定義する。1.0-rc 以降、ここに書かれた約束に反する変更は原則として禁止する。

---

## 1. Semantic Versioning の解釈

Quiver は [Semantic Versioning 2.0.0](https://semver.org/lang/ja/) (`MAJOR.MINOR.PATCH`) に従う。

| 区分 | 上がる条件 | 互換性の約束 |
|---|---|---|
| **MAJOR** | public API の breaking change を含む | 互換性は保証しない。移行ガイドと migration tool を提供する |
| **MINOR** | 後方互換な機能追加 (public API への追加) | 既存コードはそのままコンパイル・動作する |
| **PATCH** | バグ修正のみ (API 追加なし) | 振る舞いの修正以外で挙動を変えない |

- `1.0.0` 未満 (`0.x` / `-rc` / `-preview`) は **安定性の保証対象外**。`0.x` 系では MINOR でも breaking change が入りうる。
- pre-release タグ (`-rc.1`, `-preview.2` 等) が付くバージョンは feedback 収集目的であり、GA (`1.0.0`) までは API が動く可能性がある。

### breaking change の定義

以下は MAJOR を要する breaking change とみなす:

- public 型・メンバーの削除・リネーム
- public メソッドのシグネチャ変更 (引数の増減・型変更・戻り値型変更)
- public enum のメンバー値の変更 (順序入れ替えを含む)
- 例外型・例外契約の変更 (これまで投げなかった箇所で投げる、捕捉していた型が変わる)
- デフォルト挙動の変更でユーザのデータ/結果が変わるもの

以下は breaking change と **みなさない** (MINOR / PATCH で可):

- public 型・メンバーの **追加**
- `internal` / `private` の変更
- XML doc コメント・診断メッセージ文言の変更
- パフォーマンス改善 (結果が同一なら)
- `[Obsolete(error: false)]` の付与 (警告は出るがコンパイルは通る)

---

## 2. public API の定義 (安定性の対象範囲)

安定性を保証する **public API surface** は以下のアセンブリの `public` 型・メンバーに限る:

| アセンブリ | 対象 | 備考 |
|---|---|---|
| `Quiver` | ✅ 対象 | 公開ファサード (`GraphDatabase`, `GraphTransaction`, options 等) |
| `Quiver.Client` | ✅ 対象 | Gremlin ライク API / Match DSL / `[Node]` 等の属性 |
| `Quiver.Core` | ✅ 対象 | 共通 ID 型・例外型・`PropertyValue` 等の基礎型 |

以下は **安定性の対象外**。SemVer に関係なく MINOR/PATCH でも変更しうる:

- すべての `internal` 型・メンバー (`InternalsVisibleTo` 経由で見えるものを含む)
- `Quiver.SourceGen` (Roslyn generator。生成 **コード** の出力安定性は別途 generator 側で管理)
- `Quiver.Storage` / `Quiver.Stores` / `Quiver.Index` / `Quiver.Codec` / `Quiver.Wal` / `Quiver.Transactions` / `Quiver.Operators` — これらは実装詳細レイヤーであり、直接参照は非推奨。`Quiver` ファサード経由で使うこと
- `Quiver.Hosting` / `Quiver.OpenTelemetry` — optional add-on パッケージ。独自に versioning するが、安定化は GA 後に順次
- `[Experimental]` 属性付きのすべての API (§5 参照)

> 直接の実装レイヤー参照を防ぐため、実装アセンブリの public surface は最小化していくが、現時点では参照可能なものも残っている。**ファサード (`Quiver` / `Quiver.Client`) 以外への直接依存は将来予告なく壊れうる** ことを前提にすること。

このリストは [`tests/Quiver.PublicApi.Tests/`](../tests/Quiver.PublicApi.Tests/) の approval test で機械的に固定される (§6)。

---

## 3. 非推奨 (deprecation) と breaking change の事前告知

API を削除する場合、いきなり消さず以下の段階を踏む:

1. **告知 (deprecate)**: 削除予定の 1 つ前の MINOR で `[Obsolete]` を付ける。
   ```csharp
   [Obsolete("Use GraphTransaction.SeekIndex instead. Will be removed in v2.0.", error: false)]
   public IReadOnlyList<NodeId> FindByIndex(string indexName, PropertyValue value) { ... }
   ```
   - メッセージには **代替 API** と **削除予定バージョン** を必ず書く。
   - `error: false` のまま (コンパイルは通る)。
2. **保持期間**: deprecate した API は **最低 1 MINOR は残す**。
   - 例: `1.2` で deprecate → `1.3` でも残す → `2.0` で削除可能。
   - すなわち削除は必ず次の MAJOR まで持ち越す。MINOR/PATCH での削除は禁止。
3. **削除**: 次の MAJOR で削除する。削除リストは CHANGELOG / リリースノートの "Breaking Changes" に列挙する。

`[Obsolete(..., error: true)]` (コンパイルエラー化) は、その API を呼ぶとデータ破損につながる等の安全上の理由がある場合に限り、MINOR でも許容する。この場合もメッセージで理由と代替を示す。

---

## 4. ファイル / WAL フォーマット互換性

オンディスクフォーマット (データファイル・WAL・索引・sidecar) のバージョンは API バージョンとは独立に管理するが、互換性の約束は SemVer に連動する:

| 変更 | 許容バージョン | 振る舞い |
|---|---|---|
| 後方互換な読み取り (旧フォーマットを読める) | MINOR / PATCH | 旧バージョンで作った DB をそのまま開ける |
| **auto-upgrade** (開いた時点で新フォーマットへ書き換え) | MINOR | 初回オープン時に自動移行。移行前に backup を推奨する旨をログに出す |
| **format bump で旧バージョン非互換** (新フォーマットを旧バイナリが読めない) | MAJOR | migration tool 必須。CHANGELOG に移行手順を明記 |

- MINOR の auto-upgrade は **前方互換を壊しうる** (新しいバイナリで開いた DB を古いバイナリで開けなくなる)。ダウングレード前に backup を取ること。
- フォーマットバージョンはファイルヘッダに記録される。非対応バージョンを開こうとした場合は明確な例外メッセージ (期待バージョンと実バージョン) を出して fail-fast する。

---

## 5. experimental API

まだ安定化していない API には `[System.Diagnostics.CodeAnalysis.Experimental("QUIVERxxx")]` 属性を付ける。

```csharp
// SSN ベースの Serializable 分離は評価中 (FT-33)。利用すると QUIVER001 診断が出る。
public enum IsolationLevel : byte
{
    ReadCommitted = 1,
    SnapshotIsolation = 2,
    [Experimental("QUIVER001")]
    Serializable = 3,
}

[Experimental("QUIVER001")]
public sealed class SerializabilityException : GraphDbException { ... }
```

利用側は明示的に opt-in する:

```csharp
#pragma warning disable QUIVER001 // SSN Serializable は実験的と理解した上で使う
using var tx = db.BeginTransaction(IsolationLevel.Serializable);
#pragma warning restore QUIVER001
```

- `[Experimental]` 付きの API は **SemVer の対象外**。MINOR / PATCH でも予告なくシグネチャ変更・削除しうる。
- 利用するには診断 ID (`QUIVER001` 等) を明示的に suppress する必要があり、「これは不安定」と利用側が意識的に opt-in する形になる。
- 安定化したら `[Experimental]` を外す。これは API 追加扱い (MINOR) であり breaking ではない。

診断 ID の割り当て一覧:

| ID | 対象 | 状態 |
|---|---|---|
| `QUIVER001` | SSN ベースの Serializable 分離 (`IsolationLevel.Serializable` / `SerializabilityException`、FT-33) | 評価中 |

---

## 6. 機械的な enforcement

ポリシーを人手のレビューだけに頼らず、CI で機械的に固定する。

### 6.1 public API approval test

[`tests/Quiver.PublicApi.Tests/`](../tests/Quiver.PublicApi.Tests/) で [`PublicApiGenerator`](https://github.com/PublicApiGenerator/PublicApiGenerator) を使い、対象アセンブリ (`Quiver` / `Quiver.Client` / `Quiver.Core`) の public surface をテキスト化し、checked-in の baseline (`PublicApi/*.approved.txt`) と比較する。

- public API に差分が出ると test が **fail** し、`*.received.txt` を出力する。
- 意図した変更なら `*.received.txt` を `*.approved.txt` に上書きコミットする = **明示承認**。これにより「気づかないうちの breaking change」を PR diff として可視化する。
- baseline ファイルの diff はレビューで「この MINOR/MAJOR でこの変更は妥当か」を判断する材料になる。

承認手順:

```bash
# 1. 差分を確認
dotnet test tests/Quiver.PublicApi.Tests
# 2. 意図通りなら received を approved に反映 (PowerShell)
Get-ChildItem tests/Quiver.PublicApi.Tests/PublicApi/*.received.txt | ForEach-Object {
    Move-Item $_ ($_ -replace '\.received\.txt$', '.approved.txt') -Force
}
# 3. approved.txt の diff をコミット
```

### 6.2 NuGet package validation

`Directory.Build.props` で `EnablePackageValidation` を有効化している。NuGet パッケージ生成 (`dotnet pack`) 時に、`PackageValidationBaselineVersion` で指定した直前リリースとの public API 差分を検出し、breaking change があればパック時にエラーにする。

- baseline バージョンは各リリースで前 GA バージョンに更新する。
- package validation は pack 時のみ走り、通常の `dotnet build` には影響しない。

---

## 7. サポートポリシー (GA 後)

- **最新 MAJOR の最新 MINOR** を常にサポートする。
- セキュリティ修正は最新 MAJOR の現行 MINOR に対して PATCH で提供する。
- 1.0 GA 以前 (`0.x` / `-rc`) はベストエフォートであり、上記サポート対象外。

---

## 関連ドキュメント

- [README の Versioning セクション](../README.md#versioning)
- [00_conventions.md](design/00_conventions.md) — 命名・ID 型・例外型の正本
- [docs/api/](api/) — docfx で生成した API リファレンス
