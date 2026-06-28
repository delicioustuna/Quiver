// Dock model types (Document, Tool, RootDock, ProportionalDock, ToolDock, DocumentDock, etc.)
// come from the Dock.Model.Inpc NuGet package. Custom model classes were removed because
// plain auto-properties without PropertyChanged caused StackOverflow in the Dock.Avalonia
// rendering pipeline. See StudioDockFactory for the layout, App.axaml for DataTemplates.
