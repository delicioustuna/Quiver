using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Quiver.Studio.Models;
using Quiver.Studio.Rendering;
using Quiver.Studio.ViewModels;

namespace Quiver.Studio.Views;

public sealed class GraphCanvasPanel : Control
{
    private GraphCanvasViewModel? _vm;
    private VisualVertex? _dragVertex;
    private Point _lastPointer;
    private bool _isPanning;
    private bool _isMinimapDragging;
    private bool _needsFit = true;
    private Size _lastSize;

    private const double MinimapWidth = 160;
    private const double MinimapHeight = 110;
    private const double MinimapMargin = 8;
    private const double MinimapPadding = 6;

    private static readonly IPen LinkDashPen = new Pen(Brushes.DodgerBlue, 2, new DashStyle([4, 4], 0));

    public GraphCanvasPanel()
    {
        ClipToBounds = true;
        Focusable = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void FitToScreen()
    {
        _needsFit = true;
        InvalidateVisual();
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

    private void SyncZoomLevel()
    {
        if (_vm is null) return;
        var zoom = _vm.Renderer.Camera.Zoom;
        Dispatcher.UIThread.Post(() => { if (_vm is not null) _vm.ZoomLevel = zoom; });
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (result != _lastSize)
        {
            _lastSize = result;
            InvalidateVisual();
        }
        return result;
    }

    public override void Render(DrawingContext context)
    {
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var bg = isDark ? Brushes.Black : Brushes.White;
        context.DrawRectangle(bg, null, new Rect(Bounds.Size));

        if (_vm is not { HasGraph: true }) return;
        _vm.Renderer.IsDarkTheme = isDark;

        if (_needsFit && Bounds.Width > 0 && Bounds.Height > 0)
        {
            _vm.Renderer.Camera.FitToContent(_vm.Vertices, Bounds.Width, Bounds.Height);
            _needsFit = false;
            SyncZoomLevel();
        }

        _vm.Renderer.Render(context, _vm.Vertices, _vm.Edges);

        if (_vm.IsLinkMode && _vm.LinkSource is { } src)
        {
            var from = _vm.Renderer.Camera.WorldToScreen(src.X, src.Y);
            var to = new Point(_vm.LinkCursorX, _vm.LinkCursorY);
            context.DrawLine(LinkDashPen, from, to);
        }

        DrawMinimap(context);
    }

    private void DrawMinimap(DrawingContext ctx)
    {
        if (_vm is not { HasGraph: true } || _vm.Vertices.Count == 0) return;
        if (Bounds.Width < MinimapWidth * 2 || Bounds.Height < MinimapHeight * 2) return;

        var mapRect = GetMinimapRect();
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var bgBrush = isDark
            ? new SolidColorBrush(Color.FromArgb(180, 30, 30, 30))
            : new SolidColorBrush(Color.FromArgb(180, 245, 245, 245));
        var borderPen = new Pen(isDark ? Brushes.Gray : Brushes.Silver, 1);
        ctx.DrawRectangle(bgBrush, borderPen, mapRect, 4, 4);

        ComputeWorldBounds(out var wMinX, out var wMinY, out var wMaxX, out var wMaxY);
        var wW = wMaxX - wMinX;
        var wH = wMaxY - wMinY;
        if (wW < 1) wW = 1;
        if (wH < 1) wH = 1;

        var innerX = mapRect.X + MinimapPadding;
        var innerY = mapRect.Y + MinimapPadding;
        var innerW = mapRect.Width - MinimapPadding * 2;
        var innerH = mapRect.Height - MinimapPadding * 2;
        var scale = Math.Min(innerW / wW, innerH / wH);

        var drawW = wW * scale;
        var drawH = wH * scale;
        var ox = innerX + (innerW - drawW) / 2;
        var oy = innerY + (innerH - drawH) / 2;

        foreach (var vertex in _vm.Vertices)
        {
            var nx = ox + (vertex.X - wMinX) * scale;
            var ny = oy + (vertex.Y - wMinY) * scale;
            var dotR = Math.Max(2, vertex.Radius * scale * 0.5);
            var brush = new SolidColorBrush(vertex.Color);
            ctx.DrawEllipse(brush, null, new Point(nx, ny), dotR, dotR);
        }

        var camera = _vm.Renderer.Camera;
        var vpTL = camera.ScreenToWorld(0, 0);
        var vpBR = camera.ScreenToWorld(Bounds.Width, Bounds.Height);

        var vx = ox + (vpTL.X - wMinX) * scale;
        var vy = oy + (vpTL.Y - wMinY) * scale;
        var vw = (vpBR.X - vpTL.X) * scale;
        var vh = (vpBR.Y - vpTL.Y) * scale;

        vx = Math.Max(vx, ox);
        vy = Math.Max(vy, oy);
        if (vx + vw > ox + drawW) vw = ox + drawW - vx;
        if (vy + vh > oy + drawH) vh = oy + drawH - vy;

        if (vw > 0 && vh > 0)
        {
            var vpBrush = isDark
                ? new SolidColorBrush(Color.FromArgb(50, 100, 180, 255))
                : new SolidColorBrush(Color.FromArgb(50, 30, 100, 220));
            var vpPen = new Pen(Brushes.DodgerBlue, 1);
            ctx.DrawRectangle(vpBrush, vpPen, new Rect(vx, vy, vw, vh), 2, 2);
        }
    }

    private Rect GetMinimapRect()
    {
        var x = Bounds.Width - MinimapWidth - MinimapMargin;
        var y = Bounds.Height - MinimapHeight - MinimapMargin;
        return new Rect(x, y, MinimapWidth, MinimapHeight);
    }

    private void ComputeWorldBounds(out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.MaxValue; minY = double.MaxValue;
        maxX = double.MinValue; maxY = double.MinValue;
        foreach (var n in _vm!.Vertices)
        {
            var r = n.Radius;
            if (n.X - r < minX) minX = n.X - r;
            if (n.Y - r < minY) minY = n.Y - r;
            if (n.X + r > maxX) maxX = n.X + r;
            if (n.Y + r > maxY) maxY = n.Y + r;
        }
    }

    private bool TryMinimapPan(double screenX, double screenY)
    {
        if (_vm is not { HasGraph: true } || _vm.Vertices.Count == 0) return false;

        var mapRect = GetMinimapRect();
        if (!mapRect.Contains(new Point(screenX, screenY))) return false;

        ComputeWorldBounds(out var wMinX, out var wMinY, out var wMaxX, out var wMaxY);
        var wW = wMaxX - wMinX;
        var wH = wMaxY - wMinY;
        if (wW < 1) wW = 1;
        if (wH < 1) wH = 1;

        var innerX = mapRect.X + MinimapPadding;
        var innerY = mapRect.Y + MinimapPadding;
        var innerW = mapRect.Width - MinimapPadding * 2;
        var innerH = mapRect.Height - MinimapPadding * 2;
        var scale = Math.Min(innerW / wW, innerH / wH);

        var drawW = wW * scale;
        var drawH = wH * scale;
        var ox = innerX + (innerW - drawW) / 2;
        var oy = innerY + (innerH - drawH) / 2;

        var worldX = wMinX + (screenX - ox) / scale;
        var worldY = wMinY + (screenY - oy) / scale;

        var camera = _vm.Renderer.Camera;
        camera.OffsetX = Bounds.Width / 2 - worldX * camera.Zoom;
        camera.OffsetY = Bounds.Height / 2 - worldY * camera.Zoom;

        InvalidateVisual();
        return true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null) return;

        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (_vm.IsLinkMode && props.IsLeftButtonPressed)
        {
            var hitVertex = _vm.HasGraph
                ? HitTestHelper.HitTestVertex(_vm.Vertices, _vm.Renderer.Camera, pos.X, pos.Y)
                : null;
            if (hitVertex is not null)
                _ = _vm.CompleteLinkAsync(hitVertex);
            else
                _vm.CancelLinkMode();
            InvalidateVisual();
            return;
        }

        if (!_vm.HasGraph) return;

        if (props.IsRightButtonPressed)
        {
            var hitVertex = HitTestHelper.HitTestVertex(_vm.Vertices, _vm.Renderer.Camera, pos.X, pos.Y);
            if (hitVertex is not null)
            {
                _vm.SelectVertex(hitVertex);
                ShowVertexContextMenu(hitVertex, pos);
            }
            else
            {
                var hitEdge = HitTestHelper.HitTestEdge(_vm.Edges, _vm.Renderer.Camera, pos.X, pos.Y);
                if (hitEdge is not null)
                {
                    _vm.SelectEdge(hitEdge);
                    ShowEdgeContextMenu(hitEdge, pos);
                }
                else
                {
                    ShowCanvasContextMenu(pos);
                }
            }
            InvalidateVisual();
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (TryMinimapPan(pos.X, pos.Y))
            {
                _isMinimapDragging = true;
                e.Pointer.Capture(this);
                return;
            }

            var hitVertex = HitTestHelper.HitTestVertex(_vm.Vertices, _vm.Renderer.Camera, pos.X, pos.Y);
            if (hitVertex is not null)
            {
                _dragVertex = hitVertex;
                _vm.SelectVertex(hitVertex);
                e.Pointer.Capture(this);
            }
            else
            {
                var hitEdge = HitTestHelper.HitTestEdge(_vm.Edges, _vm.Renderer.Camera, pos.X, pos.Y);
                if (hitEdge is not null)
                {
                    _vm.SelectEdge(hitEdge);
                }
                else
                {
                    _vm.SelectVertex(null);
                    _isPanning = true;
                }
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
        if (_vm is null) return;

        var pos = e.GetPosition(this);

        if (_vm.IsLinkMode)
        {
            _vm.LinkCursorX = pos.X;
            _vm.LinkCursorY = pos.Y;
            InvalidateVisual();
            return;
        }

        if (_isMinimapDragging)
        {
            TryMinimapPan(pos.X, pos.Y);
            return;
        }

        if (!_vm.HasGraph) return;

        if (_dragVertex is not null)
        {
            var camera = _vm.Renderer.Camera;
            var worldBefore = camera.ScreenToWorld(_lastPointer.X, _lastPointer.Y);
            var worldNow = camera.ScreenToWorld(pos.X, pos.Y);
            _dragVertex.X += worldNow.X - worldBefore.X;
            _dragVertex.Y += worldNow.Y - worldBefore.Y;
            _dragVertex.IsPinned = true;
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
        _dragVertex = null;
        _isPanning = false;
        _isMinimapDragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm is not { HasGraph: true }) return;

        var pos = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _vm.Renderer.Camera.ZoomAt(factor, pos.X, pos.Y);
        SyncZoomLevel();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { IsLinkMode: true })
        {
            _vm.CancelLinkMode();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void ShowCanvasContextMenu(Point screenPos)
    {
        if (_vm is null) return;
        var world = _vm.Renderer.Camera.ScreenToWorld(screenPos.X, screenPos.Y);
        _vm.LastContextWorldX = world.X;
        _vm.LastContextWorldY = world.Y;

        var menu = new ContextMenu();
        var addVertex = new MenuItem { Header = "Add Vertex..." };
        addVertex.Click += async (_, _) =>
        {
            await _vm.InvokeAddVertexRequested();
        };
        menu.Items.Add(addVertex);
        menu.Open(this);
    }

    private void ShowVertexContextMenu(VisualVertex vertex, Point screenPos)
    {
        var menu = new ContextMenu();

        var addEdge = new MenuItem { Header = "Add Edge from here..." };
        addEdge.Click += (_, _) => _vm?.BeginLinkMode(vertex);
        menu.Items.Add(addEdge);

        var deleteVertex = new MenuItem { Header = "Delete Vertex" };
        deleteVertex.Click += async (_, _) =>
        {
            if (_vm is null) return;
            try
            {
                _vm._editingService?.DeleteVertex(vertex.Id);
                _vm.RemoveVertexFromGraph(vertex);
            }
            catch (Exception ex)
            {
                _vm._logger.LogError(ex, "Vertex削除失敗");
            }
        };
        menu.Items.Add(deleteVertex);

        menu.Open(this);
    }

    private void ShowEdgeContextMenu(VisualEdge edge, Point screenPos)
    {
        var menu = new ContextMenu();
        var deleteEdge = new MenuItem { Header = "Delete Edge" };
        deleteEdge.Click += async (_, _) =>
        {
            if (_vm is null) return;
            try
            {
                _vm._editingService?.DeleteEdge(edge.Id);
                _vm.RemoveEdgeFromGraph(edge);
            }
            catch (Exception ex)
            {
                _vm._logger.LogError(ex, "Edge削除失敗");
            }
        };
        menu.Items.Add(deleteEdge);
        menu.Open(this);
    }
}
