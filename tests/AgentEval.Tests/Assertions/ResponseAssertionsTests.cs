// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentEval.Tests;

public class ResponseAssertionsTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ResponseAssertions(null!));
    }

    [Fact]
    public void Constructor_EmptyResponse_DoesNotThrow()
    {
        var assertions = new ResponseAssertions("");
        Assert.NotNull(assertions);
    }

    #endregion

    #region Contain Tests

    [Fact]
    public void Contain_SubstringExists_Passes()
    {
        "Hello World".Should().Contain("World");
    }

    [Fact]
    public void Contain_SubstringMissing_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().Contain("Goodbye"));
        Assert.Contains("Goodbye", ex.Message);
    }

    [Fact]
    public void Contain_CaseInsensitiveByDefault()
    {
        "Hello World".Should().Contain("world");
        "Hello World".Should().Contain("WORLD");
    }

    [Fact]
    public void Contain_CaseSensitive_MatchesExact()
    {
        "Hello World".Should().Contain("World", caseSensitive: true);
        
        Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().Contain("world", caseSensitive: true));
    }

    [Fact]
    public void Contain_IsFluent()
    {
        var result = "Hello World".Should().Contain("Hello");
        Assert.IsType<ResponseAssertions>(result);
    }

    #endregion

    #region ContainAll Tests

    [Fact]
    public void ContainAll_AllPresent_Passes()
    {
        "The quick brown fox".Should().ContainAll("quick", "brown", "fox");
    }

    [Fact]
    public void ContainAll_OneMissing_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "The quick brown fox".Should().ContainAll("quick", "lazy"));
        Assert.Contains("lazy", ex.Message);
        Assert.Contains("Missing:", ex.Message);
    }

    [Fact]
    public void ContainAll_MultipleMissing_ListsAll()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().ContainAll("Goodbye", "Moon", "Hello"));
        Assert.Contains("Goodbye", ex.Message);
        Assert.Contains("Moon", ex.Message);
    }

    #endregion

    #region ContainAny Tests

    [Fact]
    public void ContainAny_OnePresent_Passes()
    {
        "Hello World".Should().ContainAny("Goodbye", "World", "Moon");
    }

    [Fact]
    public void ContainAny_NonePresent_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().ContainAny("Goodbye", "Moon"));
        Assert.Contains("None found", ex.Message);
    }

    #endregion

    #region NotContain Tests

    [Fact]
    public void NotContain_SubstringAbsent_Passes()
    {
        "Hello World".Should().NotContain("Goodbye");
    }

    [Fact]
    public void NotContain_SubstringPresent_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().NotContain("World"));
        Assert.Contains("NOT to contain", ex.Message);
    }

    [Fact]
    public void NotContain_CaseInsensitiveByDefault()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().NotContain("world"));
    }

    [Fact]
    public void NotContain_CaseSensitive_IgnoresDifferentCase()
    {
        "Hello World".Should().NotContain("world", caseSensitive: true);
    }

    #endregion

    #region MatchPattern Tests

    [Fact]
    public void MatchPattern_ValidPattern_Passes()
    {
        "The answer is 42".Should().MatchPattern(@"\d+");
    }

    [Fact]
    public void MatchPattern_NoMatch_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "No numbers here".Should().MatchPattern(@"\d+"));
        Assert.Contains("Matches pattern", ex.Message);
    }

    [Fact]
    public void MatchPattern_CaseInsensitiveByDefault()
    {
        "Hello World".Should().MatchPattern("hello");
    }

    [Fact]
    public void MatchPattern_CustomOptions()
    {
        "Line1\nLine2".Should().MatchPattern("Line1.*Line2", RegexOptions.Singleline);
    }

    [Fact]
    public void MatchPattern_EmailFormat()
    {
        "Contact us at test@example.com".Should()
            .MatchPattern(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
    }

    #endregion

    #region Length Tests

    [Fact]
    public void HaveLengthBetween_WithinRange_Passes()
    {
        "Hello".Should().HaveLengthBetween(1, 10);
    }

    [Fact]
    public void HaveLengthBetween_TooShort_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hi".Should().HaveLengthBetween(5, 10));
        Assert.Contains("Length between", ex.Message);
    }

    [Fact]
    public void HaveLengthBetween_TooLong_Throws()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "This is a very long string".Should().HaveLengthBetween(1, 10));
    }

    [Fact]
    public void HaveLengthAtLeast_MeetsMinimum_Passes()
    {
        "Hello World".Should().HaveLengthAtLeast(5);
    }

    [Fact]
    public void HaveLengthAtLeast_TooShort_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hi".Should().HaveLengthAtLeast(10));
        Assert.Contains("Length", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    #endregion

    #region NotBeEmpty Tests

    [Fact]
    public void NotBeEmpty_WithContent_Passes()
    {
        "Hello".Should().NotBeEmpty();
    }

    [Fact]
    public void NotBeEmpty_EmptyString_Throws()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "".Should().NotBeEmpty());
    }

    [Fact]
    public void NotBeEmpty_WhitespaceOnly_Throws()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "   ".Should().NotBeEmpty());
    }

    #endregion

    #region StartWith Tests

    [Fact]
    public void StartWith_CorrectPrefix_Passes()
    {
        "Hello World".Should().StartWith("Hello");
    }

    [Fact]
    public void StartWith_WrongPrefix_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().StartWith("World"));
        Assert.Contains("start with", ex.Message);
    }

    [Fact]
    public void StartWith_CaseInsensitiveByDefault()
    {
        "Hello World".Should().StartWith("hello");
    }

    [Fact]
    public void StartWith_CaseSensitive()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().StartWith("hello", caseSensitive: true));
    }

    #endregion

    #region EndWith Tests

    [Fact]
    public void EndWith_CorrectSuffix_Passes()
    {
        "Hello World".Should().EndWith("World");
    }

    [Fact]
    public void EndWith_WrongSuffix_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().EndWith("Hello"));
        Assert.Contains("end with", ex.Message);
    }

    [Fact]
    public void EndWith_CaseInsensitiveByDefault()
    {
        "Hello World".Should().EndWith("world");
    }

    [Fact]
    public void EndWith_CaseSensitive()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "Hello World".Should().EndWith("world", caseSensitive: true));
    }

    #endregion

    #region Fluent Chaining Tests

    [Fact]
    public void FluentChaining_MultipleAssertions_AllPass()
    {
        "Hello World, the answer is 42!"
            .Should()
            .NotBeEmpty()
            .Contain("Hello")
            .Contain("World")
            .NotContain("Goodbye")
            .MatchPattern(@"\d+")
            .HaveLengthAtLeast(10)
            .StartWith("Hello")
            .EndWith("!");
    }

    [Fact]
    public void FluentChaining_FirstFails_ThrowsImmediately()
    {
        Assert.Throws<ResponseAssertionException>(() =>
            "Hello World"
                .Should()
                .Contain("Goodbye")  // This should throw
                .Contain("Hello"));  // Never reached
    }

    #endregion

    #region Response Property Tests

    [Fact]
    public void Response_ReturnsOriginalString()
    {
        var original = "Hello World";
        var assertions = original.Should();
        Assert.Equal(original, assertions.Response);
    }

    #endregion

    #region Extension Method Tests

    [Fact]
    public void Should_ExtensionMethod_ReturnsAssertions()
    {
        var result = "test".Should();
        Assert.IsType<ResponseAssertions>(result);
    }

    #endregion
}
