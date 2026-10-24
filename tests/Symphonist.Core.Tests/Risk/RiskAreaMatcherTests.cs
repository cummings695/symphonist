using Symphonist.Core.Config;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Risk;

public class RiskAreaMatcherTests
{
    private static readonly RiskArea Pricing = new("pricing", "money", ["src/Pricing/**"]);

    [Theory]
    [InlineData("src/Pricing/Calc.cs", true)]
    [InlineData("src/pricing/calc.cs", true)]
    [InlineData("src/Orders/Api.cs", false)]
    public void Matches_area_globs(string file, bool touched)
    {
        var result = RiskAreaMatcher.Touched([Pricing], [file]);
        Assert.Equal(touched, result.Count == 1);
        if (touched) Assert.Equal(("pricing", file), result[0]);
    }

    [Fact]
    public void Area_without_paths_never_matches() =>
        Assert.Empty(RiskAreaMatcher.Touched([new RiskArea("none", "", [])], ["src/Pricing/Calc.cs"]));

    [Fact]
    public void Backslash_paths_are_normalised() =>
        Assert.Single(RiskAreaMatcher.Touched([Pricing], ["src\\Pricing\\Calc.cs"]));
}
