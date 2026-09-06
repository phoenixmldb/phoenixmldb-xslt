using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{

    /// <summary>
    /// XTSE0370: Check for unescaped right curly bracket in fixed part of AVT/TVT.
    /// </summary>
    private static void CheckUnescapedRightBrace(string literal, XElement context)
    {
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '}')
            {
                if (i + 1 < literal.Length && literal[i + 1] == '}')
                {
                    i++; // skip escaped }}
                }
                else
                {
                    throw new XsltException("XTSE0370: An unescaped right curly bracket '}' in a fixed part of an attribute value template is not allowed",
                        GetSourceLocation(context));
                }
            }
        }
    }


    private static string UnescapeAvt(string value)
    {
        return value.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

}
