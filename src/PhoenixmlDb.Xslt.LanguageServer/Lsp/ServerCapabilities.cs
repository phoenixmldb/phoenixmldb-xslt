using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public int TextDocumentSync { get; init; } = 1;

    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; init; }

    [JsonPropertyName("signatureHelpProvider")]
    public SignatureHelpOptions? SignatureHelpProvider { get; init; }

    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; init; }

    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; init; }
}
