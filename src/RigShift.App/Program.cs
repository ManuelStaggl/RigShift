using Velopack;

namespace RigShift.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack must run first: it handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
