# Yatagarasu 運用ガイド (Operations Cookbook)

このディレクトリは **Yatagarasu を埋め込んだアプリケーションを本番運用する人** に向けた実務ドキュメント集。
「どう API を呼ぶか」を扱う [docs/cookbook.md](../cookbook.md) や [docs/api/](../api/) とは目的が異なり、
ここでは **「どう設定すれば速いか」「壊れた DB をどう直すか」「何ができないか」** を扱う。

| 章 | いつ読むか |
|---|---|
| [01_quickstart.md](01_quickstart.md) | 初めて Yatagarasu を埋め込む。最小構成で 1 つの DB を立ち上げ、最初の commit を通すまで。 |
| [02_backup_restore.md](02_backup_restore.md) | バックアップ戦略を決める。ライブスナップショット / コールドコピー / 別マシンへの復元。 |
| [03_performance_tuning.md](03_performance_tuning.md) | 挿入や検索が遅い。チューニングノブ (バッファプール / WAL / checkpoint / group commit / lock) を回す前に。 |
| [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md) | クラッシュ後に起動できない / データが合わない。recovery の読み方、orphan index の修復。 |
| [05_known_limits.md](05_known_limits.md) | 採用判断・キャパシティプランニング。embedded 前提の制約、サイズの目安。 |
| [06_yatagarasu_rename.md](06_yatagarasu_rename.md) | 0.6.x以前から0.7.0へ移行する。パッケージ、名前空間、型名、DB拡張子、NuGet廃止設定。 |

> **最優先で読むべき一行** — 大量挿入は必ず 1 トランザクションに詰めること。
> per-tx (1 件 1 commit) パターンは bulk パスより **約 100 倍遅い**。
> 根拠と詳細は [03_performance_tuning.md](03_performance_tuning.md) 冒頭。

これらの章は実装済み機能 (ライブスナップショット、vacuum、索引修復、グループコミット、
適応チェックポイント) を前提にしている。
各機能の設計詳細は [docs/spec/](../spec/) を参照。
