using System.Windows;
using System.Windows.Threading;
using Kitsune.App.Services;

namespace Kitsune.App;

public partial class App : Application
{
    public static AppState State { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        State = new AppState();
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        // Kitsune.exe --launch <instance>: start that instance right away (handy for desktop shortcuts).
        var idx = Array.IndexOf(e.Args, "--launch");
        if (idx >= 0 && idx + 1 < e.Args.Length) main.LaunchByName(e.Args[idx + 1]);
        // Kitsune.exe --reroll: open the parallel reroll window right away.
        if (Array.IndexOf(e.Args, "--reroll") >= 0) main.Dispatcher.BeginInvoke(main.OpenReroll);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Kitsune", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        State.Settings.Save(State.Paths);
        State.Dispose();
        base.OnExit(e);
    }
}
