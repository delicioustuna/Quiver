using System.Runtime.CompilerServices;

// 旧サブアセンブリの統合により InternalsVisibleTo をここへ集約。
// テスト/benchmark と、公開面を狭く保った同梱 add-on の Quiver.Rag に公開する。
[assembly: InternalsVisibleTo("Quiver.Tests")]
[assembly: InternalsVisibleTo("Quiver.Backend.Tests")]
[assembly: InternalsVisibleTo("Quiver.Index.Tests")]
[assembly: InternalsVisibleTo("Quiver.Operators.Tests")]
[assembly: InternalsVisibleTo("Quiver.PropertyTests")]
[assembly: InternalsVisibleTo("Quiver.Storage.Tests")]
[assembly: InternalsVisibleTo("Quiver.Stores.Tests")]
[assembly: InternalsVisibleTo("Quiver.Transactions.Tests")]
[assembly: InternalsVisibleTo("Quiver.Wal.Tests")]
[assembly: InternalsVisibleTo("Quiver.Benchmarks")]
[assembly: InternalsVisibleTo("Quiver.Rag")]
// 内部実装 (codec / WAL parser / 物理オペレータ) を直接検査する dev/test 系。
[assembly: InternalsVisibleTo("Quiver.Codec.Tests")]
[assembly: InternalsVisibleTo("Quiver.FuzzTests")]
[assembly: InternalsVisibleTo("QuiverSandbox")]
