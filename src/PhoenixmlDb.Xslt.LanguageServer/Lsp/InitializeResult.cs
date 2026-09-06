using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record InitializeResult(
    [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities);
