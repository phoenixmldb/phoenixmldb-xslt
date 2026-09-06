using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record DidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);
