using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Risk;

public class ReviewerTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two\nlines\r\nand\ttab", "two lines  and tab")]
    [InlineData("bell\u0007 escape\u001b[31mred\u001b[0m", "bell escape[31mred[0m")]
    [InlineData("bidi ‮gnp.exe‬ zero​width", "bidi gnp.exe zerowidth")]
    [InlineData("del\u007F nel\u0085", "del nel")]
    [InlineData("  padded  ", "padded")]
    public void Line_strips_control_and_format_characters(string text, string expected) =>
        Assert.Equal(expected, ReviewerText.Line(text, 100));

    [Fact]
    public void Line_cuts_at_the_limit_and_says_so()
    {
        Assert.Equal("abcd…", ReviewerText.Line("abcdefgh", 5));
        Assert.Equal("abcde", ReviewerText.Line("abcde", 5));
    }

    [Fact]
    public void Line_never_splits_a_surrogate_pair()
    {
        var cut = ReviewerText.Line("abc😀def", 5); // the emoji is two chars at index 3-4
        Assert.Equal("abc…", cut);
    }

    [Fact]
    public void Items_are_capped_in_length_and_number()
    {
        var items = Enumerable.Range(1, 12).Select(i => i == 1 ? new string('x', 300) : $"item\n{i}").ToList();

        var cleaned = ReviewerText.Items(items);

        Assert.Equal(ReviewerText.MaxItems + 1, cleaned.Count);
        Assert.Equal(ReviewerText.MaxItemChars, cleaned[0].Length);
        Assert.EndsWith("…", cleaned[0]);
        Assert.Equal("item 2", cleaned[1]);
        Assert.Equal("(+2 more)", cleaned[^1]);
    }

    [Fact]
    public void Empty_items_after_cleaning_are_dropped()
    {
        Assert.Equal(["ok"], ReviewerText.Items(["\u0007​", "ok"]));
    }
}
