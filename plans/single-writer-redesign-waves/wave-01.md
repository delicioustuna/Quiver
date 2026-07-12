# Wave 1 着手指示書: Core identity contract の破壊確定

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-10
> 対応する正本のバージョン: `bf8b05b6b073f77848a9fe8aed34eae8e67f3c37`
> ステータス: 承認済み(2026-07-12)

## 1. 着手前チェック

- [ ] `redesign-wave-0` annotated tag が `develop` の Wave 0 merge commit に存在する。
- [ ] `git merge-base --is-ancestor redesign-wave-0 HEAD` が成功し、topic branch が Wave 0 完了点を含む。
- [ ] `redesign-baseline` tag と測定済み baseline 文書が存在する。
- [ ] branch が `redesign/single-writer`、作業場所が専用 worktree である。
- [ ] session 再開確認を実施し、`HEAD` と前セッションの最終 commit hash が一致する。
- [ ] C-1 の redo-only、WAL record 7種、loser 除外が正本内で一致する。
- [ ] C-2 の SSN/lock削除 Wave 4、public transaction cutover Wave 6 が正本とreviewで一致する。
- [ ] C-3 の writer 自己可視性が WriterLease と同じ Wave 4 に割り当てられている。
- [ ] 正本 §2.3、§5.1、§16 と review C-4 の canonical Invalid、strict factory、raw unpack の規則が一致する。
- [ ] 本書の正本 version を実 commit hash へ更新し、ユーザがコミット計画を承認した。

見出しの「対応済み」だけで判断せず、各参照先を `rg` で照合する。
不一致が一つでもあれば doc-only forward-fix を先に行う。

## 2. 読む順序

1. 正本 §9 Wave 1。
2. 正本 §2.3 と §5.1。
3. 正本 §7.1 の identity disposition。
4. 正本 §8.1 の ID equality 変更。
5. review C-1〜C-4、M-1、M-5。
6. `docs/design/00_conventions.md` の public API、ID、test 規約。

## 3. コミット計画

各 planned commit は solution build と focused test が成功する状態にする。
後続 Wave の transaction、property、index definition を先行公開しない。

1. typed ID equality を Generation 込みにする。
   `NodeId`、`RelationshipId`、`HyperedgeId`、`EntityRef` の equality と hash を同じ commit で更新する。
   `EntityRef` の raw `(Kind, Value)` constructor を非公開にし、`From(NodeId/RelationshipId/HyperedgeId)` と `Create(kind, sequence, generation)` だけを public construction にする。
   typed Invalid の `From` は `default(EntityRef)` にだけ写像する。factory は三つの有効 kind、範囲、kind bit 混入を検証し、Property、予約、未知 kind を `ArgumentOutOfRangeException` で拒否する。`UnpackKind` は raw packed bit の抽出だけを担い、生成境界ではない。`Value` を Generation 込み `PackLocal` とし、旧 `Id = Sequence` contract を `Sequence` / `Generation` property へ置換する。
   static helper は `UnpackSequence(long)` / `UnpackGeneration(long)` へ改名し、全 call site を同じ commit で移行する。
   typed ID から EntityRef を作る全 call site は raw constructor を使えず、型別 `From` factory に移行する。
   Pack/PackLocal の bit layout、public enum の underlying value、query/operator の既存 tie-break は変更しない。新しい比較operatorや `IComparable` は追加しない。
   dictionary、frontier、index key、typed ID factory、`Pack` / `PackLocal` codec round-trip、不正kind/value rejection の same-sequence/different-generation test を追加する。三つの typed Invalid、`EntityId.Invalid`、`EntityId.FromPacked(0)` の canonical Invalid、Property(3)/予約/未知 kind（例 5、15）の factory/`EntityId` rejection、malformed internal `EntityId.ToPacked`、raw `UnpackKind` の抽出も検証する。
   public `PropertyId` equality/hash は Wave 1 の対象外とし、Wave 3 の削除まで現行 Sequence equalityを維持する。
2. entity kind を三種類へ限定する。
   `EntityKind.Property`、`EntityId.FromProperty`、`EntityId.AsProperty` と対応 test を削除する。
   `EntityKind.Hyperedge` の数値4を維持し、値3を予約欠番として test で固定する。
   internal `EntityId` 型と transaction call site の `EntityRef` 統合は Wave 4 へ残す。
   public `PropertyId` と property cursor は Wave 3 の active contract として残し、`EntityRef` に変換しない。
3. PublicApi、tests、as-built を更新する。
   identity の旧期待値を置換し、`docs/spec/` と `docs/design/development.md` に Generation 込み equality と後続 Wave の staging を反映する。

各 commit で最低限、次を実行する。

```powershell
dotnet build Quiver.slnx -v minimal
dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build --filter "FullyQualifiedName~EntityIdTests|FullyQualifiedName~IndexGenerationTests|FullyQualifiedName~HyperedgeIdentityTests"
dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build --filter "FullyQualifiedName~IdentityGenerationRegressionTests"
dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build
dotnet test tests/Quiver.Stores.Tests/Quiver.Stores.Tests.csproj --no-build
dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build
dotnet test tests/Quiver.Transactions.Tests/Quiver.Transactions.Tests.csproj --no-build
dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj --no-build
$stagedPaths = @(git diff --cached --name-only --diff-filter=ACMR)
if ($stagedPaths.Count -gt 0) { & scripts/agent-guardrails/check-track-markers.ps1 @stagedPaths }
```

対象外 project の compile error が出た場合は identity call site を同じ planned commit に含める。
壊れた中間 commit を予定どおりとして pushしない。

## 4. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | solution build、Core、Stores、Operators、PublicApi test | 0 errors、0 warnings、全 test成功 |
| crash test | N/A | enum underlying value固定test、`git diff redesign-wave-0 -- src/Quiver/Wal src/Quiver/Storage src/Quiver/Transactions` | Node=1、Relationship=2、Hyperedge=4を維持し、WAL、page flush、transaction/recovery behavior の変更0件 |
| baseline gate | 適用 | baseline と同じ引数の `--basic-perf` | comparable CRUD、visibility、traversal の各 p50 が redesign baseline 比1.20x以内。生出力を保存 |
| as-built 更新 | 適用 | `git diff redesign-wave-0 -- docs/spec docs/design/development.md` | Generation 込み equality、三 EntityKind、後続 Wave staging が記載済み |

追加条件は次のとおりである。
integration candidate で設定すべき追加条件数は6件である。

- `EntityKind` は Node、Relationship、Hyperedge だけであり、予約 raw kind を valid な `EntityRef` または `EntityId` にしない。
- typed ID equality と hash が Generation を含む。
- public `PropertyId` は Wave 3 の現行 active contract として残るが、`EntityRef` へ変換できない。
- transaction/property/index の新旧 public modelを追加していない。
- guardrail 差分監査に新規漏出がない。
- planned commit がすべて origin へ push 済みで、WIP が残っていない。

gate 結果と topic hash `T` に対するユーザの merge 承認後、process §6 の integration worktree で remote `develop` hash `D` との candidate を作り、本表の全 gate と追加条件を再実行する。
remote の `D` と `T` を push 直前にも照合し、一致時だけ candidate を `develop` へ push する。
ユーザが Wave 1 完了とタグ付与を明示承認した後にだけ、candidate merge commit へ `redesign-wave-1` annotated tag を付ける。
