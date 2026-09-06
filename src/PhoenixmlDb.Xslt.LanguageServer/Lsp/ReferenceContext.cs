using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record ReferenceContext(
    [property: JsonPropertyName("includeDeclaration")] bool IncludeDeclaration);
