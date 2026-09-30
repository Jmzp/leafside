using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace PdfReader.App.Services;

/// <summary>
/// Localized strings for code (XAML uses x:Uid). Strings live in Strings/&lt;language&gt;/Resources.resw;
/// English is the default and Windows picks the user's language when a translation exists.
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    /// <summary>The string for <paramref name="key"/>, or the key itself if it is missing (never throws).</summary>
    public static string Get(string key)
    {
        try
        {
            string value = Loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
