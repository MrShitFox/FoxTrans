using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FoxTrans.Desktop.ViewModels;

namespace FoxTrans.Desktop.Views;

public sealed partial class StreamingTextPresenter : UserControl
{
    private readonly Run _stableRun = new();
    private readonly Run _stableSuffixSpacer = new();
    private readonly Run _transparentPrefix = new();
    private readonly Run _animatedSuffix = new();
    private readonly TextBlock _stableLayer;
    private readonly TextBlock _suffixLayer;
    private StreamingTextViewModel? _viewModel;

    public static readonly StyledProperty<double> TextSizeProperty =
        AvaloniaProperty.Register<StreamingTextPresenter, double>(
            nameof(TextSize),
            20);
    public static readonly StyledProperty<double> TextLineHeightProperty =
        AvaloniaProperty.Register<StreamingTextPresenter, double>(
            nameof(TextLineHeight),
            29);
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<StreamingTextPresenter, TextAlignment>(
            nameof(TextAlignment),
            TextAlignment.Center);
    public static readonly StyledProperty<IBrush> TextBrushProperty =
        AvaloniaProperty.Register<StreamingTextPresenter, IBrush>(
            nameof(TextBrush),
            Brushes.White);

    public StreamingTextPresenter()
    {
        AvaloniaXamlLoader.Load(this);
        _stableLayer = this.FindControl<TextBlock>("StableLayer")!;
        _suffixLayer = this.FindControl<TextBlock>("SuffixLayer")!;
        _stableLayer.Inlines!.Add(_stableRun);
        _stableLayer.Inlines.Add(_stableSuffixSpacer);
        _suffixLayer.Inlines!.Add(_transparentPrefix);
        _suffixLayer.Inlines.Add(_animatedSuffix);
        DataContextChanged += (_, _) => AttachViewModel();
        Loaded += (_, _) => AttachViewModel();
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == ForegroundProperty)
                UpdateRuns();
            if (eventArgs.Property == TextBrushProperty)
                UpdateRuns();
        };
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

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public IBrush TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    private void AttachViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as StreamingTextViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateRuns();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is
            nameof(StreamingTextViewModel.StableText) or
            nameof(StreamingTextViewModel.AnimatedSuffixText))
        {
            UpdateRuns();
        }
    }

    private void UpdateRuns()
    {
        string stable = _viewModel?.StableText ?? "";
        string suffix = _viewModel?.AnimatedSuffixText ?? "";
        IBrush foreground = TextBrush;
        _stableRun.Text = stable;
        _stableRun.Foreground = foreground;
        _stableSuffixSpacer.Text = suffix;
        _stableSuffixSpacer.Foreground = Brushes.Transparent;
        _transparentPrefix.Text = stable;
        _transparentPrefix.Foreground = Brushes.Transparent;
        _animatedSuffix.Text = suffix;
        _animatedSuffix.Foreground = foreground;
        _suffixLayer.IsVisible = suffix.Length > 0;
    }
}
