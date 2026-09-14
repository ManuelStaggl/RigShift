using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// <c>controls:LiveRegion.IsLive="True"</c>: a polite live region that really announces. WPF does not raise
/// <see cref="AutomationEvents.LiveRegionChanged"/> by itself when a text changes, so a screen reader ignored the
/// <c>LiveSetting</c> alone (analysis finding I-17). Announces when the text, the message or the visibility set on the
/// element changes – not when a page with the element is merely shown.
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.RegisterAttached(
        "IsLive", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnIsLiveChanged));

    public static bool GetIsLive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsLiveProperty);
    }

    public static void SetIsLive(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsLiveProperty, value);
    }

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        DependencyProperty[] watched = element switch
        {
            System.Windows.Controls.TextBlock => [System.Windows.Controls.TextBlock.TextProperty, UIElement.VisibilityProperty],
            InfoBar => [InfoBar.MessageProperty, InfoBar.IsOpenProperty],
            _ => [UIElement.VisibilityProperty],
        };

        void Changed(object? sender, EventArgs args) => Announce(element);

        // Handlers are attached only while loaded: template items (profile cards) come and go with every reload.
        element.Loaded += (_, _) =>
        {
            foreach (DependencyProperty property in watched)
            {
                DependencyPropertyDescriptor.FromProperty(property, element.GetType()).AddValueChanged(element, Changed);
            }
        };
        element.Unloaded += (_, _) =>
        {
            foreach (DependencyProperty property in watched)
            {
                DependencyPropertyDescriptor.FromProperty(property, element.GetType()).RemoveValueChanged(element, Changed);
            }
        };
    }

    private static void Announce(FrameworkElement element) =>
        // After layout, so the peer reads the new text and the element is visible.
        element.Dispatcher.InvokeAsync(
            () =>
            {
                if (element.IsVisible && element is not InfoBar { IsOpen: false })
                {
                    UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }
            },
            DispatcherPriority.Background);
}
