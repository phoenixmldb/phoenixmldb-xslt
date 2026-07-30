using FluentAssertions;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class ContextItemUsageTests
{
    // A for-each whose select does NOT atomize the item delivers it as a node (Transmission) —
    // the legacy default; value-of/copy-of/navigation all consume a node correctly.
    [Fact]
    public void NonAtomizedSelect_IsTransmission()
    {
        UsageClassifier.ClassifyContextItemUsage(selectAtomized: false)
            .Should().Be(Usage.Transmission);
    }

    // A data()-wrapped for-each select atomizes the item (Absorption) → deliver the atomized value
    // (si-copy-002: a bare xsl:copy then copies the atomic as text, not a leaked node).
    [Fact]
    public void AtomizedSelect_IsAbsorption()
    {
        UsageClassifier.ClassifyContextItemUsage(selectAtomized: true)
            .Should().Be(Usage.Absorption);
    }
}
