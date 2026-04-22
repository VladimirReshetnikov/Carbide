// Sample: counter — proposal §16.2.
// Ambition tier: minimum-interactive. Click handling + mutable state; still no XAML.
// Appears in the multi-preview fixture as preview B.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace CarbideCounter;

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
            svl.MainView = new CounterView();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public class CounterView : StackPanel
{
    private int _count;
    private readonly TextBlock _label = new() { Text = "0" };

    public CounterView()
    {
        Margin = new Thickness(16);
        Spacing = 8;
        var button = new Button { Content = "Click me" };
        button.Click += (_, _) => _label.Text = (++_count).ToString();
        Children.Add(button);
        Children.Add(_label);
    }
}
