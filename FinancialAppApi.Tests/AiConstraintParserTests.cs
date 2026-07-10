using FinancialAppApi.Services;

namespace FinancialAppApi.Tests;

// Phase 2: deterministic negation / exclusion / hypothetical parsing.
public class AiConstraintParserTests
{
    [Fact]
    public void DontOpenLedger_SetsPreventNavigation()
    {
        var c = AiAssistantService.ParseConstraints("Don't open the ledger, just tell me the total.");
        Assert.True(c.PreventNavigation);
        // "just tell me" is filler, not a real included category.
        Assert.Empty(c.IncludedCategories);
    }

    [Fact]
    public void ExcludingRent_CapturesExcludedCategory()
    {
        var c = AiAssistantService.ParseConstraints("How much did I spend excluding rent?");
        Assert.Contains("rent", c.ExcludedCategories, StringComparer.OrdinalIgnoreCase);
        Assert.False(c.ExcludeTransfers);
    }

    [Fact]
    public void WithoutCountingTransfers_SetsExcludeTransfers()
    {
        var c = AiAssistantService.ParseConstraints("What's my total without counting transfers.");
        Assert.True(c.ExcludeTransfers);
        // "transfers" is folded into ExcludeTransfers, not duplicated as a category term.
        Assert.DoesNotContain("transfers", c.ExcludedCategories, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatIfISave_IsHypothetical()
    {
        var c = AiAssistantService.ParseConstraints("What if I save another RM200 per cycle?");
        Assert.True(c.Hypothetical);
    }

    [Fact]
    public void OnlyBadminton_CapturesIncludedTerm()
    {
        var c = AiAssistantService.ParseConstraints("Only badminton please.");
        Assert.Contains("badminton", c.IncludedCategories, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotAskingAboutWishlist_CapturesNegatedTopic()
    {
        var c = AiAssistantService.ParseConstraints("I'm not asking about my wishlist.");
        Assert.Contains("wishlist", c.NegatedTopics, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlainQuestion_HasNoConstraints()
    {
        var c = AiAssistantService.ParseConstraints("How much did I spend this month?");
        Assert.False(c.PreventNavigation);
        Assert.False(c.ExcludeTransfers);
        Assert.False(c.Hypothetical);
        Assert.Empty(c.ExcludedCategories);
    }

    [Fact]
    public void ResolveConstraintCategories_MatchesKnownCategories()
    {
        var (categories, ledger) = AiAssistantService.ResolveConstraintCategories(
            ["rent", "unknownthing"],
            ["Rent", "Food"],
            ["Essentials", "Rewards"]);
        Assert.Contains("Rent", categories);
        Assert.Empty(ledger);
    }
}
