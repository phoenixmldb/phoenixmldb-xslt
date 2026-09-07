using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Upward-navigation axis for a climbing (striding-then-climbing) watcher.
/// <c>None</c> = a plain downward watcher (no climb). <c>Ancestor</c> /
/// <c>AncestorOrSelf</c> = the watcher's leaf path is followed by an
/// <c>ancestor::</c> / <c>ancestor-or-self::</c> step. The climb is resolved at the
/// leaf's StartElement, where every ancestor is already open on the streaming
/// element stack (Task 1.3).
/// </summary>
internal enum ClimbAxisKind
{
    None,
    Ancestor,
    AncestorOrSelf
}
