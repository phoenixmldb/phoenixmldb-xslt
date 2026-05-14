using System.Text.Json.Serialization;

namespace PhoenixmlDb.Xslt.LanguageServer.Lsp;

// Minimal LSP 3.17 subset. Property names are camelCase via [JsonPropertyName].

public sealed record Position(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public sealed record Range(
    [property: JsonPropertyName("start")] Position Start,
    [property: JsonPropertyName("end")] Position End);

public sealed record Diagnostic(
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("message")] string Message);

public sealed record TextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri);

public sealed record VersionedTextDocumentIdentifier(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int Version);

public sealed record TextDocumentItem(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("languageId")] string LanguageId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("text")] string Text);

public sealed record TextDocumentContentChangeEvent(
    [property: JsonPropertyName("range")] Range? Range,
    [property: JsonPropertyName("text")] string Text);

public sealed record DidOpenTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(
    [property: JsonPropertyName("textDocument")] VersionedTextDocumentIdentifier TextDocument,
    [property: JsonPropertyName("contentChanges")] TextDocumentContentChangeEvent[] ContentChanges);

public sealed record DidCloseTextDocumentParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public sealed record PublishDiagnosticsParams(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("diagnostics")] Diagnostic[] Diagnostics);

public sealed record ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public int TextDocumentSync { get; init; } = 1;

    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; init; }
}

public sealed record InitializeResult(
    [property: JsonPropertyName("capabilities")] ServerCapabilities Capabilities);

// Plan 28 additions
public sealed record DocumentSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("range")] Range Range,
    [property: JsonPropertyName("selectionRange")] Range SelectionRange);

public sealed record DocumentSymbolParams(
    [property: JsonPropertyName("textDocument")] TextDocumentIdentifier TextDocument);

public static class SymbolKind
{
    public const int Function = 12;
    public const int Variable = 13;
    public const int Class = 5;
    public const int Module = 2;
    public const int Method = 6;
}
