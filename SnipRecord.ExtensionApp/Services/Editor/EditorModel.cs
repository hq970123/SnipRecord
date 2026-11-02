using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace SnipRecord.ExtensionApp.Services.Editor;

// Annotation model for the screenshot editor. Every annotation is an immutable record in
// IMAGE PIXEL coordinates (of the original, uncropped image); edits replace a record with a
// modified copy (same Id), which keeps undo/redo trivial and selection stable.

public enum EditorTool { Select, Pen, Highlighter, Arrow, Line, Rectangle, Ellipse, Text, Mosaic, Number, Crop }

public abstract record Annotation(Color Color)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public abstract Rect Bounds { get; }
    /// <summary>p and tolerance are in image pixels.</summary>
    public abstract bool HitTest(Vector2 p, float tolerance);
    public abstract Annotation Offset(Vector2 d);
    /// <summary>Restyle with a new colour and/or stroke width (pixels). Kinds without the property ignore it.</summary>
    public virtual Annotation WithStyle(Color color, float? width) => this with { Color = color };

    internal static bool InRect(Rect r, Vector2 p, float tol) =>
        p.X >= r.X - tol && p.X <= r.X + r.Width + tol && p.Y >= r.Y - tol && p.Y <= r.Y + r.Height + tol;

    internal static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len = ab.LengthSquared();
        var t = len < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    public static Rect Normalize(Vector2 a, Vector2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    internal static Rect Move(Rect r, Vector2 d) => new(r.X + d.X, r.Y + d.Y, r.Width, r.Height);
}

/// <summary>Freehand pen or highlighter (highlighter = translucent, drawn as one path so overlaps don't darken).</summary>
public sealed record StrokeAnnotation(Color Color, float Width, bool Highlighter, Vector2[] Points) : Annotation(Color)
{
    public override Rect Bounds
    {
        get
        {
            if (Points.Length == 0) return Rect.Empty;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in Points) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            var h = Width / 2;
            return new Rect(x0 - h, y0 - h, x1 - x0 + Width, y1 - y0 + Width);
        }
    }
    public override bool HitTest(Vector2 p, float tol)
    {
        if (Points.Length == 1) return Vector2.Distance(p, Points[0]) <= Width / 2 + tol;
        for (var i = 1; i < Points.Length; i++)
            if (SegmentDistance(p, Points[i - 1], Points[i]) <= Width / 2 + tol) return true;
        return false;
    }
    public override Annotation Offset(Vector2 d) => this with { Points = Points.Select(q => q + d).ToArray() };
    /// <summary>Highlighter keeps its translucency; its width is 4× the pen width level.</summary>
    public override Annotation WithStyle(Color color, float? width) => this with
    {
        Color = Highlighter ? Color.FromArgb(Color.A, color.R, color.G, color.B) : color,
        Width = width.HasValue ? (Highlighter ? width.Value * 4 : width.Value) : Width,
    };
}

/// <summary>Straight line, optionally with an arrow head at End.</summary>
public sealed record LineAnnotation(Color Color, float Width, Vector2 Start, Vector2 End, bool Arrow) : Annotation(Color)
{
    public float HeadLength => Math.Max(12f, Width * 4.5f);
    public override Rect Bounds
    {
        get
        {
            var r = Normalize(Start, End);
            var pad = Arrow ? HeadLength : Width;
            return new Rect(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2);
        }
    }
    public override bool HitTest(Vector2 p, float tol) =>
        SegmentDistance(p, Start, End) <= Width / 2 + tol + (Arrow && Vector2.Distance(p, End) < HeadLength ? HeadLength / 3 : 0);
    public override Annotation Offset(Vector2 d) => this with { Start = Start + d, End = End + d };
    public override Annotation WithStyle(Color color, float? width) => this with { Color = color, Width = width ?? Width };
}

/// <summary>Rectangle or ellipse outline.</summary>
public sealed record ShapeAnnotation(Color Color, float Width, Rect Rect, bool Ellipse) : Annotation(Color)
{
    public override Rect Bounds => new(Rect.X - Width / 2, Rect.Y - Width / 2, Rect.Width + Width, Rect.Height + Width);
    public override bool HitTest(Vector2 p, float tol)
    {
        if (!InRect(Rect, p, Width / 2 + tol)) return false;
        if (!Ellipse) return true;
        var c = new Vector2((float)(Rect.X + Rect.Width / 2), (float)(Rect.Y + Rect.Height / 2));
        var rx = (float)Rect.Width / 2 + Width / 2 + tol; var ry = (float)Rect.Height / 2 + Width / 2 + tol;
        var q = (p - c) / new Vector2(Math.Max(rx, 1), Math.Max(ry, 1));
        return q.LengthSquared() <= 1;
    }
    public override Annotation Offset(Vector2 d) => this with { Rect = Move(Rect, d) };
    public override Annotation WithStyle(Color color, float? width) => this with { Color = color, Width = width ?? Width };
}

/// <summary>Text. Size is the measured layout size (pixels), filled in by the editor via AnnotationRenderer.MeasureText.</summary>
public sealed record TextAnnotation(Color Color, Vector2 Position, string Text, float FontSize, Size Size) : Annotation(Color)
{
    public override Rect Bounds => new(Position.X, Position.Y, Math.Max(Size.Width, FontSize / 2), Math.Max(Size.Height, FontSize));
    public override bool HitTest(Vector2 p, float tol) => InRect(Bounds, p, tol);
    public override Annotation Offset(Vector2 d) => this with { Position = Position + d };
}

/// <summary>Pixelated region of the base image. Block = mosaic cell size in pixels.</summary>
public sealed record MosaicAnnotation(Rect Rect, int Block) : Annotation(Color.FromArgb(0, 0, 0, 0))
{
    public override Rect Bounds => Rect;
    public override bool HitTest(Vector2 p, float tol) => InRect(Rect, p, tol);
    public override Annotation Offset(Vector2 d) => this with { Rect = Move(Rect, d) };
    public override Annotation WithStyle(Color color, float? width) => this;
}

/// <summary>Filled circle with a step number.</summary>
public sealed record NumberAnnotation(Color Color, Vector2 Center, float Radius, int Number) : Annotation(Color)
{
    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
    public override bool HitTest(Vector2 p, float tol) => Vector2.Distance(p, Center) <= Radius + tol;
    public override Annotation Offset(Vector2 d) => this with { Center = Center + d };
}

/// <summary>The editable document: annotations (z-order = list order) + crop rectangle.</summary>
public sealed class EditorDocument
{
    private readonly List<Annotation> _items = new();

    public EditorDocument(int width, int height)
    {
        PixelWidth = width; PixelHeight = height;
        Crop = new Rect(0, 0, width, height);
    }

    public int PixelWidth { get; }
    public int PixelHeight { get; }
    /// <summary>Visible/exported region in original pixel coordinates (integral).</summary>
    public Rect Crop { get; internal set; }
    public IReadOnlyList<Annotation> Items => _items;

    public Annotation? Find(Guid id) => _items.FirstOrDefault(a => a.Id == id);
    public int IndexOf(Guid id) => _items.FindIndex(a => a.Id == id);
    internal void Insert(int index, Annotation a) => _items.Insert(Math.Clamp(index, 0, _items.Count), a);
    internal void RemoveAt(int index) => _items.RemoveAt(index);
    /// <summary>Replace in place (same Id). Also used for live previews while dragging (no undo entry).</summary>
    public void Replace(Annotation a) { var i = IndexOf(a.Id); if (i >= 0) _items[i] = a; }

    /// <summary>Topmost annotation under p (image pixels), or null.</summary>
    public Annotation? HitTest(Vector2 p, float tolerance)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
            if (_items[i].HitTest(p, tolerance)) return _items[i];
        return null;
    }

    public int NextNumber() => _items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Clamp r to the current crop and round outwards to whole pixels.</summary>
    public Rect ClampToCrop(Rect r)
    {
        var x0 = Math.Max(Crop.X, Math.Floor(r.X)); var y0 = Math.Max(Crop.Y, Math.Floor(r.Y));
        var x1 = Math.Min(Crop.X + Crop.Width, Math.Ceiling(r.X + r.Width)); var y1 = Math.Min(Crop.Y + Crop.Height, Math.Ceiling(r.Y + r.Height));
        return x1 <= x0 || y1 <= y0 ? Rect.Empty : new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}

// ---------------------------------------------------------------- undo / redo

public interface IEditCommand
{
    void Do(EditorDocument doc);
    void Undo(EditorDocument doc);
}

public sealed class AddCommand(Annotation item) : IEditCommand
{
    private int _index = -1;
    public void Do(EditorDocument doc) { if (_index < 0) _index = doc.Items.Count; doc.Insert(_index, item); }
    public void Undo(EditorDocument doc) { var i = doc.IndexOf(item.Id); if (i >= 0) doc.RemoveAt(i); }
}

public sealed class RemoveCommand(Annotation item) : IEditCommand
{
    private int _index;
    public void Do(EditorDocument doc) { _index = doc.IndexOf(item.Id); if (_index >= 0) doc.RemoveAt(_index); }
    public void Undo(EditorDocument doc) { if (_index >= 0) doc.Insert(_index, item); }
}

/// <summary>Move / restyle / re-text: swap the record with the same Id.</summary>
public sealed class ReplaceCommand(Annotation before, Annotation after) : IEditCommand
{
    public void Do(EditorDocument doc) => doc.Replace(after);
    public void Undo(EditorDocument doc) => doc.Replace(before);
}

public sealed class CropCommand(Rect before, Rect after) : IEditCommand
{
    public void Do(EditorDocument doc) => doc.Crop = after;
    public void Undo(EditorDocument doc) => doc.Crop = before;
}

public sealed class CommandStack(EditorDocument doc)
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();
    private IEditCommand? _savedAt;     // top of the undo stack at the last save (null = pristine)

    public event EventHandler? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty => (_undo.Count > 0 ? _undo.Peek() : null) != _savedAt;

    /// <summary>Run and record a command.</summary>
    public void Execute(IEditCommand cmd) { cmd.Do(doc); Record(cmd); }

    /// <summary>Record a command whose effect is already applied (e.g. a finished drag).</summary>
    public void Record(IEditCommand cmd) { _undo.Push(cmd); _redo.Clear(); Changed?.Invoke(this, EventArgs.Empty); }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var c = _undo.Pop(); c.Undo(doc); _redo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var c = _redo.Pop(); c.Do(doc); _undo.Push(c);
        Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    public void MarkSaved() { _savedAt = _undo.Count > 0 ? _undo.Peek() : null; Changed?.Invoke(this, EventArgs.Empty); }
}
