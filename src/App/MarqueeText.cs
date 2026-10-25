using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace ArnoA2.App;

/// <summary>
/// One line of text that scrolls sideways when it is wider than its space: it pauses on the
/// start, glides left until the end is visible, pauses, and starts over. Text that fits is
/// shown centred and still.
/// </summary>
public sealed class MarqueeText : Control
{
    const double Speed = 30, Pause = 1.6; // pixels per second, seconds
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    string text = "";
    FormattedText? format;
    double offset, waited;
    bool atEnd;
    DateTime lastTick = DateTime.UtcNow;

    public MarqueeText()
    {
        ClipToBounds = true;
        timer.Tick += (_, _) =>
        {
            // Real elapsed time, so the speed holds even when ticks arrive late.
            var now = DateTime.UtcNow;
            Advance(Math.Clamp((now - lastTick).TotalSeconds, 0, 0.25));
            lastTick = now;
        };
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }

    /// <summary>How far the text has scrolled (0 when it fits).</summary>
    public double Offset => offset;
    public bool Scrolls => Overflow > 0.5;

    public IBrush Foreground { get; set; } = Brushes.White;
    public double FontSize { get; set; } = 11.5;

    public string Text
    {
        get => text;
        set
        {
            value ??= "";
            if (value == text)
                return;
            // The scroll carries on: the guidance changes with every step (its distance),
            // and restarting each time would keep it parked on the start forever.
            text = value;
            format = null;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    FormattedText Format() => format ??= new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, FontSize, Foreground);

    protected override Size MeasureOverride(Size available)
    {
        var f = Format();
        return new Size(Math.Min(f.Width, double.IsInfinity(available.Width) ? f.Width : available.Width), f.Height);
    }

    double Overflow => Math.Max(0, Format().Width - Bounds.Width);

    /// <summary>Move the scroll on by this many seconds.</summary>
    public void Advance(double dt)
    {
        if (Overflow <= 0.5)
        {
            offset = 0;
            return;
        }

        offset = Math.Min(offset, Overflow);
        if (waited < Pause)
        {
            waited += dt;
            return;
        }

        if (atEnd)
        {
            // Back to the start after the end has been shown.
            offset = 0;
            atEnd = false;
            waited = 0;
        }
        else
        {
            offset = Math.Min(Overflow, offset + Speed * dt);
            if (offset >= Overflow)
            {
                atEnd = true;
                waited = 0;
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var f = Format();
        double y = (Bounds.Height - f.Height) / 2;
        double x = Overflow <= 0.5 ? (Bounds.Width - f.Width) / 2 : -offset;
        context.DrawText(f, new Point(x, y));
    }
}
