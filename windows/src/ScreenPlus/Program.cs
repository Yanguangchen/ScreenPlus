using System.Windows;

namespace ScreenPlus;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Cli.Handles(args)) return Cli.Run(args);

        // One ScreenPlus at a time: a second copy would add a second toolbar and fight over the recording.
        using var instance = new Mutex(initiallyOwned: true, @"Local\ScreenPlus.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("ScreenPlus is already running. Look for its toolbar at the bottom of your screen.",
                            "ScreenPlus", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
