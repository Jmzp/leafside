using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PdfReader.App.Services;

/// <summary>
/// Per-user registration (HKCU, no admin rights) so the reader appears in Explorer's "Open with" for .pdf files.
/// Windows does not let an app make itself the default handler; the user picks it once ("Always").
/// Registration is refreshed at startup, so moving the app's folder keeps it working.
/// </summary>
public static partial class FileAssociation
{
    private const string ProgId = "LeafSide.Document";
    private const string AppKey = @"Applications\LeafSide.exe";
    /// <summary>What earlier versions (named "PDF Reader") registered; removed when found.</summary>
    private static readonly string[] LegacyProgIds = ["LectorPDF.Document", "PdfReader.Document"];
    private const string LegacyAppKey = @"Applications\PdfReader.exe";
    private const string ClassesKey = @"Software\Classes";

    public static void Register()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LeafSide.exe");
        string command = $"\"{exe}\" \"%1\"";
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey);
        RemoveLegacy(classes);
        using (var existing = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
        {
            if (existing?.GetValue("") as string == command) return; // up to date: touch nothing
        }

        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", Loc.Get("PdfDocument"));
            prog.SetValue("FriendlyTypeName", Loc.Get("PdfDocument"));
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var open = prog.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        }
        using (var openWith = classes.CreateSubKey(@".pdf\OpenWithProgids"))
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using (var app = classes.CreateSubKey(AppKey))
        {
            app.SetValue("FriendlyAppName", Loc.Get("AppName"));
            using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
            using (var open = app.CreateSubKey(@"shell\open\command")) open.SetValue("", command);
        }
        NotifyShell();
    }

    /// <summary>Removes everything <see cref="Register"/> wrote (LeafSide.exe --unregister).</summary>
    public static void Unregister()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(ClassesKey, writable: true);
        if (classes is null) return;
        RemoveProgId(classes, ProgId);
        RemoveLegacy(classes);
        classes.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);
        NotifyShell();
    }

    private static void RemoveLegacy(RegistryKey classes)
    {
        foreach (var progId in LegacyProgIds) RemoveProgId(classes, progId);
        classes.DeleteSubKeyTree(LegacyAppKey, throwOnMissingSubKey: false);
    }

    private static void RemoveProgId(RegistryKey classes, string progId)
    {
        classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
        using var openWith = classes.OpenSubKey(@".pdf\OpenWithProgids", writable: true);
        openWith?.DeleteValue(progId, throwOnMissingValue: false);
    }

    private static void NotifyShell() => SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
