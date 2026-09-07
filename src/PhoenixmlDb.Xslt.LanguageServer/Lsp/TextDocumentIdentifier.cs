using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);
