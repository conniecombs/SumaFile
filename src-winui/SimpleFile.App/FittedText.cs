using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SimpleFile.Core;
using Windows.Foundation;

namespace SimpleFile.App;

/// <summary>Where <see cref="FittedText"/> shortens text that is too wide.</summary>
public enum FittedTextKind
{
    /// <summary>At the end.</summary>
    End,
    /// <summary>In the middle, keeping the end of the name and its extension.</summary>
    FileName,
    /// <summary>In the middle, keeping the root and the last folders.</summary>
    FolderPath,
}

/// <summary>
/// One line of text, shortened with "…" to the width it is given. WinUI's own trimming cuts only at the end,
/// which loses a file's extension and the folder a path leads to. <see cref="Suffix"/> is never shortened, so
/// "long na…me.mkv and 7 more files" keeps its count. The rules are in <see cref="TextFit"/>; this control only
/// measures. Font and colour set on the control reach the text by inheritance.
/// </summary>
public sealed class FittedText : UserControl
{
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.NoWrap,
        // A guard only: the text is already shortened to fit when this could apply.
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    // The last result, so a repeated layout pass at the same width sets the same text and layout settles.
    private (string Text, string Suffix, FittedTextKind Kind, double Width, string Shown)? _fitted;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FittedText), new PropertyMetadata("", OnTextChanged));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(FittedText), new PropertyMetadata("", OnTextChanged));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(FittedTextKind), typeof(FittedText), new PropertyMetadata(FittedTextKind.End, OnTextChanged));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public FittedTextKind Kind { get => (FittedTextKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public FittedText()
    {
        IsTabStop = false;
        Content = _text;
        // Empty until a text arrives; see OnTextChanged.
        Visibility = Visibility.Collapsed;
    }

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var control = (FittedText)owner;
        var whole = (control.Text ?? "") + (control.Suffix ?? "");
        // A screen reader hears the whole text, never the shortened one.
        AutomationProperties.SetName(control._text, whole);
        // No text, no line: an empty folder heading must not leave a gap.
        control.Visibility = whole.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        control.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Fit(availableSize.Width - Padding.Left - Padding.Right);
        if (!string.Equals(_text.Text, shown, StringComparison.Ordinal))
        {
            _text.Text = shown;
        }

        return base.MeasureOverride(availableSize);
    }

    private string Fit(double width)
    {
        var text = Text ?? "";
        var suffix = Suffix ?? "";
        // Unlimited width (an Auto column, a horizontal stack) or none at all: nothing to fit to.
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return text + suffix;
        }

        var kind = Kind;
        if (_fitted is { } last && last.Width == width && last.Kind == kind
            && string.Equals(last.Text, text, StringComparison.Ordinal)
            && string.Equals(last.Suffix, suffix, StringComparison.Ordinal))
        {
            return last.Shown;
        }

        bool Fits(string candidate) => WidthOf(candidate + suffix) <= width;
        var shown = kind switch
        {
            FittedTextKind.FileName => TextFit.FileName(text, Fits),
            FittedTextKind.FolderPath => TextFit.FolderPath(text, Fits),
            _ => TextFit.End(text, Fits),
        } + suffix;
        _fitted = (text, suffix, kind, width, shown);
        return shown;
    }

    /// <summary>The width the line would take with this text, measured on the text block that shows it.</summary>
    private double WidthOf(string candidate)
    {
        _text.Text = candidate;
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _text.DesiredSize.Width;
    }
}
