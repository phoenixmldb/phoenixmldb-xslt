using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record DidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);
