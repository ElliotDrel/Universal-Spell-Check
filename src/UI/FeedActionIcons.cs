using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfBinding = System.Windows.Data.Binding;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace UniversalSpellCheck.UI;

internal static class FeedActionIcons
{
    private const double CanvasSize = 16;

    public static Viewbox Copy() => Wrap(CreateCopyIcon());

    public static Viewbox Check() => Wrap(CreateCheckIcon());

    public static Viewbox Clock() => Wrap(CreateClockIcon());

    public static Viewbox MoreVertical() => Wrap(CreateMoreIcon());

    private static Viewbox Wrap(UIElement content)
    {
        return new Viewbox
        {
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Child = content
        };
    }

    private static void BindStroke(Shape shape)
    {
        shape.SetBinding(Shape.StrokeProperty, new WpfBinding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(WpfButton), 1)
        });
    }

    private static void BindFill(Shape shape)
    {
        shape.SetBinding(Shape.FillProperty, new WpfBinding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(WpfButton), 1)
        });
    }

    private static Canvas CreateCopyIcon()
    {
        var canvas = new Canvas { Width = CanvasSize, Height = CanvasSize };

        var back = new WpfRectangle
        {
            Width = 9,
            Height = 9,
            RadiusX = 1.5,
            RadiusY = 1.5,
            StrokeThickness = 1.25,
            Fill = WpfBrushes.Transparent
        };
        Canvas.SetLeft(back, 1);
        Canvas.SetTop(back, 4);
        BindStroke(back);

        var front = new WpfRectangle
        {
            Width = 9,
            Height = 9,
            RadiusX = 1.5,
            RadiusY = 1.5,
            StrokeThickness = 1.25,
            Fill = WpfBrushes.Transparent
        };
        Canvas.SetLeft(front, 5);
        Canvas.SetTop(front, 1);
        BindStroke(front);

        canvas.Children.Add(back);
        canvas.Children.Add(front);
        return canvas;
    }

    private static Canvas CreateCheckIcon()
    {
        var canvas = new Canvas { Width = CanvasSize, Height = CanvasSize };

        var check = new Polyline
        {
            Points = new PointCollection
            {
                new(3.5, 8.5),
                new(6.5, 11.5),
                new(12.5, 4.5)
            },
            StrokeThickness = 1.5,
            Fill = WpfBrushes.Transparent,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        BindStroke(check);
        canvas.Children.Add(check);
        return canvas;
    }

    private static Canvas CreateClockIcon()
    {
        var canvas = new Canvas { Width = CanvasSize, Height = CanvasSize };
        var face = new Ellipse
        {
            Width = 12,
            Height = 12,
            StrokeThickness = 1.25,
            Fill = WpfBrushes.Transparent
        };
        Canvas.SetLeft(face, 2);
        Canvas.SetTop(face, 2);
        BindStroke(face);

        var hands = new Polyline
        {
            Points = new PointCollection
            {
                new(8, 4.75),
                new(8, 8),
                new(10.5, 9.5)
            },
            StrokeThickness = 1.25,
            Fill = WpfBrushes.Transparent,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        BindStroke(hands);

        canvas.Children.Add(face);
        canvas.Children.Add(hands);
        return canvas;
    }

    private static Canvas CreateMoreIcon()
    {
        var canvas = new Canvas { Width = CanvasSize, Height = CanvasSize };

        foreach (var top in new[] { 3.5, 8.0, 12.5 })
        {
            var dot = new Ellipse
            {
                Width = 2,
                Height = 2
            };
            Canvas.SetLeft(dot, 7);
            Canvas.SetTop(dot, top);
            BindFill(dot);
            canvas.Children.Add(dot);
        }

        return canvas;
    }
}
