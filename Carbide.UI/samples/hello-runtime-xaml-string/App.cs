// Sample: hello-runtime-xaml-string — proposal §16.3.
// Ambition tier: XAML-as-literal. AvaloniaRuntimeXamlLoader.Load against an inline
// XAML string. No .axaml file; no companion generation. Appears in the multi-preview
// fixture as preview C.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace HelloRuntimeXaml;

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
            svl.MainView = new MainView();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public class MainView : UserControl
{
    private const string Xaml = @"
        <UserControl xmlns='https://github.com/avaloniaui'
                     xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
            <TextBlock Text='Hello from runtime XAML' Margin='16' />
        </UserControl>";

    public MainView()
    {
        AvaloniaRuntimeXamlLoader.Load(Xaml, typeof(MainView).Assembly, this);
    }
}
