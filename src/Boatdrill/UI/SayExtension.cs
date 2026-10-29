using System.Windows.Markup;
using Boatdrill.Core.Text;

namespace Boatdrill.UI;

/// <summary>
/// Words in XAML in both languages, the English first: <c>Text="{ui:Say 'Run a drill now', '지금 복원 훈련하기'}"</c>.
/// They are read when the view loads, in the language Boatdrill speaks then; changing the language builds
/// the window again. Quote with single quotes, and escape a quote inside with a backslash.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class SayExtension : MarkupExtension
{
    public SayExtension()
    {
    }

    public SayExtension(string english, string korean)
    {
        English = english;
        Korean = korean;
    }

    [ConstructorArgument("english")]
    public string English { get; set; } = string.Empty;

    [ConstructorArgument("korean")]
    public string Korean { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Language.Pick(English, Korean);
}
