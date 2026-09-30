using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PdfReader.Core;
using Windows.ApplicationModel.Activation;

namespace PdfReader.App;

public partial class App : Application
{
    private static App? _current;
    private MainWindow? _window;

    public App()
    {
        _current = this;
        InitializeComponent();
        UnhandledException += (_, e) => Services.ErrorLog.Write(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Services.ErrorLog.Write(e.Exception);
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        // Files passed on the command line (e.g. "Abrir con" from Explorer).
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (arguments.Contains("--bench") && CommandLine.Files(arguments, File.Exists).FirstOrDefault() is { } benchFile)
        {
            Services.AppState.PersistenceEnabled = false;
            _ = RunBenchmarkAsync(_window, benchFile);
            return;
        }
        _ = _window.StartAsync(CommandLine.Files(arguments, File.Exists));
        _ = Task.Run(RegisterFileAssociation);
    }

    private static void RegisterFileAssociation()
    {
        try { Services.FileAssociation.Register(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Services.ErrorLog.Write(ex);
        }
    }

    /// <summary>
    /// Another launch (e.g. double-clicking a PDF in Explorer) was redirected to this instance.
    /// Raised on a background thread.
    /// </summary>
    public static void OnRedirectedActivation(AppActivationArguments args)
    {
        IReadOnlyList<string> files = args.Data switch
        {
            ILaunchActivatedEventArgs launch => CommandLine.Files(CommandLine.Split(launch.Arguments ?? string.Empty), File.Exists),
            IFileActivatedEventArgs file => file.Files.Select(f => f.Path).Where(File.Exists).ToList(),
            _ => [],
        };
        if (_current?._window is not { } window) return;
        window.DispatcherQueue.TryEnqueue(async () =>
        {
            window.BringToFront();
            foreach (var path in files) await window.OpenFileAsync(path);
        });
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
