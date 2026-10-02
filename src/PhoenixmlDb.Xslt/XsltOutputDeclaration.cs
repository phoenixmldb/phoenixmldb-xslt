using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// A compiled <c>xsl:output</c> declaration, as <see cref="XsltTransformer.OutputDeclarations"/>
/// reports it: after declarations of the same name across the stylesheet and its imports and
/// includes have been merged by import precedence.
/// </summary>
/// <param name="Name">The declaration's name, or <c>null</c> for the unnamed (principal) one.</param>
/// <param name="Method">The method the declaration states, or <c>null</c> when it states none.</param>
/// <param name="EffectiveMethod">
/// <paramref name="Method"/>, or <see cref="OutputMethod.Xml"/> when it states none. A result
/// serialized under a declaration with no method can still use html or xhtml, by the
/// default-method rule (an <c>html</c> document element), and <c>xsl:result-document</c> can
/// choose a method at run time. To restrict what is actually delivered, use
/// <see cref="XsltTransformer.AllowedOutputMethods"/>.
/// </param>
public sealed record XsltOutputDeclaration(QName? Name, OutputMethod? Method, OutputMethod EffectiveMethod);
