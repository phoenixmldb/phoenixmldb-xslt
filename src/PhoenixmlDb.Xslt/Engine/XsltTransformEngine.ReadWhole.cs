using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Why a transformation of a stylesheet with a streamable initial mode reads its input into memory
/// in place of streaming it (xslt#582). <paramref name="Detail"/> completes the sentence
/// "the input was read into memory: ...". <paramref name="Construct"/> is set where the cause is a
/// construct of the stylesheet that this processor cannot evaluate while streaming; only that cause
/// is an error under <see cref="XsltTransformOptions.StrictStreamability"/>.
/// </summary>
internal sealed record ReadWholeCause(string Detail, string? Construct = null, SourceLocation? Location = null, string? Place = null)
{
    /// <summary>What was read into memory: the input, or the document of an xsl:source-document.</summary>
    public string Subject { get; init; } = "the input";

    /// <summary>A construct of the stylesheet that this processor cannot evaluate while streaming.</summary>
    public static ReadWholeCause OfConstruct(string construct, SourceLocation? location, string? place)
        => new(construct + At(location) + (place is null ? "" : " (" + place + ")")
                + ", which this processor cannot evaluate while streaming",
            construct, location, place);

    /// <summary>The text of the warning.</summary>
    public string Warning => "XSLT streaming: " + Subject + " was read into memory: " + Detail + ".";

    /// <summary>
    /// The error under <see cref="XsltTransformOptions.StrictStreamability"/>: phx:XTSE9430 for the
    /// principal input, found before the transformation starts; phx:XTDE9430 for an
    /// xsl:source-document, found when it runs.
    /// </summary>
    public XsltException StrictError(bool sourceDocument)
        => new((sourceDocument
                    ? "phx:XTDE9430: The document of xsl:source-document cannot be streamed: "
                    : "phx:XTSE9430: The input cannot be streamed: ")
                + "this processor cannot evaluate " + Construct + (Place is null ? "" : " (" + Place + ")")
                + " while streaming, although the rules of XSLT 3.0 section 19.8 find it guaranteed streamable."
                + " Without StrictStreamability (xslt command line: --strict-streaming), or with StreamingFallback"
                + " (--streaming-fallback), it reads the " + (sourceDocument ? "document" : "input")
                + " into memory and gives the same result.",
            Location);

    /// <summary>" at file:line:column", as the command line writes the location of an error.</summary>
    internal static string At(SourceLocation? location)
    {
        if (location is null)
            return "";
        var module = location.Module;
        if (!string.IsNullOrEmpty(module) && module.StartsWith("file://", StringComparison.Ordinal)
            && Uri.TryCreate(module, UriKind.Absolute, out var uri))
            module = uri.LocalPath;
        if (location.Line <= 0)
            return string.IsNullOrEmpty(module) ? "" : " in " + module;
        var line = location.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var column = location.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(module) ? $" at line {line}, column {column}" : $" at {module}:{line}:{column}";
    }
}

public sealed partial class XsltTransformEngine
{
    /// <summary>
    /// Why this transformation reads its input into memory although its initial mode is
    /// streamable, or null where it streams it, or where the caller asked for a tree
    /// (<see cref="XsltTransformOptions.DisableStreaming"/>) or the initial mode is not streamable.
    /// <paramref name="entryIsNotAtTheDocument"/>: the transformation starts at a named template or
    /// a function. The order is that of the decision each transform overload makes.
    /// </summary>
    private ReadWholeCause? WhyTheInputIsReadWhole(XsltTransformOptions? options, bool entryIsNotAtTheDocument)
    {
        if (options is null or { DisableStreaming: true })
            return null;
        var initialModeKey = options.InitialMode ?? new QName(NamespaceId.None, "");
        if (!_stylesheet.Modes.TryGetValue(initialModeKey, out var mode) || !mode.Streamable)
            return null;
        if (options.SourceValidation is Ast.ValidationMode.Strict or Ast.ValidationMode.Lax)
            return new ReadWholeCause(
                $"the input is validated (SourceValidation {options.SourceValidation}), and it is validated as a tree");
        // With no input there is nothing to read (and xsl:initial-template is the entry only then).
        if (entryIsNotAtTheDocument)
            return !options.HasSourceDocument ? null : new ReadWholeCause(options.InitialFunction is { } function
                ? $"the transformation starts at the function {function.PrefixedName}, not at a template rule of the streamable mode"
                : $"the transformation starts at the named template {options.InitialTemplate?.PrefixedName}, not at a template rule of the streamable mode");
        return WhyThePassCannotAnswer(options);
    }

    /// <summary>
    /// Why the streaming pass from the document node would not give the right result for this
    /// stylesheet, or null where it would. Where it would not, the input is read whole and the
    /// tree engine answers.
    /// </summary>
    private ReadWholeCause? WhyThePassCannotAnswer(XsltTransformOptions? options)
    {
        // A rule of a streamable mode that reads outside its matched element cannot be answered
        // by the streaming pass; it answered wrongly with no error (xslt#298).
        if (StreamedScopeEscapeDetector.FirstEscape(_stylesheet) is { } escape)
            return ReadWholeCause.OfConstruct(escape.Construct, escape.Location, PlaceOf(escape.Template));
        // XInclude expansion needs the whole input buffered into a DOM before ConvertToXdm; the
        // streaming forward pass bypasses that, so fall through to the buffered path.
        if (options?.ExpandXInclude == true)
            return new ReadWholeCause("XInclude expansion of the input (ExpandXInclude) reads it as a tree");
        return DocNodeTemplateReadWholeCause(options);
    }

    /// <summary>
    /// Reports why the input is read into memory: under StrictStreamability a construct of the
    /// stylesheet is phx:XTSE9430, and otherwise the warning goes to the warning listener when the
    /// caller asked for it. Every overload that reads the input into memory does it through the one
    /// that takes it as text, which reports, so it is reported once.
    /// </summary>
    private static void ReportReadingWhole(ReadWholeCause? cause, XsltTransformOptions? options)
    {
        if (cause is null || options is null)
            return;
        if (cause.Construct is not null && StrictAboutReadingWhole(options))
            throw cause.StrictError(sourceDocument: false);
        if (options.StreamingFallbackWarnings && options.WarningListener is { } listener)
            listener(cause.Warning);
    }

    /// <summary>
    /// Whether reading the input into memory for a construct of the stylesheet is an error: under
    /// StrictStreamability, unless StreamingFallback asks for a buffer or the W3C harness for the
    /// fallback.
    /// </summary>
    internal static bool StrictAboutReadingWhole(XsltTransformOptions options)
        => options is { StrictStreamability: true, StreamingFallback: false, ReadWholeUnderStrictStreamability: false };

    /// <summary>How a message names a template rule: <c>the template match="book" mode="g"</c>.</summary>
    internal static string PlaceOf(XsltTemplate template)
        => template.Label is { Length: > 0 } label ? "the template " + label : "a template rule";

    /// <summary>
    /// Why the rule for the document node that a streamable transformation dispatches through
    /// cannot be evaluated off the live reader (see <see cref="DocLevelWholeInputBuffer"/>), or null.
    /// </summary>
    private ReadWholeCause? DocNodeTemplateReadWholeCause(XsltTransformOptions? options)
    {
        if (DocNodeTemplate(options) is not { Body: { } body } template || !DocLevelWholeInputBuffer(body, _stylesheet))
            return null;
        var (construct, location) = DocLevelReadWholeConstruct(body, _stylesheet);
        return ReadWholeCause.OfConstruct(construct, location ?? body.Location, PlaceOf(template));
    }

    /// <summary>
    /// For a message: what in a body that <see cref="DocLevelWholeInputBuffer"/> reads whole is the
    /// cause. The innermost instruction that is a cause by itself, or the body.
    /// </summary>
    internal static (string Construct, SourceLocation? Location) DocLevelReadWholeConstruct(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        if (StreamabilityChecker.HasSeveralConsumingOperands(body))
            return ("more than one instruction that reads the children of the document node",
                body.Location ?? body.Instructions.FirstOrDefault(i => i.Location is not null)?.Location);
        return Culprit(body, stylesheet) ?? ("the body", body.Location);
    }

    private static (string Construct, SourceLocation? Location)? Culprit(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        foreach (var instruction in body.Instructions)
        {
            if (instruction is XsltLiteralText or XsltText)
                continue;
            if (!DocLevelWholeInputBuffer(new XsltSequenceConstructor { Instructions = [instruction], Location = instruction.Location }, stylesheet))
                continue;
            foreach (var property in instruction.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(XsltSequenceConstructor) && property.GetIndexParameters().Length == 0
                    && property.GetValue(instruction) is XsltSequenceConstructor inner
                    && Culprit(inner, stylesheet) is { } deeper)
                    return deeper;
            }
            return (InstructionName(instruction), instruction.Location);
        }
        return null;
    }

    /// <summary>The name of an instruction as a stylesheet writes it: <c>xsl:for-each-group</c>, <c>the literal result element out</c>.</summary>
    internal static string InstructionName(XsltInstruction instruction)
    {
        if (instruction is XsltLiteralResultElement lre)
            return "the literal result element " + lre.Name.PrefixedName;
        if (instruction is XsltTextValueTemplate)
            return "a text value template";
        var type = instruction.GetType().Name;
        if (type.StartsWith("Xslt", StringComparison.Ordinal))
            type = type[4..];
        if (type.EndsWith("Instruction", StringComparison.Ordinal) && type.Length > "Instruction".Length)
            type = type[..^"Instruction".Length];
        var name = new System.Text.StringBuilder("xsl:");
        for (var i = 0; i < type.Length; i++)
        {
            if (char.IsUpper(type[i]) && i > 0)
                name.Append('-');
            name.Append(char.ToLowerInvariant(type[i]));
        }
        return name.ToString();
    }
}
