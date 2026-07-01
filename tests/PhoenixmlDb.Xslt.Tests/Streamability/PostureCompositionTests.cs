using FluentAssertions;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Streamability;

/// <summary>
/// Task 0.2 foundation tests for the XSLT 3.0 §19 compositional streamability model.
/// Covers the <see cref="PostureSweep.IsGuaranteedStreamable"/> rule (§19.8.6) and the
/// <see cref="StreamabilityAnnotation"/> side-table. Purely additive — no engine behaviour
/// is exercised or changed here.
/// </summary>
public class PostureCompositionTests
{
    [Fact]
    public void Grounded_Motionless_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Grounded, Sweep.Motionless).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Striding_Consuming_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Striding, Sweep.Consuming).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Climbing_Motionless_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Climbing, Sweep.Motionless).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Crawling_Consuming_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Crawling, Sweep.Consuming).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Roaming_Consuming_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Roaming, Sweep.Consuming).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Grounded_FreeRanging_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Grounded, Sweep.FreeRanging).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Artistic_Motionless_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Artistic, Sweep.Motionless).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Annotation_RoundTrips_SetThenTryGet()
    {
        var node = new object();
        var ps = new PostureSweep(Posture.Striding, Sweep.Consuming);

        StreamabilityAnnotation.Set(node, ps);

        StreamabilityAnnotation.TryGet(node, out var got).Should().BeTrue();
        got.Should().Be(ps);
    }

    [Fact]
    public void Annotation_TryGet_OnUnannotatedNode_ReturnsFalse()
    {
        var node = new object();

        StreamabilityAnnotation.TryGet(node, out _).Should().BeFalse();
    }
}
