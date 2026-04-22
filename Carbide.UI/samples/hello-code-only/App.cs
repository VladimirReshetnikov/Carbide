// Sample: hello-code-only — proposal §16.1.
// Ambition tier: minimum-viable. Pure C# UI construction; no XAML.
// Appears in the multi-preview fixture as preview A.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace HelloCodeOnly;

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime svl)
        {
            svl.MainView = new TextBlock
            {
                Text = "Hello, Carbide + Avalonia (code-only)",
                Margin = new Thickness(16),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
