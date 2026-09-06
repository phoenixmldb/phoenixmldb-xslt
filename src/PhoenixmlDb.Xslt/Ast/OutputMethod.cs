using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Output method.
/// </summary>
public enum OutputMethod
{
    Xml,
    Html,
    Xhtml,
    Text,
    Json,
    Adaptive,
    Csv     // XSLT 4.0
}
