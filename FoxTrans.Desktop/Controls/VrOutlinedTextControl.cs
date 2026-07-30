using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FoxTrans.Desktop.Controls;

/// <summary>
/// Geometry-backed text for a transparent VR surface. The rounded outline is
/// part of the glyph render, so it remains legible against both bright and
/// dark worlds without a panel behind it.
/// </summary>
public sealed class VrOutlinedTextControl : Control
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, string>(
            nameof(Text),
            "");
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, IBrush?>(
            nameof(Fill));
    public static readonly StyledProperty<IBrush?> OutlineProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, IBrush?>(
            nameof(Outline));
    public static readonly StyledProperty<double> OutlineThicknessProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, double>(
            nameof(OutlineThickness),
            2);
    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, double>(
            nameof(TextSize),
            24);
    public static readonly StyledProperty<double> TextLineHeightProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, double>(
            nameof(TextLineHeight),
            0);
    public static readonly StyledProperty<FontWeight> TextWeightProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, FontWeight>(
            nameof(TextWeight),
            FontWeight.Normal);
    public static readonly StyledProperty<int> MaximumLinesProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, int>(
            nameof(MaximumLines),
            2);
    public static readonly StyledProperty<FontFamily> TextFontFamilyProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, FontFamily>(
            nameof(TextFontFamily),
            new FontFamily("Segoe UI Variable, Segoe UI, sans-serif"));
    public static readonly StyledProperty<TextAlignment> HorizontalTextAlignmentProperty =
        AvaloniaProperty.Register<VrOutlinedTextControl, TextAlignment>(
            nameof(HorizontalTextAlignment),
            TextAlignment.Center);

    static VrOutlinedTextControl()
    {
        AffectsMeasure<VrOutlinedTextControl>(
            TextProperty,
            OutlineThicknessProperty,
            TextSizeProperty,
            TextLineHeightProperty,
            TextWeightProperty,
            TextFontFamilyProperty,
            MaximumLinesProperty,
            HorizontalTextAlignmentProperty);
        AffectsRender<VrOutlinedTextControl>(
            TextProperty,
            FillProperty,
            OutlineProperty,
            OutlineThicknessProperty,
            TextSizeProperty,
            TextLineHeightProperty,
            TextWeightProperty,
            TextFontFamilyProperty,
            MaximumLinesProperty,
            HorizontalTextAlignmentProperty);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Outline
    {
        get => GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    public double OutlineThickness
    {
        get => GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public double TextLineHeight
    {
        get => GetValue(TextLineHeightProperty);
        set => SetValue(TextLineHeightProperty, value);
    }

    public FontWeight TextWeight
    {
        get => GetValue(TextWeightProperty);
        set => SetValue(TextWeightProperty, value);
    }

    public int MaximumLines
    {
        get => GetValue(MaximumLinesProperty);
        set => SetValue(MaximumLinesProperty, value);
    }

    public FontFamily TextFontFamily
    {
        get => GetValue(TextFontFamilyProperty);
        set => SetValue(TextFontFamilyProperty, value);
    }

    public TextAlignment HorizontalTextAlignment
    {
        get => GetValue(HorizontalTextAlignmentProperty);
        set => SetValue(HorizontalTextAlignmentProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double inset = Inset;
        double width = double.IsFinite(availableSize.Width)
            ? Math.Max(1, availableSize.Width - inset * 2)
            : 4096;
        double height = double.IsFinite(availableSize.Height)
            ? Math.Max(1, availableSize.Height - inset * 2)
            : 4096;
        FormattedText formatted = Format(width, height);
        return new(
            Math.Min(width, formatted.Width) + inset * 2,
            Math.Min(height, formatted.Height) + inset * 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (string.IsNullOrWhiteSpace(Text) ||
            Bounds.Width <= 0 ||
            Bounds.Height <= 0 ||
            Fill is null)
        {
            return;
        }

        double inset = Inset;
        FormattedText formatted = Format(
            Math.Max(1, Bounds.Width - inset * 2),
            Math.Max(1, Bounds.Height - inset * 2));
        double y = Math.Max(
            inset,
            (Bounds.Height - formatted.Height) / 2);
        Geometry? geometry = formatted.BuildGeometry(new Point(inset, y));
        if (geometry is null)
            return;
        Pen? outline = Outline is null || OutlineThickness <= 0
            ? null
            : new Pen(
                Outline,
                OutlineThickness,
                null,
                PenLineCap.Round,
                PenLineJoin.Round,
                2);
        context.DrawGeometry(Fill, outline, geometry);
    }

    private double Inset => Math.Max(4, OutlineThickness + 3);

    private FormattedText Format(double width, double height)
    {
        var formatted = new FormattedText(
            Text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                TextFontFamily,
                FontStyle.Normal,
                TextWeight,
                FontStretch.Normal),
            TextSize,
            Fill ?? Brushes.White)
        {
            TextAlignment = HorizontalTextAlignment,
            MaxTextWidth = width,
            MaxTextHeight = height,
            MaxLineCount = Math.Max(1, MaximumLines),
            Trimming = TextTrimming.CharacterEllipsis
        };
        if (TextLineHeight > 0)
            formatted.LineHeight = TextLineHeight;
        return formatted;
    }
}
