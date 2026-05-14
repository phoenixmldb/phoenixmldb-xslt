using System;

namespace PhoenixmlDb.Xslt.LanguageServer;

/// <summary>
/// Per-URI source-of-truth: full text + version, with a (line, character) ↔ offset helper.
/// MVP uses full-text sync.
/// </summary>
public sealed class DocumentBuffer
{
    public DocumentBuffer(string uri, int version, string text)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Version = version;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public string Uri { get; }
    public int Version { get; private set; }
    public string Text { get; private set; }

    public void ReplaceAll(int version, string text)
    {
        Version = version;
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }
}
