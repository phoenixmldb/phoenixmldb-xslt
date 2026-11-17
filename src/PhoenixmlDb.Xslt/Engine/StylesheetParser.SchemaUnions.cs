using System.Xml.Linq;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>
    /// The member types of a plain union type that a schema of this package defines, as
    /// expanded names: <c>&lt;xs:simpleType name="u1"&gt;&lt;xs:union memberTypes="xs:date xs:time"/&gt;</c>
    /// gives {date, time} in the XSD namespace. Null when the type is not such a union of named
    /// types in a schema the package imports (inline, or at a location the package can read).
    /// </summary>
    private static HashSet<string>? UnionMemberTypes(XdmSequenceType type, XsltStylesheet package)
    {
        if (type.SchemaTypeLocalName is not { } local)
            return null;
        var targetNamespace = type.SchemaTypeNamespace ?? "";
        foreach (var import in SchemaImportsOf(package))
        {
            foreach (var schema in SchemaDocuments(import))
            {
                if ((schema.Attribute("targetNamespace")?.Value ?? "") != targetNamespace)
                    continue;
                var simpleType = schema.Elements(Xsd + "simpleType").FirstOrDefault(t => t.Attribute("name")?.Value == local);
                if (simpleType?.Element(Xsd + "union") is not { } union
                    || union.Elements(Xsd + "simpleType").Any()
                    || union.Attribute("memberTypes")?.Value is not { } memberTypes)
                    continue;
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in memberTypes.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = member.IndexOf(':', StringComparison.Ordinal);
                    var memberNamespace = colon > 0
                        ? union.GetNamespaceOfPrefix(member[..colon])?.NamespaceName
                        : union.GetDefaultNamespace().NamespaceName;
                    if (memberNamespace is null)
                        return null;
                    members.Add("Q{" + memberNamespace + "}" + (colon > 0 ? member[(colon + 1)..] : member));
                }
                return members;
            }
        }
        return null;
    }

    private static IEnumerable<XsltSchemaImport> SchemaImportsOf(XsltStylesheet stylesheet)
    {
        foreach (var import in stylesheet.SchemaImports)
            yield return import;
        foreach (var module in stylesheet.Includes.Concat(stylesheet.Imports))
            foreach (var import in SchemaImportsOf(module))
                yield return import;
    }

    private static IEnumerable<XElement> SchemaDocuments(XsltSchemaImport import)
    {
        if (import.InlineSchema is { } inline)
        {
            XElement? parsed = null;
            try { parsed = XElement.Parse(inline); }
            catch (System.Xml.XmlException) { }
            if (parsed is not null)
                yield return parsed;
        }
        foreach (var location in import.SchemaLocations)
        {
            XElement? loaded = null;
            try
            {
                var uri = import.ModuleUri is { } baseUri ? new Uri(baseUri, location) : new Uri(location, UriKind.RelativeOrAbsolute);
                if (uri.IsAbsoluteUri && uri.IsFile && File.Exists(uri.LocalPath))
                    loaded = XElement.Load(uri.LocalPath);
            }
            catch (Exception e) when (e is UriFormatException or IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
            }
            if (loaded is not null)
                yield return loaded;
        }
    }
}
