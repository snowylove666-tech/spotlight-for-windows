using Spotlight.Core.Search;

public class TokenizerTests
{
    [Fact]
    public void CjkRunsBecomeBigrams()
    {
        var t = TextTokenizer.ForIndex("合同条款").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "合同", "同条", "条款", "款" }, t);
    }

    [Fact]
    public void WhitespaceInsideChineseIsIgnored()
    {
        var t = TextTokenizer.ForIndex("采购\n合同").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("购合", t);
    }

    [Fact]
    public void CamelCaseIsSplitForNames()
    {
        var t = TextTokenizer.ForIndex("AnnualReport2024", splitWords: true).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("AnnualReport2024", t);
        Assert.Contains("Annual", t);
        Assert.Contains("Report", t);
        Assert.Contains("2024", t);
    }

    [Theory]
    [InlineData("合同", "\"合同\"")]
    [InlineData("合", "\"合\"*")]
    [InlineData("合同条款", "\"合同 同条 条款\"")]
    [InlineData("Report 2024", "\"report\"* AND \"2024\"*")]
    [InlineData("季度report", "\"季度\" AND \"report\"*")]
    public void QueriesBecomeFtsExpressions(string q, string expected) =>
        Assert.Equal(expected, TextTokenizer.ToFtsQuery(q));

    [Fact]
    public void PunctuationOnlyQueryIsEmpty() => Assert.Null(TextTokenizer.ToFtsQuery("\"*()"));

    [Theory]
    [InlineData("1+2", "3")]
    [InlineData("12*(3+4)", "84")]
    [InlineData("2^10", "1024")]
    [InlineData("１０÷４", null)]           // full-width digits are not supported
    [InlineData("10÷4", "2.5")]
    [InlineData("report", null)]
    [InlineData("2024", null)]
    public void Calculator(string q, string? expected)
    {
        bool ok = Spotlight.Core.Search.Calculator.TryEvaluate(q, out var v);
        Assert.Equal(expected != null, ok);
        if (ok) Assert.Equal(expected, v);
    }

    [Fact]
    public void InitialsMatchWords() => Assert.Equal("vsc", SearchEngine.Initials("Visual Studio Code"));
}
