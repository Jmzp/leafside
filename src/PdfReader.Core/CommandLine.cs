using System.Text;

namespace PdfReader.Core;

public static class CommandLine
{
    /// <summary>
    /// Splits a Windows command line the way CommandLineToArgvW does for arguments (quotes group, a backslash
    /// escapes a quote, runs of backslashes before a quote are halved).
    /// </summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');
                    else i--; // the quote toggles quoting; handle it on the next iteration
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                hasToken = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"'); // "" inside quotes is a literal quote
                    i++;
                }
                else inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken) args.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    /// <summary>
    /// The documents to open among some arguments: existing files that are not options (--x) nor programs
    /// (a forwarded command line starts with the executable's own path).
    /// </summary>
    public static IReadOnlyList<string> Files(IEnumerable<string> args, Func<string, bool> exists) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)
                        && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && exists(a))
            .ToList();
}
