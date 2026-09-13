using UrlShortener.Domain;

namespace UrlShortener.Tests;

public class ShortCodeGeneratorTests
{
    // Kept in sync with ShortCodeGenerator's private alphabet: lower/upper alphanumerics
    // with confusable characters (l, I, O, 0, 1) excluded.
    private const string ExpectedAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ShortCodeGenerator _generator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(20)]
    public void Generate_ReturnsStringOfRequestedLength(int length)
    {
        var code = _generator.Generate(length);
        Assert.Equal(length, code.Length);
    }

    [Fact]
    public void Generate_DefaultLength_IsSeven()
    {
        var code = _generator.Generate();
        Assert.Equal(7, code.Length);
    }

    [Fact]
    public void Generate_OnlyUsesExpectedAlphabetCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = _generator.Generate(20);
            Assert.All(code, c => Assert.Contains(c, ExpectedAlphabet));
        }
    }

    [Fact]
    public void Generate_ExcludesConfusableCharacters()
    {
        const string confusable = "l0O1I";
        for (var i = 0; i < 500; i++)
        {
            var code = _generator.Generate(20);
            Assert.DoesNotContain(code, c => confusable.Contains(c));
        }
    }

    [Fact]
    public void Generate_ProducesDifferentValuesAcrossCalls()
    {
        var codes = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            codes.Add(_generator.Generate());
        }

        // Random generation over a 57-char alphabet at length 7 should essentially never
        // collide 100 times in a row; leave a small margin for the astronomically unlikely case.
        Assert.True(codes.Count > 90, $"Expected high uniqueness among generated codes, got {codes.Count}/100 unique.");
    }
}
