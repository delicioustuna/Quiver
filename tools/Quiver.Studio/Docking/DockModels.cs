// Document、Tool、RootDock、ProportionalDock、ToolDock、DocumentDock などのドックモデル型は
// Dock.Model.Inpc NuGet パッケージから取得する。PropertyChanged を通知しない単純な自動プロパティを持つ
// 独自モデルは Dock.Avalonia の描画パイプラインで StackOverflow を起こすため使用しない。
// レイアウトは StudioDockFactory、DataTemplate は App.axaml を参照。
