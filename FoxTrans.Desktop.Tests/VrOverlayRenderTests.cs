using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using System.Globalization;
using FoxTrans.Desktop.Controls;
using FoxTrans.Desktop.Models;
using FoxTrans.Desktop.Services;
using FoxTrans.Desktop.ViewModels;
using FoxTrans.Desktop.Views;
using FoxTrans.Desktop.Vr;
using Xunit;

public sealed class VrOverlayRenderTests
{
    [Fact]
    public void BgraPremultipliedConversionKeepsScalarAndVectorPathsIdentical()
    {
        byte[] bgra =
        [
            32, 64, 128, 128,
            0, 0, 0, 0,
            12, 34, 56, 255,
            7, 8, 9, 3
        ];
        byte[] scalar = new byte[bgra.Length];
        byte[] vector = new byte[bgra.Length];

        VrOverlaySurface.ConvertBgraPremultipliedToRgbaStraight(
            bgra, scalar, forceScalar: true);
        VrOverlaySurface.ConvertBgraPremultipliedToRgbaStraight(bgra, vector);

        Assert.Equal(scalar, vector);
        Assert.Equal(new byte[]
        {
            255, 128, 64, 128,
            0, 0, 0, 0,
            56, 34, 12, 255,
            255, 255, 255, 3
        }, scalar);
    }

    [AvaloniaFact]
    public void OverlayBindsRuntimeTranslationWithoutLoadedEvent()
    {
        PipelineViewDefinition definition =
            DesktopBootstrap.PlaceholderTopology();
        var viewModel = new VrOverlayViewModel(
            definition,
            null);
        var view = new VrOverlayView { DataContext = viewModel };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DesktopRuntimeSnapshot snapshot = DesktopRuntimeReducer.Reduce(
            DesktopRuntimeSnapshot.Create(definition, now),
            AppEvent.TranslationCompleted("Translated line"),
            now);

        viewModel.Apply(snapshot, null, now, animationSeconds: 0);

        VrOutlinedTextControl text =
            view.FindControl<VrOutlinedTextControl>("TranslationText")!;
        Assert.True(text.IsVisible);
        Assert.Equal("Translated line", text.Text);
    }

    [Fact]
    public void RuntimeTranslationEventReachesOverlayAndSettlesOnce()
    {
        PipelineViewDefinition definition =
            DesktopBootstrap.PlaceholderTopology();
        var viewModel = new VrOverlayViewModel(definition, null)
        {
            ReducedMotion = true
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DesktopRuntimeSnapshot snapshot = DesktopRuntimeReducer.Reduce(
            DesktopRuntimeSnapshot.Create(definition, now),
            AppEvent.TranslationCompleted("Runtime translation"),
            now);

        viewModel.Apply(snapshot, null, now, animationSeconds: 0);
        viewModel.AdvanceText(now);
        long settledRevision = viewModel.Revision;

        Assert.Equal("Runtime translation", viewModel.TranslationText);
        Assert.True(viewModel.HasTranslationText);

        viewModel.Apply(snapshot, null, now, animationSeconds: 0);
        viewModel.AdvanceText(now);

        Assert.Equal(settledRevision, viewModel.Revision);
    }

    [Fact]
    public void OverlayTranslationExactlyMatchesVrChatChatboxWindow()
    {
        PipelineViewDefinition definition =
            DesktopBootstrap.PlaceholderTopology();
        var viewModel = new VrOverlayViewModel(definition, null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string input = string.Concat(
            Enumerable.Repeat("e\u0301\U0001F642", 90));
        DesktopRuntimeSnapshot snapshot = DesktopRuntimeReducer.Reduce(
            DesktopRuntimeSnapshot.Create(definition, now),
            AppEvent.TranslationCompleted(input),
            now);

        viewModel.Apply(snapshot, null, now, animationSeconds: 0);

        Assert.Equal(VrChatTextFormatter.Format(input), viewModel.TranslationText);
        Assert.Equal(
            VrChatTextFormatter.MaximumTextElements,
            StringInfo.ParseCombiningCharacters(
                viewModel.TranslationText).Length);
    }

    [AvaloniaFact]
    public void OverlayRendersTranslationIntoTheTransparentHudFrame()
    {
        var viewModel = new VrOverlayViewModel(
            DesktopBootstrap.PlaceholderTopology(),
            null);
        var view = new VrOverlayView { DataContext = viewModel };
        var window = new Window
        {
            Width = VrOverlaySurface.Width,
            Height = VrOverlaySurface.Height,
            Content = view
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DesktopRuntimeSnapshot snapshot = DesktopRuntimeReducer.Reduce(
            DesktopRuntimeSnapshot.Create(
                DesktopBootstrap.PlaceholderTopology(),
                now),
            AppEvent.TranslationCompleted("Translated line"),
            now);
        viewModel.Apply(snapshot, null, now, animationSeconds: 0);

        using var surface = new VrOverlaySurface();
        ReadOnlyMemory<byte> rgba = surface.Render(view);

        Assert.True(HasVisiblePixel(
            rgba.Span,
            100,
            280,
            VrOverlaySurface.Width - 100,
            VrOverlaySurface.Height - 50));
        window.Close();
    }

    [AvaloniaFact]
    public void ListeningStatusKeepsRightSafeAreaWithoutWaveform()
    {
        PipelineViewDefinition definition =
            DesktopBootstrap.PlaceholderTopology();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DesktopRuntimeSnapshot listening = DesktopRuntimeReducer.Reduce(
            DesktopRuntimeSnapshot.Create(definition, now),
            AppEvent.Listening(),
            now);
        var viewModel = new VrOverlayViewModel(definition, null);
        var view = new VrOverlayView { DataContext = viewModel };
        var window = new Window
        {
            Width = VrOverlaySurface.LogicalWidth,
            Height = VrOverlaySurface.LogicalHeight,
            Content = view
        };
        using var surface = new VrOverlaySurface();

        viewModel.Apply(listening, null, now, animationSeconds: 0);
        ReadOnlyMemory<byte> rgba = surface.Render(view);

        Assert.Equal(
            "Listening",
            view.FindControl<VrOutlinedTextControl>("VoiceStatusText")!.Text);
        Assert.False(HasVisiblePixel(
            rgba.Span,
            VrOverlaySurface.Width - 48,
            0,
            VrOverlaySurface.Width,
            VrOverlaySurface.Height));
        Assert.Null(view.FindControl<VoiceWaveformControl>("VoiceWaveform"));
        window.Close();
    }

    [AvaloniaFact]
    public void AudioOnlyChangesNeverSubmitAnotherHudTexture()
    {
        PipelineViewDefinition definition =
            DesktopBootstrap.PlaceholderTopology();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DesktopRuntimeSnapshot snapshot =
            DesktopRuntimeSnapshot.Create(definition, now);
        RuntimeSample sample = new(snapshot, null);
        var device = new FakeVrOverlayDevice();
        using var supervisor = new VrOverlaySupervisor(device);
        using var host = new VrOverlayHost(
            definition,
            null,
            _ => sample,
            () => new DesktopPreferences(),
            supervisor);

        host.Start();
        int firstSubmitCount = device.SubmitCount;
        sample = new(
            snapshot,
            new AudioVisualFrame(
                0.4f,
                0.7f,
                false,
                true,
                new float[12],
                1,
                now));
        host.RenderTick();

        Assert.Equal(1, firstSubmitCount);
        Assert.Equal(firstSubmitCount, device.SubmitCount);
    }

    [AvaloniaFact]
    public void OverlayResolvesDesignSystemResourcesAndModuleToggles()
    {
        var viewModel = new VrOverlayViewModel(
            DesktopBootstrap.PlaceholderTopology(),
            null)
        {
            PipelineName = "Voxtral + LLM",
            ModelLine = "Test model"
        };
        var view = new VrOverlayView { DataContext = viewModel };
        var window = new Window
        {
            Width = VrOverlaySurface.Width,
            Height = VrOverlaySurface.Height,
            Content = view
        };
        using var surface = new VrOverlaySurface();
        ReadOnlyMemory<byte> rgba = surface.Render(view);
        Assert.Equal(VrOverlaySurface.Width * VrOverlaySurface.Height * 4,
            rgba.Length);
        Assert.True(window.TryFindResource("Brush.TextPrimary", out object? value));
        Color expected = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
        int sample = (200 * VrOverlaySurface.Width + 12) * 4;
        Assert.Equal(0, rgba.Span[sample]);
        Assert.Equal(0, rgba.Span[sample + 1]);
        Assert.Equal(0, rgba.Span[sample + 2]);
        Assert.Equal(0, rgba.Span[sample + 3]);
        Assert.True(expected.A > 0);
        VrOutlinedTextControl brand =
            view.FindControl<VrOutlinedTextControl>("OverlayBrand")!;
        Assert.Equal("FoxTrans Overlay", brand.Text);
        Assert.Equal(
            "Voxtral + LLM",
            view.FindControl<VrOutlinedTextControl>("OverlayPipeline")!.Text);
        VrOutlinedTextControl pipeline =
            view.FindControl<VrOutlinedTextControl>("OverlayPipeline")!;
        VrOutlinedTextControl models =
            view.FindControl<VrOutlinedTextControl>("OverlayModels")!;
        Assert.Equal("Test model", models.Text);
        Assert.Equal(12, models.TextSize);
        Point brandOrigin = brand.TranslatePoint(default, view)!.Value;
        Point pipelineOrigin = pipeline.TranslatePoint(default, view)!.Value;
        Point modelsOrigin = models.TranslatePoint(default, view)!.Value;
        Assert.True(
            ControlHasVisiblePixel(rgba.Span, brand, brandOrigin),
            $"Brand did not render: origin={brandOrigin}, " +
            $"bounds={brand.Bounds}, visible={brand.IsVisible}, " +
            $"fill={brand.Fill}, outline={brand.Outline}, " +
            $"rows={VisibleRowSummary(rgba.Span)}, " +
            $"topColumns={VisibleColumnRange(rgba.Span, 0, 160)}");
        Assert.InRange(pipelineOrigin.Y - brandOrigin.Y, 28, 34);
        Assert.InRange(modelsOrigin.Y - pipelineOrigin.Y, 16, 22);
        VrOutlinedTextControl translation =
            view.FindControl<VrOutlinedTextControl>("TranslationText")!;
        Assert.Equal(40, translation.TextSize);
        Assert.Equal(0.65, translation.OutlineThickness);
        Assert.Contains(
            "Segoe UI",
            translation.TextFontFamily.ToString(),
            StringComparison.Ordinal);
        Point? translationOrigin =
            translation.TranslatePoint(default, view);
        Assert.NotNull(translationOrigin);
        Assert.True(translationOrigin.Value.Y > 290);
        Assert.False(HasVisiblePixel(
            rgba.Span,
            0,
            0,
            48,
            VrOverlaySurface.Height));
        Assert.False(HasVisiblePixel(
            rgba.Span,
            VrOverlaySurface.Width - 48,
            0,
            VrOverlaySurface.Width,
            VrOverlaySurface.Height));

        viewModel.ShowHeader = false;
        viewModel.ShowVoiceStatus = false;
        viewModel.ShowRecognition = false;
        viewModel.ShowTranslation = false;

        Assert.False(view.FindControl<StackPanel>("OverlayHeader")!.IsVisible);
        Assert.False(view.FindControl<StackPanel>("VoiceStatus")!.IsVisible);
        Assert.False(view.FindControl<Grid>("RecognitionSection")!.IsVisible);
        Assert.False(view.FindControl<Grid>("TranslationSection")!.IsVisible);
        window.Close();
    }

    [Fact]
    public void OverlayStatePollingHonoursConfiguredBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(1d / 12),
            VrOverlayHost.StatePollIntervalFor(30));
        Assert.Equal(TimeSpan.FromSeconds(1d / 10),
            VrOverlayHost.StatePollIntervalFor(10));
        Assert.Equal(TimeSpan.FromSeconds(1d / 5),
            VrOverlayHost.StatePollIntervalFor(5));
    }

    private static bool HasVisiblePixel(
        ReadOnlySpan<byte> rgba,
        int left,
        int top,
        int right,
        int bottom)
    {
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (rgba[(y * VrOverlaySurface.Width + x) * 4 + 3] != 0)
                    return true;
            }
        }
        return false;
    }

    private static bool ControlHasVisiblePixel(
        ReadOnlySpan<byte> rgba,
        Control control,
        Point logicalOrigin)
    {
        const double scale = (double)VrOverlaySurface.Width /
            VrOverlaySurface.LogicalWidth;
        return HasVisiblePixel(
            rgba,
            (int)Math.Floor(logicalOrigin.X * scale),
            (int)Math.Floor(logicalOrigin.Y * scale),
            (int)Math.Ceiling(
                (logicalOrigin.X + control.Bounds.Width) * scale),
            (int)Math.Ceiling(
                (logicalOrigin.Y + control.Bounds.Height) * scale));
    }

    private static string VisibleRowSummary(ReadOnlySpan<byte> rgba)
    {
        var ranges = new List<string>();
        int start = -1;
        for (int y = 0; y < VrOverlaySurface.Height; y++)
        {
            bool visible = HasVisiblePixel(
                rgba, 0, y, VrOverlaySurface.Width, y + 1);
            if (visible && start < 0)
                start = y;
            if (!visible && start >= 0)
            {
                ranges.Add($"{start}-{y - 1}");
                start = -1;
            }
        }
        if (start >= 0)
            ranges.Add($"{start}-{VrOverlaySurface.Height - 1}");
        return string.Join(",", ranges);
    }

    private static string VisibleColumnRange(
        ReadOnlySpan<byte> rgba,
        int top,
        int bottom)
    {
        int first = -1;
        int last = -1;
        for (int x = 0; x < VrOverlaySurface.Width; x++)
        {
            if (!HasVisiblePixel(rgba, x, top, x + 1, bottom))
                continue;
            first = first < 0 ? x : first;
            last = x;
        }
        return $"{first}-{last}";
    }

    private sealed class FakeVrOverlayDevice : IVrOverlayDevice
    {
        public int SubmitCount { get; private set; }

        public bool IsAvailable() => true;

        public int Open(string key, string name, out nint overlay)
        {
            overlay = (nint)1;
            return 0;
        }

        public int Submit(
            nint overlay,
            ReadOnlySpan<byte> rgba,
            uint width,
            uint height)
        {
            SubmitCount++;
            return 0;
        }

        public int SetPlacement(
            nint overlay,
            VrOverlayPlacement placement) => 0;

        public int SetVisible(nint overlay, bool visible) => 0;

        public int Poll(nint overlay, out bool shouldQuit)
        {
            shouldQuit = false;
            return 0;
        }

        public void Close(nint overlay)
        {
        }

        public string DescribeResult(int result) => result.ToString();
    }
}
