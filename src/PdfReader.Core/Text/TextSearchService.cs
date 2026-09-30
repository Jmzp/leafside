using PdfReader.Core.Engine;

namespace PdfReader.Core.Text;

/// <summary>
/// Searches a whole document in the background, page by page, starting from a given page and wrapping
/// around, so the matches closest to where the user is reading arrive first.
/// </summary>
public static class TextSearchService
{
    /// <param name="onPageMatches">Invoked (from a background thread) with the matches of each page that has any.</param>
    /// <returns>Total number of matches.</returns>
    public static Task<int> SearchAsync(IPdfDocument document, string query, int startPage, bool matchCase, bool wholeWord,
        Action<int, IReadOnlyList<SearchMatch>> onPageMatches, CancellationToken token)
    {
        return Task.Run(() =>
        {
            int total = 0;
            int count = document.PageCount;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                int page = (startPage + i) % count;
                var matches = document.Search(page, query, matchCase, wholeWord);
                if (matches.Count == 0) continue;
                total += matches.Count;
                onPageMatches(page, matches);
            }
            return total;
        }, token);
    }
}
