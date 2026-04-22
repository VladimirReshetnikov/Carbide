// @carbide-ui/avalonia-runner — placeholder Avalonia Application for UI-M2.
// At UI-M3 the runner instantiates a user-supplied App class from a loaded PE;
// this placeholder renders a splash so the pipeline can be smoke-tested before
// the full RunnerProgram ships.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Carbide.UI.Runner;

public sealed class App : Application
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
                Text = "Carbide.UI runner — UI-M2 splash",
                Margin = new Thickness(16),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
