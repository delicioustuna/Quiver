using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Quiver.Studio.Services;

namespace Quiver.Studio.Models;

public sealed class RoslynCompletionData : ICompletionData
{
    private readonly CompletionGlyph _glyph;
    private readonly Func<CancellationToken, Task<string?>>? _descriptionFactory;
    private object? _cachedDescription;

    public RoslynCompletionData(CompletionEntry entry)
    {
        Text = entry.DisplayText;
        _glyph = entry.Glyph;
        _descriptionFactory = entry.DescriptionFactory;
        Priority = entry.Glyph switch
        {
            CompletionGlyph.Method or CompletionGlyph.ExtensionMethod => 1.0,
            CompletionGlyph.Property => 0.9,
            CompletionGlyph.Field => 0.8,
            CompletionGlyph.Class or CompletionGlyph.Struct or CompletionGlyph.Interface => 0.7,
            CompletionGlyph.Keyword => 0.5,
            _ => 0.6,
        };
    }

    public string Text { get; }

    public object Content => Text;

    public object? Description
    {
        get
        {
            if (_cachedDescription is not null)
                return _cachedDescription;

            if (_descriptionFactory is null)
                return _glyph != CompletionGlyph.Other ? _glyph.ToString() : null;

            var tb = new TextBlock
            {
                Text = "Loading...",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
            };
            _cachedDescription = tb;

            _ = Task.Run(async () =>
            {
                try
                {
                    var text = await _descriptionFactory(CancellationToken.None);
                    if (!string.IsNullOrEmpty(text))
                        Dispatcher.UIThread.Post(() => tb.Text = text);
                    else
                        Dispatcher.UIThread.Post(() => tb.Text = _glyph.ToString());
                }
                catch
                {
                    Dispatcher.UIThread.Post(() => tb.Text = _glyph.ToString());
                }
            });

            return tb;
        }
    }

    public IImage? Image => null;

    public double Priority { get; }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, Text);
    }
}
