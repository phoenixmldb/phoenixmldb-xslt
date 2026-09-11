using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Keys that compare equal hash equal. The empty sequence arrives as null or as an empty array,
/// and a single item as itself or as a one-element array; equality normalized both, the hash did
/// not, so an equal key could land in another bucket and miss the memo.
/// </summary>
public sealed class FunctionMemoKeyTests
{
    private static readonly XsltFunction Function = new()
    {
        Name = new QName(NamespaceId.None, "f"),
        Parameters = [],
        Body = new XsltSequenceConstructor { Instructions = [] },
    };

    private static FunctionMemoKey Key(params object?[] arguments) => new(Function, arguments);

    [Theory]
    [MemberData(nameof(EqualRepresentations))]
    public void EqualKeys_HashEqual(object? a, object? b)
    {
        var (ka, kb) = (Key(a), Key(b));
        ka.Should().Be(kb);
        ka.GetHashCode().Should().Be(kb.GetHashCode());
    }

    public static TheoryData<object?, object?> EqualRepresentations => new()
    {
        { null, Array.Empty<object?>() },
        { 1L, new object?[] { 1L } },
        { "x", new object?[] { "x" } },
    };

    [Fact]
    public void DifferentSequences_AreDifferentKeys()
        => Key(new object?[] { 1L, 2L }).Should().NotBe(Key(new object?[] { 2L, 1L }));
}
