using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Conformance.Tests.Xslt;

/// <summary>
/// assert-result-document must judge the document it names and fail when that document is absent.
/// </summary>
public class ResultDocumentLookupTests
{
    private static readonly Dictionary<string, string> Results = new()
    {
        ["MMP.xml"] = "mmp",
        ["file:///out/P.xml"] = "p",
        ["H.xml"] = "h",
    };

    [Theory]
    [InlineData("P.xml", "p")]       // not MMP.xml, whose name merely ends with "P.xml"
    [InlineData("MMP.xml", "mmp")]
    [InlineData("H.xml", "h")]
    public void A_result_document_is_found_by_its_name(string href, string expected) =>
        XsltTestRunner.FindResultDocument(Results, href).Should().Be(expected);

    [Theory]
    [InlineData("X.xml")]
    [InlineData("MP.xml")]
    public void A_missing_result_document_is_not_found(string href) =>
        XsltTestRunner.FindResultDocument(Results, href).Should().BeNull();
}
