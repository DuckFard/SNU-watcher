using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnuWatcher.Desktop.Runtime;

internal static partial class StudentUiValidation
{
    // Exercise WPF's real tab traversal without sending input to other desktop applications.
    private static async Task ProbeOnboardingKeyboardAsync(StudentWindow window)
    {
        var welcome = Named<Border>(window, "Onboarding");
        var buttons = Visuals<Button>(welcome).Where(button => button.IsVisible && button.IsEnabled).ToArray();
        Require(buttons.Length == 3, "ui_onboarding_actions_missing");
        window.Activate();
        foreach (var direction in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
        {
            foreach (var start in buttons)
            {
                Require(start.Focus(), "ui_onboarding_focus_unavailable");
                await PumpUiAsync(window);
                for (var index = 0; index < buttons.Length + 2; index++)
                {
                    Require(Keyboard.FocusedElement is FrameworkElement focused
                        && focused.MoveFocus(new TraversalRequest(direction)), "ui_onboarding_tab_failed");
                    await PumpUiAsync(window);
                    Require(Keyboard.FocusedElement is FrameworkElement next && next.IsVisible && next.IsEnabled
                        && welcome.IsAncestorOf(next), "ui_onboarding_focus_escaped");
                }
            }
        }
        // Covered controls must also reject explicit keyboard focus, rather than only tabbing around them.
        Require(!Named<Button>(window, "QuitButton").Focus()
            && !Named<RadioButton>(window, "FindNavigation").Focus(), "ui_onboarding_background_focusable");
        Require(buttons[^1].Focus(), "ui_onboarding_action_focus_unavailable");
    }
}
