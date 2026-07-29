using PhoenixmlDb.Xslt.Engine.Streamability;
using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class PostureContextTests
{
    [Fact]
    public void Current_DefaultsToAbsorption()
    {
        new PostureContext().Current.Should().Be(Usage.Absorption);
    }

    [Fact]
    public void Push_ThenPop_RestoresPrevious_Nested()
    {
        var ctx = new PostureContext();

        var saved = ctx.Push(Usage.Transmission);
        saved.Should().Be(Usage.Absorption);
        ctx.Current.Should().Be(Usage.Transmission);

        var savedInner = ctx.Push(Usage.Inspection);
        savedInner.Should().Be(Usage.Transmission);
        ctx.Current.Should().Be(Usage.Inspection);

        ctx.Pop(savedInner);
        ctx.Current.Should().Be(Usage.Transmission);

        ctx.Pop(saved);
        ctx.Current.Should().Be(Usage.Absorption);
    }
}
