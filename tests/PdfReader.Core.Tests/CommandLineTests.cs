namespace PdfReader.Core.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData(@"""C:\Program Files\PDF Reader\PdfReader.exe"" ""C:\Docs\a b.pdf""", new[] { @"C:\Program Files\PDF Reader\PdfReader.exe", @"C:\Docs\a b.pdf" })]
    [InlineData(@"PdfReader.exe   C:\x.pdf  --bench", new[] { "PdfReader.exe", @"C:\x.pdf", "--bench" })]
    [InlineData(@"a\\""b c"" d", new[] { @"a\b c", "d" })]
    [InlineData(@"a\""b", new[] { @"a""b" })]
    [InlineData(@"""C:\dir\\""", new[] { @"C:\dir\" })]
    [InlineData(@"""""", new[] { "" })]
    [InlineData("", new string[0])]
    public void Splits_like_windows(string commandLine, string[] expected) =>
        Assert.Equal(expected, CommandLine.Split(commandLine));

    [Fact]
    public void Picks_existing_documents()
    {
        var existing = new HashSet<string> { @"C:\app\PdfReader.exe", @"C:\a.pdf", @"C:\b.PDF" };
        var files = CommandLine.Files([@"C:\app\PdfReader.exe", "--bench", @"C:\a.pdf", @"C:\missing.pdf", @"C:\b.PDF"], existing.Contains);
        Assert.Equal([@"C:\a.pdf", @"C:\b.PDF"], files);
    }
}
