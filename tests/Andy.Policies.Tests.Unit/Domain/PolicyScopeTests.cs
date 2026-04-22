// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Validation;
using Xunit;

namespace Andy.Policies.Tests.Unit.Domain;

public class PolicyScopeTests
{
    [Theory]
    [InlineData("prod")]
    [InlineData("staging")]
    [InlineData("tool:write-branch")]
    [InlineData("tool:read")]
    [InlineData("template:code-change")]
    [InlineData("repo:rivoli-ai-andy-policies")]
    [InlineData("a")] // single-char minimum length boundary
    public void IsValid_AcceptsCanonicalScopes(string scope)
    {
        Assert.True(PolicyScope.IsValid(scope));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("UPPER")]                  // uppercase rejected
    [InlineData("has space")]              // whitespace rejected
    [InlineData("9-starts-digit")]         // must start with letter
    [InlineData("-starts-dash")]
    [InlineData("*")]                      // reserved wildcard
    [InlineData("repo:rivoli-ai/conductor")] // slash disallowed (repo-slug shape uses binding targetRef)
    [InlineData("embedded/path")]          // slash disallowed
    public void IsValid_RejectsInvalidScopes(string? scope)
    {
        Assert.False(PolicyScope.IsValid(scope));
    }

    [Fact]
    public void IsValid_RejectsScopesExceedingSixtyThreeCharacters()
    {
        var tooLong = "a" + new string('b', 63);

        Assert.False(PolicyScope.IsValid(tooLong));
    }

    [Fact]
    public void Canonicalise_DeduplicatesAndSorts()
    {
        var input = new[] { "tool:write", "prod", "tool:write", "staging", "prod" };

        var result = PolicyScope.Canonicalise(input);

        Assert.Equal(new[] { "prod", "staging", "tool:write" }, result);
    }

    [Fact]
    public void Canonicalise_EmptyInput_ReturnsEmpty()
    {
        var result = PolicyScope.Canonicalise(Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void Canonicalise_InvalidElement_ThrowsArgumentException()
    {
        var input = new[] { "prod", "INVALID" };

        var ex = Assert.Throws<ArgumentException>(() => PolicyScope.Canonicalise(input));
        Assert.Contains("INVALID", ex.Message);
    }

    [Fact]
    public void Canonicalise_WildcardElement_ThrowsArgumentException()
    {
        var input = new[] { "prod", "*" };

        var ex = Assert.Throws<ArgumentException>(() => PolicyScope.Canonicalise(input));
        Assert.Contains("*", ex.Message);
    }

    [Fact]
    public void Canonicalise_NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyScope.Canonicalise(null!));
    }
}
