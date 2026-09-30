using Microsoft.UI.Xaml;

namespace PdfReader.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Services.ErrorLog.Write(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Services.ErrorLog.Write(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        // Files passed on the command line (e.g. "Abrir con" from Explorer).
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (arguments.Remove("--bench") && arguments.FirstOrDefault(File.Exists) is { } benchFile)
        {
            _ = RunBenchmarkAsync(_window, benchFile);
            return;
        }
        _ = _window.StartAsync(arguments.Where(File.Exists).ToList());
    }

    /// <summary>Opens a file, runs the scroll benchmark, writes %LocalAppData%\PdfReader\bench.log and exits.</summary>
    private static async Task RunBenchmarkAsync(MainWindow window, string path)
    {
        await window.OpenFileAsync(path);
        await Task.Delay(1500);
        string report = await window.RunBenchmarkAsync();
        string log = Path.Combine(Path.GetDirectoryName(Services.ErrorLog.FilePath)!, "bench.log");
        File.WriteAllText(log, $"{DateTime.Now:O} {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}{report}");
        window.Close();
    }
}
