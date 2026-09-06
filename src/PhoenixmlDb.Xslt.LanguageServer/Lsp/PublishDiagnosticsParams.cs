using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record PublishDiagnosticsParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("diagnostics")] Diagnostic[] Diagnostics);
