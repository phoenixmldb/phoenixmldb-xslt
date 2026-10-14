using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// A result of a transformation as a tree: the document node, and the store that holds its
/// nodes (an XQuery engine built over <see cref="Nodes"/> can evaluate expressions against
/// <see cref="Document"/>).
/// </summary>
/// <param name="Document">The document node of the result.</param>
/// <param name="Nodes">The nodes of the result.</param>
public sealed record XsltResultTree(XdmDocument Document, XdmInMemoryStore Nodes);
