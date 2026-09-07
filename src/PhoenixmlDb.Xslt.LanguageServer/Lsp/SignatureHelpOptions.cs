using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

public sealed record SignatureHelpOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters);
