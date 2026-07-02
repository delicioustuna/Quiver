# 非同期トランザクションのコンテキスト安全化計画

起票日: 2026-07-02

状態: 設計案

優先度: P0（main 反映前のリリースブロッカー）

> **停止提案 (2026-07-02、library-refinement-tracks 撤回前提版)**: 非同期 tx API の撤回 (REF-16) により
> 本計画の対象 API が消滅するため、「停止 (やらない)」とする提案。本計画が要求する文脈の tx 所有化は
> write/recovery 最深部の恒久改修であり、撤回すれば `[ThreadStatic]` 文脈と「tx = 1 スレッドの同期スコープ」
> 契約の整合が回復して不要になる。**本書が特定した欠陥の記述 (§背景: `BeginTransactionAsync` の
> セマフォ待機後、継続スレッドで `[ThreadStatic]` 文脈が失われ WAL 記録が silent に欠落しうる) は、
> tx 形 async を再導入検討する際の一次資料として削除しない。** 撤回判断が確定した場合、
> 状態を「停止」、優先度を「—」へ更新する (FTS-9 と同じ流儀)。

## 背景

`Transaction` は生成時に `WalPageContext.Begin` と `MvccContext.Begin` を呼び、書き込みコンテキストを `[ThreadStatic]` に設定する。

`GraphDatabase.BeginTransactionAsync` は `EnforceExclusiveWriter` が有効でライタ競合がある場合、`SemaphoreSlim.WaitAsync` の完了後にトランザクションを生成する。

非同期メソッド内でトランザクションを生成したスレッドと、呼び出し側が `await` 後にトランザクションを使用するスレッドが同じである保証はない。

異なるスレッドで書き込みを実行すると `WalPageContext.Current` が `null` になり、`LogPageImage` や before-image の取得が no-op になる可能性がある。

この問題は例外として表面化せず、commit 後のクラッシュリカバリで初めて欠落が判明しうる。

## 目的

トランザクションを逐次使用する限り、処理がスレッドを移動しても WAL、MVCC、rollback のコンテキストが失われない構造へ変更する。

同一トランザクションに対する並行操作は引き続き非対応とし、検出時は fail-fast する。

## 非目標

- 同一トランザクション内で複数の CRUD 操作を並行実行できるようにはしない。
- ページ、ストア、索引のロック戦略は変更しない。
- `CommitAsync` 以外の CRUD を非同期 I/O 化しない。
- 複数ライタを同時進行させる設計には変更しない。

## 設計判断

### 採用案

WAL と MVCC の状態を `Transaction` が所有し、各操作の動的スコープだけ ambient context に設定する。

`[ThreadStatic]` はホットパスから現在のコンテキストを取得する手段として残せるが、トランザクションの所有場所にはしない。

各操作は次の順序で実行する。

1. トランザクション所有のコンテキストを現在のスレッドへ設定する。
2. ストア、索引、ページ操作を同期的に完了する。
3. `finally` で以前のコンテキストを復元する。

### `AsyncLocal` を採用しない理由

`AsyncLocal` は子の実行コンテキストへ値を伝播するが、非同期メソッド内で設定した値を呼び出し元の実行コンテキストへ返す仕組みではない。

同じトランザクションを複数の子 Task へ伝播させるため、誤った並行利用も検出しにくくなる。

### スレッド固定を採用しない理由

`BeginTransactionAsync` の戻り先を特定の OS スレッドへ固定する一般的な方法はない。

専用ライタスレッドだけで実行する API は有効だが、既存の `BeginTransactionAsync` 契約をそのまま安全にするものではない。

## Phase 1: 再現テスト

### 対象ファイル

- `tests/Quiver.Tests/AsyncApiTests.cs`
- `tests/Quiver.Backend.Tests/BinaryGraphStorageBackendCrashContractTests.cs`

### テストケース

1. 先行ライタで排他セマフォを保持する。
2. 別スレッドから `BeginTransactionAsync` を開始して待機状態へ入れる。
3. 先行ライタを終了し、待機側の継続を別スレッドで実行させる。
4. ノード、プロパティ、索引、全文索引を更新して `CommitAsync` する。
5. プロセス kill 相当の再オープンを行い、全変更が復旧することを検証する。

追加で、未commitのトランザクションを別スレッドで `DisposeAsync` し、全変更がrollbackされることを検証する。

テストは単なる `ManagedThreadId` の不一致ではなく、WAL replay 後のデータと索引の整合性を判定する。

## Phase 2: コンテキスト所有権の移動

### 対象ファイル

- `src/Quiver/Transactions/Transaction.cs`
- `src/Quiver/Wal/WalPageContext.cs`
- `src/Quiver/Transactions/TxNodeStore.cs`
- `src/Quiver/Transactions/TxRelationshipStore.cs`
- `src/Quiver/Transactions/TxPropertyStore.cs`
- `src/Quiver/Transactions/TxIndexManager.cs`

### 変更内容

`WalPageContext.Begin` が内部生成している `WriteTransactionContext` を、`Transaction` のフィールドとして明示的に保持する。

`WalPageContext` に、指定コンテキストを一時的に設定して以前の値を復元する scope API を追加する。

```csharp
internal readonly struct WalContextScope : IDisposable
{
    public void Dispose();
}

internal static WalContextScope Activate(WriteTransactionContext context);
```

`TxNodeStore`、`TxRelationshipStore`、`TxPropertyStore`、`TxIndexManager` は、内部ストアを呼ぶ直前に WAL と MVCC の両コンテキストを有効化する。

列挙子やカーソルは生成時の ambient context に依存させず、`MoveNext` ごとに読み取りコンテキストを有効化する。

commit、abort、savepoint、rollback、SSN stamp 更新も同じ scope API を通す。

## Phase 3: 並行利用の検出

`Transaction` に操作中フラグを持たせ、`Interlocked.CompareExchange` で逐次利用を検証する。

別スレッドからの逐次利用は許可する。

同じトランザクションへ同時に二つの操作が入った場合は `TransactionException` を投げる。

カーソルの生存中に別操作を許可するかは、既存カーソル契約を調査して決める。

許可しない場合は、カーソル取得から破棄まで操作中フラグを保持する。

## Phase 4: 公開契約と文書

### 対象ファイル

- `src/Quiver/GraphDatabase.cs`
- `src/Quiver/IGraphTransaction.cs`
- `docs/spec/08_known_limits.md`
- `docs/api/concepts/transaction.md`
- `docs/api/getting-started.md`
- `README.md`
- `tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt`

「トランザクションはスレッドアフィン」という契約を、「トランザクションは逐次利用に限るが、操作境界ではスレッドを移動できる」へ変更する。

CRUD 操作の途中で任意の `await` を挟めない点は維持する。

`BeginTransactionAsync`、`CommitAsync`、`DisposeAsync` の境界は、同期コンテキストの有無にかかわらず安全であると明記する。

## 検証

- 既存のトランザクション、WAL、MVCC、savepoint、SSN テストをすべて通す。
- 強制スレッド移動を伴う新規テストを100回反復して通す。
- binary backend の crash contract を通す。
- in-memory backend の rollback contract を通す。
- 同一トランザクションの並行利用が決定的に例外になることを確認する。
- `dotnet build Quiver.slnx` を実行する。

## 完了条件

- `BeginTransactionAsync` の競合待機後にスレッドが変わっても WAL replay で全変更が復旧する。
- rollback と savepoint がスレッド移動後も変更を残さない。
- WAL 対象のページ書き込みが ambient context 不在で no-op にならない。
- 同一トランザクションの並行利用が silent corruption ではなく例外になる。
- 公開文書と実装のスレッディング契約が一致する。

## ロールバック条件

コンテキストの明示所有によって同期 CRUD の代表ベンチマークが5%以上悪化し、scope の最適化でも回復しない場合は、専用ライタ実行 APIを代替案として再設計する。

この場合も、現行の `BeginTransactionAsync` を安全性未確認のまま main へ反映しない。
