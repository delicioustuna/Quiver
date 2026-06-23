using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Quiver.Studio.Models;
using Quiver.Studio.Rendering;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public sealed class GraphCanvasPanel : Control
{
    private GraphCanvasViewModel? _vm;
    private VisualNode? _dragNode;
    private Point _lastPointer;
    private bool _isPanning;
    private bool _needsFit = true;
    private Size _lastSize;

    public GraphCanvasPanel()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void Attach(GraphCanvasViewModel vm)
    {
        if (_vm is not null)
            _vm.GraphChanged -= OnGraphChanged;

        _vm = vm;
        _vm.GraphChanged += OnGraphChanged;
    }

    private void OnGraphChanged()
    {
        _needsFit = true;
        InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (result != _lastSize)
        {
            _lastSize = result;
            _needsFit = true;
            InvalidateVisual();
        }
        return result;
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.White, null, new Rect(Bounds.Size));

        if (_vm is not { HasGraph: true }) return;

        if (_needsFit && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _vm.Renderer.Camera.FitToContent(_vm.Nodes, Bounds.Width, Bounds.Height);
            _needsFit = false;
        }

        _vm.Renderer.Render(context, _vm.Nodes, _vm.Edges);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is not { HasGraph: true }) return;

        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            var hit = HitTestHelper.HitTestNode(_vm.Nodes, _vm.Renderer.Camera, pos.X, pos.Y);
            if (hit is not null)
            {
                _dragNode = hit;
                _vm.SelectNode(hit);
                e.Pointer.Capture(this);
            }
            else
            {
                _vm.SelectNode(null);
                _isPanning = true;
                e.Pointer.Capture(this);
            }
            _lastPointer = pos;
            InvalidateVisual();
        }
        else if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _lastPointer = pos;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is not { HasGraph: true }) return;

        var pos = e.GetPosition(this);

        if (_dragNode is not null)
        {
            var camera = _vm.Renderer.Camera;
            var worldBefore = camera.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
            var worldNow = camera.ScreenToWorld(pos.X, pos.Y);
            _dragNode.X += worldNow.X - worldBefore.X;
            _dragNode.Y += worldNow.Y - worldBefore.Y;
            _dragNode.IsPinned = true;
            _lastPointer = pos;
            InvalidateVisual();
        }
        else if (_isPanning)
        {
            var dx = pos.X - _lastPointer.X;
            var dy = pos.Y - _lastPointer.Y;
            _vm.Renderer.Camera.Pan(dx, dy);
            _lastPointer = pos;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragNode = null;
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm is not { HasGraph: true }) return;

        var pos = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _vm.Renderer.Camera.ZoomAt(factor, pos.X, pos.Y);
        InvalidateVisual();
    }
}
