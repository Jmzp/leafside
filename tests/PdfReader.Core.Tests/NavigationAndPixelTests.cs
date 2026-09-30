using PdfReader.Core.Rendering;
using PdfReader.Core.State;

namespace PdfReader.Core.Tests;

public sealed class NavigationAndPixelTests
{
    [Fact]
    public void History_goes_back_and_forward_like_a_browser()
    {
        var history = new NavigationHistory();
        Assert.Null(history.GoBack(new ViewState(0)));

        history.Push(new ViewState(1));   // jump 1 → 10
        history.Push(new ViewState(10));  // jump 10 → 50
        Assert.Equal(new ViewState(10), history.GoBack(new ViewState(50)));
        Assert.Equal(new ViewState(1), history.GoBack(new ViewState(10)));
        Assert.False(history.CanGoBack);
        Assert.Equal(new ViewState(10), history.GoForward(new ViewState(1)));
        Assert.True(history.CanGoForward);

        history.Push(new ViewState(10));  // a new jump drops the forward history
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void History_is_bounded()
    {
        var history = new NavigationHistory();
        for (int i = 0; i < NavigationHistory.Capacity + 10; i++) history.Push(new ViewState(i));
        int count = 0;
        var current = new ViewState(-1);
        while (history.GoBack(current) is { } previous) { current = previous; count++; }
        Assert.Equal(NavigationHistory.Capacity, count);
        Assert.Equal(10, current.Page); // the oldest entries were dropped
    }

    [Fact]
    public void Far_jumps_are_more_than_one_page()
    {
        Assert.False(new ViewState(3, 0.5).IsFarFrom(new ViewState(4, 0.2)));
        Assert.True(new ViewState(3, 0.5).IsFarFrom(new ViewState(5, 0)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void Invert_flips_color_channels_and_keeps_alpha(int pixels)
    {
        var data = new byte[pixels * 4];
        new Random(42).NextBytes(data);
        var original = (byte[])data.Clone();
        PixelOps.InvertBgr(data);
        for (int i = 0; i < data.Length; i++)
            Assert.Equal(i % 4 == 3 ? original[i] : (byte)(255 - original[i]), data[i]);
        PixelOps.InvertBgr(data);
        Assert.Equal(original, data);
    }
}
