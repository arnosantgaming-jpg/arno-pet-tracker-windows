using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ArnoA2.App;

/// <summary>
/// A compact slider drawn to fit small overlays: a thin track and a gold knob, both
/// centred vertically, with the knob always inside the control's bounds. Drag, click
/// or scroll to change the value.
/// </summary>
public sealed class GoldSlider : Control
{
    const double Knob = 7, TrackHeight = 4;
    static readonly IBrush Track = Brush.Parse("#3A4656");
    static readonly IBrush Fill = Brush.Parse("#C9A227");
    static readonly IBrush KnobBrush = Brush.Parse("#FFD54F");
    static readonly IPen KnobEdge = new Pen(Brush.Parse("#0B1118"), 1.5);
    double value;
    bool dragging;

    public GoldSlider()
    {
        Height = 2 * Knob + 4;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public event Action<double>? ValueChanged;

    public double Value
    {
        get => value;
        set
        {
            double next = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(next - this.value) < 1e-9)
                return;
            this.value = next;
            InvalidateVisual();
            ValueChanged?.Invoke(next);
        }
    }

    // The knob's centre travels between Knob and Width − Knob, so it never leaves the control.
    double Left => Knob;
    double Right => Math.Max(Knob, Bounds.Width - Knob);
    double X => Left + (Right - Left) * (Maximum > Minimum ? (value - Minimum) / (Maximum - Minimum) : 0);

    public override void Render(DrawingContext context)
    {
        double y = Bounds.Height / 2;
        context.DrawRectangle(Track, null, new Rect(Left, y - TrackHeight / 2, Right - Left, TrackHeight), 2, 2);
        context.DrawRectangle(Fill, null, new Rect(Left, y - TrackHeight / 2, X - Left, TrackHeight), 2, 2);
        context.DrawEllipse(KnobBrush, KnobEdge, new Point(X, y), Knob - 1, Knob - 1);
    }

    void Seek(Point p) => Value = Minimum + (Maximum - Minimum) * Math.Clamp((p.X - Left) / Math.Max(1, Right - Left), 0, 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        dragging = true;
        e.Pointer.Capture(this);
        Seek(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging)
            Seek(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Value += (Maximum - Minimum) * 0.05 * Math.Sign(e.Delta.Y);
        e.Handled = true;
    }
}
