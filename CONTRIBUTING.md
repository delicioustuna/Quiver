# Contributing to Quiver

個人利用プロジェクトのためコントリビューションは想定していませんが、
もし作成する場合は以下のフォーマットに従ってくれると嬉しいです。

## 前提条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 以降

## ビルド

```bash
dotnet build Quiver.slnx
```

## テスト

```bash
dotnet test Quiver.slnx
```

## ブランチ戦略

- **main** — 安定ブランチ（リリース対象）
- **develop** — 開発ブランチ

## コーディング規約

- `dotnet build` が警告なしで通ることを確認してください
- 新機能にはテストを追加してください
- NativeAOT 互換性を維持してください（リフレクション不使用）

## ライセンス

コントリビューションは [MIT License](LICENSE) のもとで提供されます。
