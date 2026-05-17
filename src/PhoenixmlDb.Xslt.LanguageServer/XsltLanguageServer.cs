using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using PhoenixmlDb.Xslt.LanguageServer.Lsp;
using StreamJsonRpc;
using Range = PhoenixmlDb.Xslt.LanguageServer.Lsp.Range;
using Position = PhoenixmlDb.Xslt.LanguageServer.Lsp.Position;

namespace PhoenixmlDb.Xslt.LanguageServer;

/// <summary>
/// Minimal LSP server for XSLT. MVP: textDocument lifecycle + publishDiagnostics.
/// No hover or completion in this MVP (engine doesn't yet expose introspection surfaces
/// suitable for them); Plan 26 fills those in.
/// </summary>
public sealed class XsltLanguageServer
{
    private readonly ConcurrentDictionary<string, DocumentBuffer> _buffers = new(StringComparer.Ordinal);

    public JsonRpc? Rpc { get; set; }

    [JsonRpcMethod("initialize")]
    public InitializeResult Initialize(object? _) =>
        new(new ServerCapabilities
        {
            TextDocumentSync = 1,
            DocumentSymbolProvider = true,
            SignatureHelpProvider = new SignatureHelpOptions(TriggerCharacters: ["(", ","]),
            DefinitionProvider = true,
            ReferencesProvider = true,
        });

    [JsonRpcMethod("textDocument/documentSymbol")]
    public DocumentSymbol[] DocumentSymbol(DocumentSymbolParams p)
    {
        if (!_buffers.TryGetValue(p.TextDocument.Uri, out var buf)) return Array.Empty<DocumentSymbol>();
        return Handlers.DocumentSymbolHandler.Handle(buf);
    }

    [JsonRpcMethod("textDocument/signatureHelp")]
    public SignatureHelp? SignatureHelp(TextDocumentPositionParams p)
    {
        if (!_buffers.TryGetValue(p.TextDocument.Uri, out var buf)) return null;
        return Handlers.SignatureHelpHandler.Handle(buf, p.Position);
    }

    [JsonRpcMethod("textDocument/definition")]
    public Location? Definition(TextDocumentPositionParams p)
    {
        if (!_buffers.TryGetValue(p.TextDocument.Uri, out var buf)) return null;
        return Handlers.DefinitionHandler.Handle(buf, p.Position);
    }

    [JsonRpcMethod("textDocument/references")]
    public Location[] References(ReferenceParams p)
    {
        if (!_buffers.TryGetValue(p.TextDocument.Uri, out var buf)) return Array.Empty<Location>();
        return Handlers.ReferencesHandler.Handle(buf, p.Position);
    }

    [JsonRpcMethod("initialized")]
    public void Initialized(object? _) { }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown(object? _) => null;

    [JsonRpcMethod("exit")]
    public void Exit(object? _) => Environment.Exit(0);

    [JsonRpcMethod("textDocument/didOpen")]
    public Task DidOpen(DidOpenTextDocumentParams p)
    {
        var buf = new DocumentBuffer(p.TextDocument.Uri, p.TextDocument.Version, p.TextDocument.Text);
        _buffers[p.TextDocument.Uri] = buf;
        return PublishDiagnosticsAsync(buf);
    }

    [JsonRpcMethod("textDocument/didChange")]
    public Task DidChange(DidChangeTextDocumentParams p)
    {
        if (!_buffers.TryGetValue(p.TextDocument.Uri, out var buf)) return Task.CompletedTask;
        if (p.ContentChanges.Length == 0) return Task.CompletedTask;
        var last = p.ContentChanges[^1];
        buf.ReplaceAll(p.TextDocument.Version, last.Text);
        return PublishDiagnosticsAsync(buf);
    }

    [JsonRpcMethod("textDocument/didClose")]
    public void DidClose(DidCloseTextDocumentParams p) =>
        _buffers.TryRemove(p.TextDocument.Uri, out _);

    private async Task PublishDiagnosticsAsync(DocumentBuffer buf)
    {
        var diags = ComputeDiagnostics(buf);
        if (Rpc is null) return;
        await Rpc.NotifyAsync("textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(buf.Uri, diags)).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the stylesheet via <see cref="XsltTransformer"/> and converts any
    /// <see cref="XsltException"/> to LSP diagnostics. The transformer's
    /// <c>LoadStylesheetAsync</c> performs parse + compile; an exception here means
    /// the stylesheet is invalid.
    /// </summary>
    internal Diagnostic[] ComputeDiagnostics(DocumentBuffer buf)
    {
        var diags = new List<Diagnostic>();
        var transformer = new XsltTransformer();
        try
        {
            transformer.LoadStylesheetAsync(buf.Text).GetAwaiter().GetResult();
        }
        catch (XsltException ex)
        {
            var range = ex.Location is { } loc
                ? new Range(
                    new Position(Math.Max(0, loc.Line - 1), Math.Max(0, loc.Column)),
                    new Position(Math.Max(0, loc.Line - 1), Math.Max(0, loc.Column) + Math.Max(1, loc.Length)))
                : new Range(new Position(0, 0), new Position(0, 1));
            diags.Add(new Diagnostic(range, Severity: 1, Code: null, Source: "xslt", Message: ex.Message));
        }
        catch (System.Xml.XmlException ex)
        {
            var range = new Range(
                new Position(Math.Max(0, ex.LineNumber - 1), Math.Max(0, ex.LinePosition - 1)),
                new Position(Math.Max(0, ex.LineNumber - 1), Math.Max(0, ex.LinePosition - 1) + 1));
            diags.Add(new Diagnostic(range, Severity: 1, Code: "XML", Source: "xslt", Message: ex.Message));
        }
        return diags.ToArray();
    }
}
