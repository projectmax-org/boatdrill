using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Boatdrill.Core.Text;

namespace Boatdrill.UI;

/// <summary>
/// Keeps Korean words whole where text wraps.
/// <para>
/// WPF breaks a line between any two Hangul syllables, which leaves "복원할 수 있습니" on one line and "다." on
/// the next. Korean is set here as the HTML report sets it with <c>word-break: keep-all</c>: broken at spaces.
/// What a wrapping TextBlock shows gets a word joiner, which has no width, between the syllables of each word
/// (<see cref="Korean.KeepWordsWhole"/>). The text it is bound to stays as it was, a screen reader is given the
/// words as written, and a word too long for a line is still broken, as wrapping does with any long word.
/// </para>
/// <para>
/// The text styles in Controls.xaml switch it on for every TextBlock; one that does not wrap is left alone.
/// </para>
/// </summary>
public static class KoreanWrap
{
    public static readonly DependencyProperty KeepWordsProperty = DependencyProperty.RegisterAttached(
        "KeepWords", typeof(bool), typeof(KoreanWrap), new PropertyMetadata(false, OnKeepWordsChanged));

    /// <summary>The TextBlock's own Text, watched through a binding, so that a new sentence is joined as it arrives.</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(string), typeof(KoreanWrap), new PropertyMetadata(null, OnShownChanged));

    /// <summary>Set when the automation name on a TextBlock is the one given here, and so is this class's to change.</summary>
    private static readonly DependencyProperty NamedProperty = DependencyProperty.RegisterAttached(
        "Named", typeof(bool), typeof(KoreanWrap), new PropertyMetadata(false));

    public static bool GetKeepWords(DependencyObject element) => (bool)element.GetValue(KeepWordsProperty);

    public static void SetKeepWords(DependencyObject element, bool value) => element.SetValue(KeepWordsProperty, value);

    private static void OnKeepWordsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;

        block.Loaded -= OnLoaded;
        if (e.NewValue is true)
        {
            // Whether it wraps is known once the element is built; a style may set this before TextWrapping.
            if (block.IsLoaded) Watch(block);
            else block.Loaded += OnLoaded;
        }
        else
        {
            BindingOperations.ClearBinding(block, ShownProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var block = (TextBlock)sender;
        block.Loaded -= OnLoaded;
        Watch(block);
    }

    private static void Watch(TextBlock block)
    {
        if (block.TextWrapping == TextWrapping.NoWrap || BindingOperations.IsDataBound(block, ShownProperty)) return;

        // Text made of Runs, bold words or links has no Text of its own to join, and writing one would flatten it.
        if (!BindingOperations.IsDataBound(block, TextBlock.TextProperty) && block.ReadLocalValue(TextBlock.TextProperty) == DependencyProperty.UnsetValue) return;

        BindingOperations.SetBinding(block, ShownProperty, new Binding(nameof(TextBlock.Text))
        {
            RelativeSource = RelativeSource.Self,
            Mode = BindingMode.OneWay,
        });
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block || e.NewValue is not string shown) return;

        var written = Korean.WithoutJoiners(shown);
        var joined = Korean.KeepWordsWhole(written);
        var named = (bool)block.GetValue(NamedProperty);
        if (joined.Length == written.Length)
        {
            // No Korean word in it: nothing to keep together, and nothing to say differently.
            if (named)
            {
                block.ClearValue(AutomationProperties.NameProperty);
                block.ClearValue(NamedProperty);
            }

            return;
        }

        if (named || block.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
        {
            AutomationProperties.SetName(block, written);
            block.SetValue(NamedProperty, true);
        }

        // The current value, so a binding on Text stays in place and the next sentence it brings is joined in turn.
        if (!string.Equals(shown, joined, StringComparison.Ordinal)) block.SetCurrentValue(TextBlock.TextProperty, joined);
    }
}
