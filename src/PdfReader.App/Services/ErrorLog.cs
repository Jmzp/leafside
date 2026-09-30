namespace PdfReader.App.Services;

/// <summary>Appends unexpected errors to %LocalAppData%\PdfReader\error.log.</summary>
public static class ErrorLog
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfReader", "error.log");

    public static void Write(Exception ex)
    {
        System.Diagnostics.Debug.WriteLine(ex);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
