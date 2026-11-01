namespace PROTV.Plugin.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.8.0", "0.8.0.0")]
    [InlineData("v0.8.0", "0.8.0.0")]
    [InlineData("0.8", "0.8.0.0")]
    [InlineData("0.8.0.1", "0.8.0.1")]
    [InlineData(" 1.2.3 ", "1.2.3.0")]
    public void Normalize_pads_versions_to_four_parts(string input, string expected)
    {
        Assert.Equal(expected, ProTvUpdates.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Normalize_rejects_values_that_are_not_versions(string? input)
    {
        Assert.Null(ProTvUpdates.Normalize(input));
    }

    [Fact]
    public void Compare_orders_versions_numerically()
    {
        Assert.True(ProTvUpdates.Compare("0.9.0.0", "0.8.0.0") > 0);
        Assert.True(ProTvUpdates.Compare("0.8.0.0", "0.9.0.0") < 0);
        Assert.Equal(0, ProTvUpdates.Compare("0.8.0.0", "0.8.0.0"));
        Assert.True(ProTvUpdates.Compare("0.10.0.0", "0.9.0.0") > 0);
    }

    [Theory]
    [InlineData("owner/repo", true)]
    [InlineData(" owner/repo ", true)]
    [InlineData("owner/repo/", true)]
    [InlineData("owner-name/repo_name.js", true)]
    [InlineData("owner", false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("https://github.com/owner/repo", false)]
    [InlineData("owner/re po", false)]
    [InlineData("owner/repo?x=1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Repository_must_be_a_plain_owner_slash_repo(string? input, bool expected)
    {
        Assert.Equal(expected, ProTvUpdates.IsValidRepository(input));
    }
}
