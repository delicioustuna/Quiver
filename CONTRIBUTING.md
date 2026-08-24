# Contributing to Yatagarasu

個人利用プロジェクトのためコントリビューションは想定していませんが、
もし作成する場合は以下のフォーマットに従ってくれると嬉しいです。

## 前提条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 以降

## ビルド

```bash
dotnet build Yatagarasu.slnx
```

## テスト

```bash
dotnet test Yatagarasu.slnx
```

## ブランチ戦略

- **main** — 唯一の公開ブランチ（安定版・リリース対象）
- 変更を提案する場合は作業ブランチから `main` 宛てにPull Requestを作成してください

## コーディング規約

- `dotnet build` が警告なしで通ることを確認してください
- 新機能にはテストを追加してください
- NativeAOT 互換性を維持してください（リフレクション不使用）

## ライセンス

コントリビューションは [MIT License](LICENSE) のもとで提供されます。
