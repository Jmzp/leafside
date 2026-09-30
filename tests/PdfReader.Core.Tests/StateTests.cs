using PdfReader.Core.Engine;
using PdfReader.Core.Layout;
using PdfReader.Core.State;

namespace PdfReader.Core.Tests;

public sealed class StateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdfreader-state-").FullName;
    private string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Page_position_round_trips_through_the_layout()
    {
        var layout = new DocumentLayout(Enumerable.Repeat(new PageSize(72, 72), 10).ToList(), pageGap: 4, padding: 0);
        // Page i spans [100 i, 100 i + 96].
        Assert.Equal((3, 0.5), layout.ToPagePosition(348));
        Assert.Equal(348, layout.FromPagePosition(3, 0.5), 6);
        var (page, fraction) = layout.ToPagePosition(398); // in the gap above page 4
        Assert.Equal(4, page);
        Assert.True(fraction < 0);
        Assert.Equal(398, layout.FromPagePosition(page, fraction), 6);
        Assert.Equal(900, layout.FromPagePosition(50, 0)); // clamped to the last page
    }

    [Fact]
    public void Views_recent_files_session_and_settings_persist()
    {
        string a = Path.Combine(_dir, "a.pdf"), b = Path.Combine(_dir, "B.pdf");
        var store = new AppStateStore(StatePath);
        store.Touch(a);
        store.SetView(a, new ViewState(12, 0.25, 1.5, 30));
        Thread.Sleep(5);
        store.Touch(b);
        store.Session.OpenFiles.AddRange([a, b]);
        store.Session.SelectedIndex = 1;
        store.Settings.NightMode = true;
        Assert.True(store.Save());

        var reloaded = new AppStateStore(StatePath);
        Assert.Equal(new ViewState(12, 0.25, 1.5, 30), reloaded.GetView(a.ToUpperInvariant())); // case-insensitive
        Assert.Null(reloaded.GetView(b));
        Assert.Equal([b, a], reloaded.Recent(10).Select(d => d.Path));
        Assert.Equal([a, b], reloaded.Session.OpenFiles);
        Assert.Equal(1, reloaded.Session.SelectedIndex);
        Assert.True(reloaded.Settings.NightMode);
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void Removing_from_recent_keeps_the_position()
    {
        string a = Path.Combine(_dir, "a.pdf");
        var store = new AppStateStore(StatePath);
        store.Touch(a);
        store.SetView(a, new ViewState(3));
        store.RemoveFromRecent(a);
        Assert.Empty(store.Recent(10));
        Assert.Equal(3, store.GetView(a)!.Page);
        store.Touch(a);
        Assert.Single(store.Recent(10));
    }

    [Fact]
    public void Recent_skips_missing_files()
    {
        var store = new AppStateStore(StatePath);
        store.Touch(Path.Combine(_dir, "gone.pdf"));
        Assert.Empty(store.Recent(10, File.Exists));
        Assert.Single(store.Recent(10));
    }

    [Fact]
    public void Store_is_trimmed_to_the_most_recent_documents()
    {
        var store = new AppStateStore(StatePath);
        for (int i = 0; i < AppStateStore.MaxDocuments + 20; i++) store.Touch(Path.Combine(_dir, $"{i}.pdf"));
        store.Save();
        var reloaded = new AppStateStore(StatePath);
        Assert.Equal(AppStateStore.MaxDocuments, reloaded.Data.Documents.Count);
        Assert.Null(reloaded.Data.Documents.Values.FirstOrDefault(d => d.Path.EndsWith(@"\0.pdf")));
    }

    [Fact]
    public void First_version_format_is_migrated()
    {
        File.WriteAllText(StatePath, """{"C:\\DOCS\\OLD.PDF":{"Page":42,"LastOpened":"2026-09-29T16:40:58Z"}}""");
        var store = new AppStateStore(StatePath);
        Assert.Equal(new ViewState(42), store.GetView(@"C:\docs\old.pdf"));
        Assert.Single(store.Recent(10));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"Version":2,"Documents":null,"Session":null}""")]
    public void Corrupt_state_is_ignored(string content)
    {
        File.WriteAllText(StatePath, content);
        var store = new AppStateStore(StatePath);
        Assert.Empty(store.Recent(10));
        Assert.Empty(store.Session.OpenFiles);
        store.Touch(Path.Combine(_dir, "a.pdf"));
        Assert.True(store.Save());
    }
}
