using Codex.RuntimeKit.Desktop.DebugTransport;
using Xunit;

namespace Codex.RuntimeKit.Tests;

public sealed class CodexDesktopAnchorProbeTests
{
    [Fact]
    public void AnchorProbe_IsNarrowAndDoesNotReadPageText()
    {
        var expression = CodexDesktopCdpSession.CreateUniqueVisibleAnchorProbeExpression(
            "[data-ambient-usage-label]");

        Assert.Contains("const selector = \"[data-ambient-usage-label]\"", expression, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll(selector)", expression, StringComparison.Ordinal);
        Assert.Contains("totalCount", expression, StringComparison.Ordinal);
        Assert.Contains("visibleCount", expression, StringComparison.Ordinal);
        Assert.Contains("parentClassNames", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("textContent", expression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("innerText", expression, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outerHTML", expression, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("[data-x],body")]
    [InlineData("[data-x] || alert(1)")]
    [InlineData("[data-X]")]
    public void AnchorProbe_RejectsSelectorsOutsideSingleLowercaseDataAttributeGrammar(string selector)
    {
        Assert.Throws<ArgumentException>(() =>
            CodexDesktopCdpSession.CreateUniqueVisibleAnchorProbeExpression(selector));
    }

    [Fact]
    public void AdjacencyProbe_RequiresOwnedNodeAsImmediateSiblingOfUniqueVisibleAnchor()
    {
        var expression = CodexDesktopCdpSession.CreateExtensionAdjacencyProbeExpression(
            "[data-ambient-usage-label]",
            "data-codex-control-center-api-balance");

        Assert.Contains("nextElementSibling", expression, StringComparison.Ordinal);
        Assert.Contains("parentElement", expression, StringComparison.Ordinal);
        Assert.Contains("data-codex-control-center-api-balance", expression, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[data-x]", "data-x] body")]
    [InlineData("[data-x],body", "data-owner")]
    public void AdjacencyProbe_RejectsInvalidSelectorsAndAttributes(string selector, string rootAttribute)
    {
        Assert.Throws<ArgumentException>(() =>
            CodexDesktopCdpSession.CreateExtensionAdjacencyProbeExpression(selector, rootAttribute));
    }
}
