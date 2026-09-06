using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record DocumentSymbolParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);
