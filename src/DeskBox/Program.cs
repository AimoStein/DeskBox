using DeskBox.Services;

namespace DeskBox;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var diagnostics = args.Any(arg => arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
            arg is "--desktop-icons-probe" or "--glass-preview" ||
            arg.StartsWith("--glass-smoketest=", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--glass-probe=", StringComparison.OrdinalIgnoreCase));
        using var instance = diagnostics ? null : new AppInstance();
        if (instance is not null && !instance.TryAcquire())
        {
            AppInstance.SendToPrimary(args.Length == 0 ? new[] { "--settings" } : args);
            return;
        }

        using var icons = new DesktopIconLayer();
        var preview = !diagnostics && args.Contains("--startup", StringComparer.OrdinalIgnoreCase)
            ? new StartupDesktop(icons) : null;
        try
        {
            var app = new App(instance, icons, preview);
            app.InitializeComponent();
            app.Run();
        }
        finally { preview?.Stop(); }
    }
}
