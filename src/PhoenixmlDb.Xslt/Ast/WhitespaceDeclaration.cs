using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// One <c>xsl:strip-space</c> or <c>xsl:preserve-space</c> element name test, together with the
/// import precedence of the module that declared it.
/// </summary>
/// <remarks>
/// <para>
/// XSLT resolves a whitespace-control conflict in two ordered steps (XSLT 1.0 §3.4, carried
/// forward into 3.0 §4.4):
/// </para>
/// <para>
/// "First, any match with lower import precedence than another match is ignored. Next, any match
/// with a NameTest that has a lower default priority than the default priority of the NameTest of
/// another match is ignored."
/// </para>
/// <para>
/// Import precedence dominates default priority, so the precedence has to travel with the test.
/// <c>NameTest</c> belongs to the XQuery AST in the sibling repository and cannot carry an
/// XSLT-only field, hence this wrapper rather than a property on the test itself.
/// </para>
/// <para>
/// <see cref="ImportPrecedence"/> follows the numbering used by <c>CollectImportedOutputs</c>:
/// the principal module is 0, its imports 1, their imports 2, and so on — <b>lower number wins</b>.
/// Note this is the opposite of <c>XsltTemplate.ImportPrecedence</c>, where higher wins; the two
/// conventions coexist in this codebase and the comparison helpers state which they use.
/// </para>
/// </remarks>
/// <param name="Test">The element name test from the declaration's <c>elements</c> attribute.</param>
/// <param name="ImportPrecedence">Importing depth; 0 is the principal module, higher is weaker.</param>
public sealed record WhitespaceDeclaration(NameTest Test, int ImportPrecedence);
