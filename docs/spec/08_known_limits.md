# 既知の限界

> as-built 仕様（QUIVER-SW family version 2、2026-07-19）

本書はエンジンの現時点での既知の限界と、安全に利用するための条件を記す。

## 並行性・スレッディング・安全な利用 {#concurrency}

エンジンは **書き込みトランザクションを 1 度に 1 つだけ、任意数の並行リーダと並走させて** 動作する。
以下のルールがサポートされるコントラクトであり、これに従えばデータベースは正しく、かつクラッシュ安全に保たれる。

### プロセスとスレッディングのルール {#threading}

- **データベースごとに 1 プロセス。** `*.quiver` ファイルは排他 OS ファイルロック (`FileShare.None`) で
  開かれる。2 つ目のプロセスはこれを開けない。マルチプロセスやネットワークアクセスは存在しない —
  それが必要なら自前のサービスを前段に置くこと。
- **データベースごとに 1 つの `QuiverDatabase` をスレッド間で共有する。** インスタンスはスレッドセーフ。
  一度開いてプロセスのライフタイムを通じて再利用すること。同一プロセス内で同じファイルを 2 度開かないこと。
- **トランザクションハンドルは同時使用不可である。** write 文脈と MVCC 文脈は非同期フローに保持されるため、
  `await` の継続や、重ならない `Task.Run` 越しの利用で WAL ロギングが暗黙に欠落することはない。
  ただし同じ `IReadTransaction` または `IWriteTransaction` ハンドルを複数スレッドから同時に使うことは未サポートであり、
  検出された場合は `TransactionException` をスローする。トランザクションから取得したカーソルや列挙子も、
  トランザクション有効期間内に 1 つの操作フローで消費すること。

### ライタは 1 つ、リーダは並行 {#one-writer}

- **書き込みトランザクションは 1 度に 1 つだけ進行できる。** エンジンは内部 writer gate により
  `BeginWriteTransaction()` を直列化する。2 本目の書き込みトランザクションは
  `WriterContentionMode.Wait` なら `WriterWaitTimeout` まで待ち、期限を超えると `WriterBusyException` をスローする。
  `WriterContentionMode.FailFast` は待機せず同じ例外をスローする。
  scalar index definition と全文 manifest の publish も同じ書き込み排他に従う。
  immutable全文 artifact の構築は read snapshot で行うため、構築中も通常 writer は進行できる。
- **リーダはブロックせず、ブロックもされない。** `BeginReadTransaction()` は開始時の一貫した
  コミット済みスナップショットを取得し（snapshot isolation）、デフォルトではロックを取得しない。
  任意数のリーダがそれぞれのスレッド上で、単一のライタと並行して並列に動作する。リーダは自身の開始後に
  コミットされた書き込みを観測しない — より新しい状態を見るには新しいリーダを開くこと。

### トランザクションは短く保つ {#short-transactions}

チェックポイントと `Vacuum()` は writer lease を取得するが、active reader の終了を待たない。
最古 reader の snapshot が visibility horizon と WAL 切り詰め可能位置を固定する。
vacuum はその horizon より前だけを回収するため、reader が存在しても安全な範囲では前進する。
ただし長時間 reader が古い horizon を保持すると、property、payload、manifest、artifact と WAL の回収可能範囲が広がらない。
トランザクションを開き、作業を行い、速やかに commit または dispose すること。
ユーザの思考時間、UI イベント、ネットワーク呼び出しをまたいでトランザクションを開いたままにしないこと。

### コミットと永続性 {#commit-durability}

`Commit()` は WAL が fsync された後にのみ返る。返った後は、データはプロセスの kill や電源喪失を生き延びる
（再オープン時にリカバリが再生する — [02_wal_recovery.md](02_wal_recovery.md) を参照）。`Commit()` なしで
dispose されたトランザクション（例外が `using` スコープを巻き戻す場合も含む）はロールバックされる。
部分適用されたトランザクションが可視になることは決してない。

この永続性保証は binary backend に適用される。インメモリバックエンド
（`QuiverDatabase.CreateInMemory()` / `":memory:"`）の commit は同一インスタンス内の可視性と
rollback 原子性だけを保証し、プロセス終了やデータベースの破棄・再オープンを跨いでデータを保持しない。
スナップショットと vacuum もサポート対象外である。

### 競合時のリトライ {#retry}

複数スレッドから書き込みを駆動する場合、writer lease の待機上限または fail-fast 方針は
`WriterBusyException` で書き込み開始を拒否する。
これは *一時的* である: アボートされたトランザクションは
永続的な変更を何も行っていないため、小さな有界バックオフを挟んで **トランザクション全体** を
リトライすること（部分的にではなく）:

```csharp
T WithRetry<T>(Func<T> runTxn, int maxAttempts = 5)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return runTxn(); }
        catch (WriterBusyException) when (attempt < maxAttempts)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(2 * attempt)); // back off, retry whole txn
        }
    }
}
```

### 書き込みの直列化 {#write-serialization}

書き込みゲートはエンジン内に組み込まれているため、アプリケーション側で `BeginWriteTransaction()` を
さらに `SemaphoreSlim` で囲む必要はない。高頻度の書き込みを扱うアプリケーションでは、専用の
ライタキューでジョブを集約すると、自然なバッチングとリトライ制御を実装しやすい。

読み取りにゲートは不要: `BeginReadTransaction()` を任意のスレッドで開き、互いに、そしてライタと
並行して実行できる。

複数の書き込みトランザクションを同時に進行させる機能はサポートしない。

## 高密度三角形結合の範囲と上限 {#cyclic-triangle-join-limits}

transient column経路が扱うのは、三本の有向二項relationが
`R(a,b) ∧ S(b,c) ∧ T(c,a)`を構成する固定形だけである。
任意個のrelation、4-clique、Nexus relation、一般のFree Join、hypertree分解、
join幅証明書は扱わない。公開Match grammarも複数relationの合成構文を持たない。

pair重複があるとprojection後のbag multiplicityをintersectionだけでは保存できないため、
その場合はmaterializing fallbackを使う。
既定では入力三relationの合計を1,000,000行、fallbackの中間結果を4,000,000行に制限する。
結果数、join work、経過時間も独立に制限できる。
join workはintersection comparisonとmaterialized intermediate rowを数え、route分析、列構築、sortは数えない。
後者は入力行上限、経過時間、cancellationで制限する。

時間とcancellationはrelation走査、列構築、join loopで協調的に観測する。
配列と列ごとのsortは途中で割り込まないため、停止遅延は一つのsort phaseまたは次の協調点まで伸びうる。
cancellationは`OperationCanceledException`を送出する。
作業量または時間上限では決定的prefixを返すが、fallbackが中間結果の構築中に停止した場合は
まだ出力順を確定できないため空のpartial resultを返す。

## Nexusの契約と限界 {#nexus-limits}

第一級Nexusは次の契約で動作する。いずれも v1 の設計判断であり、緩和は実需が出てから検討する。

### メンバー集合は作成時確定 {#nexus-immutable-members}

Nexusのメンバー集合（ロールとVertexの組）は `CreateNexus` の時点で確定し、
以後変更できない。変更は削除 + 再作成で表現する。プロパティは作成後も変更できる。

**設計根拠**: メンバー集合が不変であることで、「型 + ロール付きメンバー集合」による同一性が
well-defined になり、incidence チェーンの構築を作成時の一括処理にでき、
逆方向リンクの常時維持も不要になる。ストレージとロック設計の大部分がこの前提に立つ。

### メンバーはVertexのみ、アリティ 2 以上 {#nexus-vertex-members}

v1 のメンバーは `VertexId` に限る。EdgeやNexus自身をメンバーにする
高階の入れ子（RDF-star 的な拡張）はサポートしない。アリティ（メンバー数）は 2 以上を要求し、
同じロールとVertexの組は 1 つのNexus内で重複できない。

### メンバーの列挙順序は保証しない {#nexus-member-order}

`GetMembers` と DSL の `Members` は、作成時に渡したメンバーの順序を保存しない。
順序が意味を持つ場合は、ロール名（`first` / `second` など）またはメンバーVertexのプロパティで表現する。

### Edgeとの相互変換 API は対象外 {#nexus-no-conversion}

Nexus走査からEdgeを生成する API、およびその逆
（reified パターンからの移行を含む）は v1 では提供しない。必要な場合はアプリケーション側で
走査結果から明示的に作成する。このとき導出したEdgeは元のNexusと系譜を同期しない
（元の削除は導出先に波及しない）。冪等な更新は再実行と `MergeEdge` で行う。

### Vertex削除の高次数カスケード {#nexus-delete-cascade}

`DeleteVertex` は、そのVertexが属すすべてのライブNexusを 1 度ずつカスケード削除する。
このコストは所属Nexus数に比例する。実測（arity 4、AMD Ryzen 7 5700X）では
1 Vertexが 10^3 / 10^4 個のNexusに属す状態の削除がトランザクション時間
7.84 ms / 47.69 ms、WAL 61 KB / 608 KB で完走し、デッドロックや整合性違反は生じない。
RAG の Chunk や頻出エンティティのような高次数Vertexを大量に削除するバッチでは、
トランザクションを分割して WAL 切り詰めの余地を与えること
（[§short-transactions](#short-transactions) 参照）。

### 整合性チェックは書き込み停止時を想定 {#nexus-consistency-check}

`CheckConsistency` は複数ストアをロックなしで走査するため、同時更新中は一時的な不整合を
観測しうる。診断は書き込みを止めた状態で実行すること。

### 有向Nexusの最短導出が扱う範囲 {#directed-nexus-derivation-limits}

`FindShortestDerivation` が厳密に扱うのは、全tailを必要とする導出木と、0以上の有限Nexusコストを
加法または最大値で単調に集約する場合である。一般の始点終点間最短hyperpathや、共有Nexusを
一度だけ数える最小部分グラフは扱わない。後者の意味が必要な場合、返却された木の同じNexusを
重複除去しても最適性は保証されない。
有限の入力コストでも加法集約が `double` の有限範囲を超えた場合は `OverflowException` を送出する。

導出木は共有された依存を出現ごとに展開するため、基礎となるNexus網より大きくなり得る。
`ShortestDerivationOptions.MaxTreeNodes` を入力規模に応じて設定すること。上限到達時はコストが
確定していても木を返さず、`MaxTreeNodesReached` を報告する。到達列挙も `MaxResults` と
`MaxNexuses`、最短導出探索も `MaxNexuses` で明示的に制限できる。

### 最小ヒッティング集合の計算量 {#minimum-hitting-set-limits}

最小ヒッティング集合はNP困難であり、厳密解の証明時間は入力規模だけでは予測できない。
`MinimumHittingSetOptions` の `MaxNodes`、`TimeLimit`、`CancellationToken` を使って作業量を制限する。
既定値は100万nodeと1秒であり、上限終了時は実行可能解と証明済み上下界のgapを確認できる。
入力サイズによるgreedy解への自動切り替えは行わない。

これらの上限は協調的である。実行可能解が全入力を被覆することを保証するため、入力全体の正規化と
fallback解構築を最初に完了し、その後と縮約・探索中に上限を観測する。`FindMinimumHittingSet` は
snapshot入力をsolver開始前にmaterializeするため、このadapter走査もsolverのtime budgetには含めない。

`FindMinimumHittingSet` は対象型の可視Nexusと指定ロールのmemberを一時集合へmaterializeする。
作業領域は集合のmember総数、候補数、探索状態に比例する。対象型のNexusに指定ロールのmemberが
一つもなければ、そのNexusに対応する集合は空となり、全体を実行不可能と判定する。

## 0 次パーシステンスの計算量と近似 {#persistence-h0-limits}

完全グラフ濾過は点数を N、次元数を d とすると距離評価が O(N²d)、辺の整列が O(N² log N)、作業領域が O(N²) である。
疎 k-NN 濾過も現行の exact primary batch scan では距離評価が O(N²d) だが、整列する辺と Union-Find の入力は最大 O(Nk) になる。
`MaxPoints`、`MaxEdges`、`MaxDistanceEvaluations` を入力規模と利用可能メモリに合わせて必ず設定すること。

時間とキャンセルは streaming query の候補 64 件ごと、vector 読み出し、辺構築、Union-Find の協調点で観測する。
個々の query cursor 移動、`KnnSearchBatch` の単一 primary scan、配列 sort の途中は割り込まないため、停止遅延は一つの有界 phase または協調点間の実行時間まで伸びうる。
制限終了時に部分 barcode を exact として返すことはない。

疎 k-NN barcode の正確性はデータ形状と k に依存し、一般の誤差保証を持たない。
全点対経路を完走した場合だけ、要求された scale までの完全 Vietoris-Rips H0 barcode として厳密である。
`EstimateClusters` の最大 gap 規則も別の heuristic であり、barcode や既知クラスタ数との一致を保証しない。

## BM25 コーパス統計はスナップショットベース {#bm25-stats}

BM25 スコアリングはコーパス文書数 `N`、平均文書長 `avgdl`、term ごとの `df` を同じ visible segment snapshot から取得する。
同じ manifest generation の postings、norms、stats は読み取り専用 snapshot cache として再利用する。
`GraphStats` を明示的に与えた場合は取得時点の近似統計を使う。

WAND top-k 枝刈りは term ごとの保守的な上限 `idf * (K1 + 1)` を用いる。
上限または stats の鮮度から正しさを証明できない場合は strict scan へ fallback する。

**設計根拠**: スコアリングのたびに segment 全体を再materializeするとクエリレイテンシが文書数に比例して悪化する。
immutable manifest 単位の cache は old/new reader の統計を混ぜずに再計算を避ける。

**緩和策**: `GraphStats` を検索へ与える場合は、書き込みバッチ後に取り直して WAND の snapshot 統計を更新する。

vacuum の segment GC は reader horizon を越えた旧 manifest と、どの committed manifest からも
参照されない全文 artifact を物理回収する。

## Derived全文 segment の再構築 {#fulltext-segment-rebuild}

正常 reopen は persisted manifest から `*.quiver-ftseg/` 内の checksum 一致 artifact file を開き、primary scan を行わない。
referenced body の欠損または checksum 不一致で `RebuildRequired` になった場合だけ、全文検索は transaction-local primary property scan へ fallback する。
fallback artifact は global manifest として公開せず、background worker が source generation を再検証してから publish する。
この破損時 fallback は結果集合を保つが、publish 完了までは検索レイテンシが corpus size に比例する。

## Derived vector segment の再構築 {#vector-segment-rebuild}

reopen 直後、merge 中、または derived state が不足する場合、KNN は primary vector property を exact scan する。
この fallback は結果集合を保つが、immutable HNSW segment の publish が完了するまで検索レイテンシが corpus size に比例する。

通常の vector property update は commit-local flat delta segment を公開するため、共有 HNSW グラフを in-place で再リンクしない。
merge worker は writer lease の外で新しい HNSW artifact を構築し、source generation が一致する場合だけ versioned manifest を公開する。
index の drop と再作成は primary vector property を削除しない。

## HNSW 既定パラメタの品質とコスト {#hnsw-default-recall}

既定の構築パラメタは M=32 / Mmax0=64 / efConstruction=400。dim=384 / N=10,000 /
cosine の決定的コーパスで true recall@10 **0.950**、30% 削除後 **0.985** を満たす。

旧既定 M=16 / Mmax0=32 / efConstruction=200 は recall 0.825、構築 5.95 秒、
検索 1.02 ms。新既定は構築 10.00 秒 (+68%)、検索 1.43 ms (+41%) だが、
ローカル RAG の既定品質目標 0.95 を満たすため、このコストを採用した。
より軽い構築を優先する利用者は `VectorIndexDefinition` の HNSW parameter で旧値相当を明示できる。

**設計根拠**: efSearch=200 まで広げても旧構築グラフは 0.825 止まりで、検索時パラメタだけでは
0.95 に届かない。payload cache 導入後は新既定の 1.51 ms も導入前の旧既定 2.21 ms より速い。
`Quiver.Benchmarks.RecallCheck`は、既定構成のtrue recall@10が0.95以上であることを検証する。

## 組み込みグラフ注釈の有限実行範囲 {#graph-annotation-limits}

組み込みグラフ注釈はtransaction snapshotの全Vertexと対象Edgeを呼び出しごとにmaterializeする。
入力走査、policy検証、評価、結果materializationは時間とキャンセルのbudget内に含まれる。
Vertex全体とVertexごとのEdge一覧のsort自体は中断不能であり、停止観測は各sortの直後になる。
それ以外の入力走査、index構築、DAG検証、relaxation、結果計数では要素ごとに停止を観測する。
したがって停止遅延は一回のsort時間まで伸びうる。

Boolean BFS、非負tropical label-setting、DAG tropical、DAG Viterbiは入力と結果の上限内で厳密である。
負辺を含むtropicalの`BoundedWorklist`は有限graphだけを対象とし、queueが空になった場合だけ厳密である。
負閉路では有限解が存在せず、`MaxRelaxations`で停止して空結果を返す。
経路多重度はDAGなら有限path数へ収束するが、cycle上のwalk数は増え続けうるため、
`MaxAnnotationUpdates`または数値表現範囲で停止して空結果を返す。

任意ユーザーsemiring、capability宣言によるpolicy自動選択、provenance多項式、別solverへの自動接続は提供しない。
既存traversal、BFS、weighted shortest-pathのhot pathには注釈hookを置いていないため、
注釈を使わない呼び出しはこのmaterializationとpolicy dispatchを実行しない。
組み込み評価は別経路でsnapshot全体を読むため、単一終点だけを求める既存最短路より作業量が多い場合がある。

## 形式概念数と継続範囲 {#formal-concept-limits}

形式概念数は最悪で属性数に対して指数的に増える。
`MinExtent`や`MinIntent`だけでは停止性を保証しないため、結果数、closure評価回数、入力規模、時間、
キャンセルの各上限を設定し、終了理由を確認する必要がある。
時間とキャンセルはNexus/member走査、bitmap構築、closure、結果materializationで協調的に観測する。
対象・属性のsort、index dictionary構築、SHA-256 fingerprintの個々の処理は中断不能である。
それ以外の走査とbitmap変換は定期的に観測し、最悪の観測遅延は一つの中断不能phaseまたは一closure評価まで生じうる。

continuationはプロセス内の同じtransaction objectと同じsnapshot/contextだけに有効である。
文字列tokenとして永続化する形式ではなく、database reopen、別read transaction、別write transactionへ移せない。
write transactionでページ間に対象Nexusやmemberを変更すると、再開前のcontext照合で拒否する。
入力contextのmaterialization完了前にtime/cancellationで停止した初回呼び出しは、安全に束縛できるcontextが無いためcontinuationを返さない。
既存continuationからの再開が入力走査中に停止した場合は、元のpositionをそのまま返し、次回の再開時にcontextを再検証する。
Close-by-Oneは明示stackで実行するがstateless continuationを返さない。
作業量上限、または未列挙の一致概念が残る状態で結果数上限に達した列挙はterminalであり、continuationを返さない。
結果数が全一致概念数とちょうど等しい場合は`Completed`であり、truncationとは報告しない。

## 自動マイグレーションなし {#no-migration}

QUIVER-SW family version 2 ではないデータベースは `StorageFormatMismatchException` で拒否する。
旧 WAL は `WalFormatMismatchException` で拒否する。
自動 migration と互換 reader は存在しないため、データベースは source data または logical export から作り直す。

**設計根拠**: オンディスクフォーマットのマイグレーションは、全ページの読み書きとバリデーションが
必要であり、データ破損リスクが高い。Quiver の主要ユースケース（ローカル RAG）ではソースデータ
（元文書）が常に利用可能であるため、再構築コストはマイグレーションの複雑さと信頼性リスクに
見合わない。SQLite も同様に手動 dump + restore を推奨するアプローチを取っている。

**緩和策**: フォーマット変更を含むアップグレード時は以下の手順を踏む:
1. ソースデータから新フォーマットで DB を再構築する（BulkLoader を活用）
2. 旧 DB ファイルは rollback 用にバックアップとして保持する

スキーマレベルの変更（ラベル名変更・プロパティキー追加等）は `IMigration` API
でサポートされる。ここで言う「自動マイグレーションなし」は
オンディスクの物理フォーマット変更のみを指す。
適用履歴は database 内の transactional catalog に格納し、migration の mutation と同じ commit で追加する。
rollback または crash で commit record が残らない migration は履歴にも現れない。
reopen 後の `GetMigrationHistory()` はこの catalog を読み、外部履歴ファイルへ依存しない。

**将来方針**: 1.x 内では QUIVER-SW family version を固定する。
MAJOR バージョンアップ時には migration tool の提供を検討する
（[api-stability.md §4](../api-stability.md#4-ファイル--wal-フォーマット互換性) 参照）。

## In-Process のみ {#in-process}

Quiver はアプリケーションプロセス内で動作する。サーバモード・ネットワークプロトコル・プロセス間
アクセスは存在しない。`*.quiver` ファイルは排他ファイルロック (`FileShare.None`) で開かれる。

**設計根拠**: 組み込み DB として SQLite / LiteDB と同じポジションを取る設計判断。ネットワーク層を
持たないことで、シリアライゼーションオーバーヘッド・接続管理・認証・TLS の複雑さを排除し、
レイテンシを最小化する。単一プロセスモデルはトランザクションの一貫性保証を大幅に簡素化し、
分散合意プロトコルを不要にする。

**緩和策**: マルチプロセスアクセスが必要な場合は、アプリケーション側で以下のいずれかを採用する:
- 単一のホストプロセスが DB を開き、gRPC / HTTP 等で他プロセスにサービスを提供する
- 読み取り専用アクセスのみの場合、`CreateSnapshot()` で取得したバックアップファイルを別プロセスで開く

**将来方針**: サーバモードの追加は 1.x のスコープ外。将来的にコミュニティ需要があれば、
`Quiver.Server` パッケージとして Quiver の上に薄い gRPC / HTTP ラッパを別リポジトリで
提供する可能性があるが、コアエンジンは組み込み専用を維持する。

## チェックポイントによる WAL 切り詰め {#wal-truncation}

WAL の切り詰めは、完全なチェックポイント (Begin + End) の後にのみ発生する。アプリケーションが
チェックポイントなしで長時間動作する場合（例: 非常に大きく長時間実行されるトランザクション）、
WAL ファイルは際限なく増大する。

**設計根拠**: WAL の切り詰めにはチェックポイントの完全性（Begin + End sentinel が揃っている）の
保証が必要であり、不完全なチェックポイントで切り詰めるとクラッシュ時にデータを喪失する。
また、オープン中のトランザクションが参照する WAL レコードを切り詰めると、そのトランザクションの
スナップショット一貫性が崩壊する。これらの安全性保証を維持するために、切り詰めの前提条件は
保守的に設定されている。

**緩和策**:
- トランザクションを短く保つ（[§short-transactions](#short-transactions) 参照）
- recovery 時間を目標に自動調整する場合は `CheckpointPolicy.Adaptive` を選ぶ
- 長時間バッチ処理ではバッチを小さいトランザクションに分割し、各 commit 後にチェックポイントの余地を与える
- `AutoVacuum` を有効にすると、アイドル時に定期的にチェックポイント + vacuum が実行される

**将来方針**: 1.x では `CheckpointPolicy.Adaptive` の自動しきい値調整（実装済み）に
より、通常ワークロードでは WAL の際限なき増大は起きない。極端なケース（単一トランザクションで
数百万件書き込み）への対応として、トランザクション内チェックポイント（savepoint 境界での
部分切り詰め）を将来検討する。
