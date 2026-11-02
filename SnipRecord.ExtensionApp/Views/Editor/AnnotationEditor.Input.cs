using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SnipRecord.ExtensionApp.Services.Editor;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace SnipRecord.ExtensionApp.Views.Editor;

// Pointer / keyboard / inline-text / on-screen drawing half of the editor.
public sealed partial class AnnotationEditor
{
    // view mapping: screen DIP = _origin + (imagePx - crop.TopLeft) * _scale
    private float _scale = 1f;
    private Vector2 _origin;

    private Guid? _selected;
    private Annotation? Selected => _selected is { } id ? _doc.Find(id) : null;

    private bool _pressed;
    private uint _pointerId;
    private Vector2 _start;                 // image px
    private readonly List<Vector2> _points = new();
    private Annotation? _pending;           // shape being drawn (not in the document yet)
    private Annotation? _dragBefore;        // select tool: state before the move
    private Rect? _cropPreview;

    private TextBox? _editBox;
    private TextAnnotation? _editTarget;    // template (new) or original (existing)
    private bool _editIsNew, _committing;

    // ---------------------------------------------------------------- view

    private void UpdateView()
    {
        var rs = (float)(_canvas.XamlRoot?.RasterizationScale ?? 1.0);
        var crop = _doc.Crop;
        var aw = (float)(_canvas.ActualWidth - 2 * Pad);
        var ah = (float)(_canvas.ActualHeight - 2 * Pad);
        var s = 1f / rs;                                            // 1 image px = 1 physical px when it fits
        if (aw > 0 && ah > 0) s = Math.Min(s, Math.Min(aw / (float)crop.Width, ah / (float)crop.Height));
        _scale = Math.Max(s, 0.01f);
        var dw = (float)crop.Width * _scale;
        var dh = (float)crop.Height * _scale;
        // snap the origin to physical pixels so a 1:1 image stays crisp
        _origin = new Vector2(MathF.Round(((float)_canvas.ActualWidth - dw) / 2 * rs) / rs,
                              MathF.Round(((float)_canvas.ActualHeight - dh) / 2 * rs) / rs);
    }

    private Matrix3x2 ViewMatrix =>
        Matrix3x2.CreateTranslation((float)-_doc.Crop.X, (float)-_doc.Crop.Y) * Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_origin);

    private Vector2 ToImage(Point p) =>
        (new Vector2((float)p.X, (float)p.Y) - _origin) / _scale + new Vector2((float)_doc.Crop.X, (float)_doc.Crop.Y);

    private Vector2 ToScreen(Vector2 img) => Vector2.Transform(img, ViewMatrix);

    private Rect ToScreen(Rect r)
    {
        var a = ToScreen(new Vector2((float)r.X, (float)r.Y));
        return new Rect(a.X, a.Y, r.Width * _scale, r.Height * _scale);
    }

    private Vector2 ClampToCrop(Vector2 p)
    {
        var c = _doc.Crop;
        return new Vector2(Math.Clamp(p.X, (float)c.X, (float)(c.X + c.Width)), Math.Clamp(p.Y, (float)c.Y, (float)(c.Y + c.Height)));
    }

    // ---------------------------------------------------------------- Win2D

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // First creation on the shared device: the bitmap was already loaded there.
        // After device loss: rebuild the source from the in-memory original pixels.
        if (args.Reason == CanvasCreateResourcesReason.FirstTime && ReferenceEquals(_renderer.Source.Device, sender.Device)) return;
        var bmp = CanvasBitmap.CreateFromBytes(sender, _pixels, _doc.PixelWidth, _doc.PixelHeight, _renderer.Source.Format, 96f, _renderer.Source.AlphaMode);
        _renderer.ReplaceSource(bmp);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        UpdateView();
        var ds = args.DrawingSession;
        var imageRect = ToScreen(_doc.Crop);
        ds.FillRectangle(new Rect(imageRect.X + 2, imageRect.Y + 3, imageRect.Width, imageRect.Height), Color.FromArgb(0x28, 0, 0, 0)); // soft shadow
        ds.Transform = ViewMatrix;
        _renderer.Draw(ds, _doc, _pending, _editIsNew ? null : _editTarget?.Id);
        ds.Transform = Matrix3x2.Identity;
        ds.DrawRectangle(imageRect, Color.FromArgb(0x30, 0x80, 0x80, 0x80), 1f);

        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        if (_tool == EditorTool.Select && Selected is { } sel)
        {
            var b = ToScreen(sel.Bounds);
            b = new Rect(b.X - 4, b.Y - 4, b.Width + 8, b.Height + 8);
            ds.DrawRectangle(b, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1f);
            ds.DrawRectangle(b, Color.FromArgb(0xFF, 0x00, 0x78, 0xD4), 1f, dash);
        }
        if (_cropPreview is { } cp && cp.Width > 0 && cp.Height > 0)
        {
            var c = ToScreen(cp);
            using (var all = CanvasGeometry.CreateRectangle(ds, imageRect))
            using (var inner = CanvasGeometry.CreateRectangle(ds, c))
            using (var outside = all.CombineWith(inner, Matrix3x2.Identity, CanvasGeometryCombine.Exclude))
                ds.FillGeometry(outside, Color.FromArgb(0x90, 0, 0, 0));
            ds.DrawRectangle(c, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1.5f);
            ds.DrawRectangle(c, Color.FromArgb(0xFF, 0x00, 0x78, 0xD4), 1.5f, dash);
            using var fmt = new CanvasTextFormat { FontSize = 12, FontFamily = AnnotationRenderer.FontFamily };
            ds.DrawText($"{(int)cp.Width} × {(int)cp.Height}", (float)c.X + 4, (float)Math.Max(imageRect.Y, c.Y - 20), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), fmt);
        }
    }

    // ---------------------------------------------------------------- pointer

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var hadEdit = _editBox != null;
        CommitText();
        _canvas.Focus(FocusState.Pointer);
        var p = ToImage(pt.Position);
        var tol = 6f / _scale;

        switch (_tool)
        {
            case EditorTool.Select:
                var hit = _doc.HitTest(p, tol);
                _selected = hit?.Id;
                _dragBefore = hit;
                break;
            case EditorTool.Text:
                if (!hadEdit)   // a click while editing only ends the edit
                {
                    if (_doc.HitTest(p, tol) is TextAnnotation existing) BeginTextEdit(existing, false);
                    else if (_doc.Crop.Contains(new Point(p.X, p.Y))) BeginTextEdit(new TextAnnotation(_color, p, "", FontPx(), default), true);
                }
                e.Handled = true;
                return;
            case EditorTool.Number:
                if (_doc.Crop.Contains(new Point(p.X, p.Y)))
                    _stack.Execute(new AddCommand(new NumberAnnotation(_color, p, Px(NumberRadiusDip), _doc.NextNumber())));
                e.Handled = true;
                return;
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                _points.Clear();
                break;
        }
        _start = p;
        _pressed = true;
        _pointerId = e.Pointer.PointerId;
        _canvas.CapturePointer(e.Pointer);
        UpdatePending(p, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || e.Pointer.PointerId != _pointerId) return;
        if (_tool is EditorTool.Pen or EditorTool.Highlighter)
            foreach (var pp in e.GetIntermediatePoints(_canvas).Reverse())   // intermediate points come newest-first
                AddPoint(ToImage(pp.Position));
        UpdatePending(ToImage(e.GetCurrentPoint(_canvas).Position), e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || e.Pointer.PointerId != _pointerId) return;
        _pressed = false;
        _canvas.ReleasePointerCaptures();
        var minSize = 3f / _scale;

        switch (_tool)
        {
            case EditorTool.Select:
                if (_dragBefore != null && Selected is { } now && now != _dragBefore)
                    _stack.Record(new ReplaceCommand(_dragBefore, now));
                _dragBefore = null;
                break;
            case EditorTool.Crop:
                if (_cropPreview is { } r && r.Width >= Math.Max(4, minSize) && r.Height >= Math.Max(4, minSize))
                {
                    _stack.Execute(new CropCommand(_doc.Crop, r));
                    ShowStatus("已裁剪 (Ctrl+Z 撤销)");
                }
                _cropPreview = null;
                break;
            default:
                var a = _pending;
                _pending = null;
                if (a != null && IsMeaningful(a, minSize)) _stack.Execute(new AddCommand(a));
                break;
        }
        _canvas.Invalidate();
    }

    private static bool IsMeaningful(Annotation a, float min) => a switch
    {
        StrokeAnnotation => true,                               // a click = a dot
        LineAnnotation l => Vector2.Distance(l.Start, l.End) >= min,
        _ => a.Bounds.Width >= min && a.Bounds.Height >= min,
    };

    private void AddPoint(Vector2 p)
    {
        if (_points.Count == 0 || Vector2.DistanceSquared(_points[^1], p) * _scale * _scale >= 1.5f) _points.Add(p);
    }

    private void UpdatePending(Vector2 p, bool shift)
    {
        switch (_tool)
        {
            case EditorTool.Select:
                if (_dragBefore != null && (p - _start).LengthSquared() > 0) _doc.Replace(_dragBefore.Offset(p - _start));
                break;
            case EditorTool.Pen:
                AddPoint(p);
                _pending = new StrokeAnnotation(_color, StrokePx(), false, _points.ToArray());
                break;
            case EditorTool.Highlighter:
                AddPoint(p);
                _pending = new StrokeAnnotation(Color.FromArgb(0x66, _color.R, _color.G, _color.B), StrokePx() * 4, true, _points.ToArray());
                break;
            case EditorTool.Arrow:
            case EditorTool.Line:
                _pending = new LineAnnotation(_color, StrokePx(), _start, shift ? Snap45(_start, p) : p, _tool == EditorTool.Arrow);
                break;
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
                _pending = new ShapeAnnotation(_color, StrokePx(), Annotation.Normalize(_start, shift ? Square(_start, p) : p), _tool == EditorTool.Ellipse);
                break;
            case EditorTool.Mosaic:
                var mr = _doc.ClampToCrop(Annotation.Normalize(_start, p));
                _pending = mr.IsEmpty ? null : new MosaicAnnotation(mr, MosaicPx());
                break;
            case EditorTool.Crop:
                var cr = _doc.ClampToCrop(Annotation.Normalize(ClampToCrop(_start), ClampToCrop(p)));
                _cropPreview = cr.IsEmpty ? null : cr;
                break;
        }
    }

    private static Vector2 Snap45(Vector2 a, Vector2 b)
    {
        var d = b - a;
        var angle = MathF.Round(MathF.Atan2(d.Y, d.X) / (MathF.PI / 4)) * (MathF.PI / 4);
        return a + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * d.Length();
    }

    private static Vector2 Square(Vector2 a, Vector2 b)
    {
        var d = b - a;
        var m = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return a + new Vector2(MathF.CopySign(m, d.X == 0 ? 1 : d.X), MathF.CopySign(m, d.Y == 0 ? 1 : d.Y));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_tool != EditorTool.Select) return;
        if (_doc.HitTest(ToImage(e.GetPosition(_canvas)), 6f / _scale) is TextAnnotation t) BeginTextEdit(t, false);
    }

    // ---------------------------------------------------------------- inline text

    private void BeginTextEdit(TextAnnotation target, bool isNew)
    {
        _editTarget = target;
        _editIsNew = isNew;
        _selected = isNew ? null : target.Id;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var fg = new SolidColorBrush(target.Color);
        var box = new TextBox
        {
            Text = target.Text,
            FontFamily = new FontFamily(AnnotationRenderer.FontFamily),
            FontSize = target.FontSize * _scale,
            Foreground = fg,
            Background = clear,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 40,
            MinHeight = 0,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            PlaceholderText = "输入文字",
        };
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackground" }) box.Resources[key] = clear;
        foreach (var key in new[] { "TextControlForegroundFocused", "TextControlForegroundPointerOver", "TextControlForeground" }) box.Resources[key] = fg;
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        var screen = ToScreen(target.Position);
        Canvas.SetLeft(box, screen.X - 1);
        Canvas.SetTop(box, screen.Y - 1);
        box.KeyDown += (_, e) =>
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Escape || (e.Key == VirtualKey.Enter && !shift))
            {
                e.Handled = true;          // Enter = done, Shift+Enter = new line, Esc = done
                CommitText();
                _canvas.Focus(FocusState.Programmatic);
            }
        };
        box.LostFocus += (_, _) => CommitText();
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectionStart = box.Text.Length; };
        _editBox = box;
        _overlay.Children.Add(box);
        _canvas.Invalidate();
    }

    private void CommitText()
    {
        if (_editBox is not { } box || _editTarget is not { } target || _committing) return;
        _committing = true;
        try
        {
            var text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
            var color = box.Foreground is SolidColorBrush b ? b.Color : target.Color;
            var size = (float)Math.Max(8, Math.Round(box.FontSize / _scale));
            if (Math.Abs(size - target.FontSize) < 1) size = target.FontSize;
            var isNew = _editIsNew;
            _editBox = null;
            _editTarget = null;
            _editIsNew = false;
            _overlay.Children.Remove(box);
            var edited = target with { Text = text, Color = color, FontSize = size, Size = AnnotationRenderer.MeasureText(CanvasDevice.GetSharedDevice(), text, size) };
            if (isNew)
            {
                if (text.Length > 0) _stack.Execute(new AddCommand(edited));
            }
            else if (text.Length == 0) { _stack.Execute(new RemoveCommand(target)); _selected = null; }
            else if (edited != target) _stack.Execute(new ReplaceCommand(target, edited));
            _canvas.Invalidate();
        }
        finally { _committing = false; }
    }

    // ---------------------------------------------------------------- keyboard

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editBox != null) return;                 // the TextBox owns the keyboard while editing
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        var handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Z when shift: Redo(); break;
                case VirtualKey.Z: Undo(); break;
                case VirtualKey.Y: Redo(); break;
                case VirtualKey.C: _ = CopyAsync(); break;
                case VirtualKey.S when shift: _ = SaveAsAsync(); break;
                case VirtualKey.S: _ = SaveAsync(_options.DefaultSave); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case VirtualKey.Escape:
                    if (_pressed) CancelGesture();
                    else if (_selected != null) { _selected = null; _canvas.Invalidate(); }
                    else RequestClose();
                    break;
                case VirtualKey.Delete:
                case VirtualKey.Back: DeleteSelected(); break;
                case VirtualKey.V: SelectTool(EditorTool.Select); break;
                case VirtualKey.P: SelectTool(EditorTool.Pen); break;
                case VirtualKey.H: SelectTool(EditorTool.Highlighter); break;
                case VirtualKey.A: SelectTool(EditorTool.Arrow); break;
                case VirtualKey.L: SelectTool(EditorTool.Line); break;
                case VirtualKey.R: SelectTool(EditorTool.Rectangle); break;
                case VirtualKey.E: SelectTool(EditorTool.Ellipse); break;
                case VirtualKey.T: SelectTool(EditorTool.Text); break;
                case VirtualKey.M: SelectTool(EditorTool.Mosaic); break;
                case VirtualKey.N: SelectTool(EditorTool.Number); break;
                case VirtualKey.C: SelectTool(EditorTool.Crop); break;
                case VirtualKey.Number1: case VirtualKey.NumberPad1: ApplyWidth(0); break;
                case VirtualKey.Number2: case VirtualKey.NumberPad2: ApplyWidth(1); break;
                case VirtualKey.Number3: case VirtualKey.NumberPad3: ApplyWidth(2); break;
                default: handled = false; break;
            }
        }
        e.Handled = handled;
    }

    private void CancelGesture()
    {
        _pressed = false;
        _pending = null;
        _cropPreview = null;
        if (_dragBefore != null) { _doc.Replace(_dragBefore); _dragBefore = null; }
        _canvas.ReleasePointerCaptures();
        _canvas.Invalidate();
    }
}
