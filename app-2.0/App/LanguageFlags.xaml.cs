using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DaBtDynamicLock.App;

/// <summary>
/// The two flags that pick the language, wherever the choice is offered.
///
/// It does NOT change the language itself. Whoever hosts it decides what that
/// means, because the answer differs: the settings window stores it in the
/// user's settings, the installer only remembers it until the install is done
/// and then hands it to the app. Setting it here would have made this control
/// know about settings files, which is not its business.
/// </summary>
public sealed partial class LanguageFlags : UserControl
{
    /// <summary>Raised with "cs" or "en" when a different flag is pressed.</summary>
    public event Action<string>? LanguagePicked;

    public LanguageFlags()
    {
        InitializeComponent();
        Mark();
    }

    /// <summary>
    /// Marks the language in use with the bar under its flag. Called after any
    /// change, by the host: the control cannot know when something else set
    /// Texts.Language.
    /// </summary>
    public void Mark()
    {
        bool english = Texts.Language == "en";
        FlagCsMark.Opacity = english ? 0 : 1;
        FlagEnMark.Opacity = english ? 1 : 0;
    }

    /// <summary>
    /// Presses a flag the way a person would, for the self-check.
    ///
    /// It goes through the real handler rather than raising the event directly,
    /// so the check still covers the part that reads the language out of the
    /// element's Tag. A flag is a drawing, not a button, so there is no property
    /// to set that would raise the event on its own.
    /// </summary>
    public void Press(string language) =>
        OnFlagPressed(language == "en" ? FlagEn : FlagCs, null!);

    private void OnFlagPressed(object sender, PointerRoutedEventArgs e)
    {
        // Read from the Tag, never from a label: deciding anything from
        // displayed text breaks the moment the language changes, and this
        // project has that on its list of repeated faults.
        string language = (sender as FrameworkElement)?.Tag as string ?? "cs";
        if (language == Texts.Language)
            return;

        LanguagePicked?.Invoke(language);
    }
}
