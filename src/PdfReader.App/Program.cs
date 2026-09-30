using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PdfReader.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so the app runs as a single instance: opening a PDF from
/// Explorer while the reader is already open adds a tab to the existing window instead of starting another one.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "PdfReader.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (args.Contains("--unregister"))
        {
            Services.FileAssociation.Unregister();
            return 0;
        }

        // The benchmark always runs in its own process.
        if (!args.Contains("--bench"))
        {
            var main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                // Let the running instance bring its window to the front.
                AllowSetForegroundWindow(main.ProcessId);
                // Redirect off the STA thread (the call is async and must not block this thread's apartment).
                Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
                return 0;
            }
            main.Activated += (_, e) => App.OnRedirectedActivation(e);
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
