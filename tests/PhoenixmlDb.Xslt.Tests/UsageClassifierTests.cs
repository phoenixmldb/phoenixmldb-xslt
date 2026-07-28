using FluentAssertions;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class UsageClassifierTests
{
    // §5.7.1 element/complex content: the content operand's nodes are transmitted
    // (children copied) to the result — no atomic separator.
    [Theory]
    [InlineData(ConstructKind.ElementContent, Usage.Transmission)]
    [InlineData(ConstructKind.DocumentContent, Usage.Transmission)]
    // §5.7.2 simple content: the operand is atomized/absorbed — space-separated.
    [InlineData(ConstructKind.AttributeContent, Usage.Absorption)]
    [InlineData(ConstructKind.TextContent, Usage.Absorption)]
    [InlineData(ConstructKind.SimpleContentAtomize, Usage.Absorption)]
    // §19.6/§19.7 other roles.
    [InlineData(ConstructKind.NodeNameInspection, Usage.Inspection)]
    [InlineData(ConstructKind.AxisStepSource, Usage.Navigation)]
    public void OperandUsage_MapsConstructKindToSpecRole(ConstructKind kind, Usage expected)
    {
        UsageClassifier.OperandUsage(kind).Should().Be(expected);
    }
}
