namespace LinguaPath.Mobile.Controls;

/// <summary>
/// Circular progress indicator drawn with Microsoft.Maui.Graphics. Progress is 0–1; changes animate smoothly.
/// Put a Grid around it to overlay labels in the centre. Screen readers get the percentage through SemanticProperties.
/// </summary>
public sealed class ProgressRing : GraphicsView, IDrawable
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
        nameof(Progress), typeof(double), typeof(ProgressRing), 0.0, propertyChanged: OnProgressChanged);

    public static readonly BindableProperty RingColorProperty = BindableProperty.Create(
        nameof(RingColor), typeof(Color), typeof(ProgressRing), Color.FromArgb("#6D28D9"), propertyChanged: Redraw);

    public static readonly BindableProperty TrackColorProperty = BindableProperty.Create(
        nameof(TrackColor), typeof(Color), typeof(ProgressRing), Color.FromArgb("#E4E7F0"), propertyChanged: Redraw);

    public static readonly BindableProperty ThicknessProperty = BindableProperty.Create(
        nameof(Thickness), typeof(double), typeof(ProgressRing), 10.0, propertyChanged: Redraw);

    private double _shown;

    public ProgressRing()
    {
        Drawable = this;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
    }

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Color RingColor
    {
        get => (Color)GetValue(RingColorProperty);
        set => SetValue(RingColorProperty, value);
    }

    public Color TrackColor
    {
        get => (Color)GetValue(TrackColorProperty);
        set => SetValue(TrackColorProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static void Redraw(BindableObject b, object o, object n) => ((ProgressRing)b).Invalidate();

    private static void OnProgressChanged(BindableObject b, object oldValue, object newValue)
    {
        var ring = (ProgressRing)b;
        var target = Math.Clamp(newValue is double d && !double.IsNaN(d) ? d : 0, 0, 1);
        SemanticProperties.SetDescription(ring, $"{Math.Round(target * 100)}%");
        var start = ring._shown;
        ring.AbortAnimation("ring");
        ring.Animate("ring", t =>
        {
            ring._shown = start + (target - start) * t;
            ring.Invalidate();
        }, length: 700, easing: Easing.CubicOut);
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var stroke = (float)Thickness;
        var size = Math.Min(dirtyRect.Width, dirtyRect.Height) - stroke;
        if (size <= 0) return;
        var x = dirtyRect.Center.X - size / 2;
        var y = dirtyRect.Center.Y - size / 2;

        canvas.StrokeSize = stroke;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeColor = TrackColor;
        canvas.DrawEllipse(x, y, size, size);

        var p = Math.Clamp(_shown, 0, 1);
        if (p <= 0.001) return;

        canvas.StrokeColor = RingColor;
        if (p >= 0.999)
        {
            canvas.DrawEllipse(x, y, size, size);
            return;
        }
        // 0° is 3 o'clock and angles grow anticlockwise: start at 12 o'clock and sweep clockwise.
        canvas.DrawArc(x, y, size, size, 90, (float)(90 - 360 * p), clockwise: true, closed: false);
    }
}
