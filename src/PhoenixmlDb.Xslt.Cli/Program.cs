using System.Diagnostics;
using PhoenixmlDb.Xslt;

var options = CliOptions.Parse(args);

if (options.ShowVersion)
{
    Console.WriteLine("xslt 1.1.0 (PhoenixmlDb XSLT 3.0/4.0)");
    return 0;
}

if (options.ShowHelp || options.Stylesheet == null)
{
    PrintUsage();
    return options.ShowHelp ? 0 : 1;
}

try
{
    var totalSw = Stopwatch.StartNew();

    // Load the stylesheet
    var stylesheetPath = Path.GetFullPath(options.Stylesheet);
    if (!File.Exists(stylesheetPath))
    {
        await Console.Error.WriteLineAsync($"Error: Stylesheet not found: {options.Stylesheet}").ConfigureAwait(true);
        return 1;
    }

    var readSw = Stopwatch.StartNew();
    var stylesheetXml = await File.ReadAllTextAsync(stylesheetPath).ConfigureAwait(true);
    var stylesheetUri = new Uri(stylesheetPath);
    readSw.Stop();

    var compileSw = Stopwatch.StartNew();
    var transformer = new XsltTransformer();
    await transformer.LoadStylesheetAsync(stylesheetXml, stylesheetUri).ConfigureAwait(true);
    compileSw.Stop();

    if (options.Timing)
    {
        await Console.Error.WriteLineAsync(
            $"  read:    {readSw.Elapsed.TotalMilliseconds,8:F1} ms  ({stylesheetXml.Length:N0} chars)")
            .ConfigureAwait(true);
        await Console.Error.WriteLineAsync(
            $"  compile: {compileSw.Elapsed.TotalMilliseconds,8:F1} ms")
            .ConfigureAwait(true);
    }

    if (options.DryRun)
    {
        if (options.Timing)
        {
            totalSw.Stop();
            await Console.Error.WriteLineAsync(
                $"  total:   {totalSw.Elapsed.TotalMilliseconds,8:F1} ms")
                .ConfigureAwait(true);
        }
        await Console.Error.WriteLineAsync("Stylesheet compiled successfully.").ConfigureAwait(true);
        return 0;
    }

    // Set up trace listener
    if (options.Trace)
    {
        transformer.TraceListener = (depth, eventType, details) =>
        {
            var indent = new string(' ', depth * 2);
            Console.Error.WriteLine($"  {indent}[{eventType}] {details}");
        };
    }

    // Set parameters
    foreach (var (name, value) in options.Parameters)
    {
        transformer.SetParameter(name, value);
    }

    // Set initial template if specified
    if (options.InitialTemplate != null)
    {
        var (tName, tNs) = ResolveCliQName(options.InitialTemplate);
        transformer.SetInitialTemplate(tName, tNs);
    }

    // Set initial mode if specified
    if (options.InitialMode != null)
    {
        var (mName, mNs) = ResolveCliQName(options.InitialMode);
        transformer.SetInitialMode(mName, mNs);
    }

    // Load source document
    string? inputXml = null;
    if (options.SourceFile != null)
    {
        var sourceSw = Stopwatch.StartNew();
        var sourcePath = Path.GetFullPath(options.SourceFile);
        if (!File.Exists(sourcePath))
        {
            await Console.Error.WriteLineAsync($"Error: Source file not found: {options.SourceFile}").ConfigureAwait(true);
            return 1;
        }

        transformer.SetSourceDocumentUri(new Uri(sourcePath));

        if (options.Stream)
        {
            sourceSw.Stop();
            if (options.Timing)
            {
                await Console.Error.WriteLineAsync(
                    $"  source:  {sourceSw.Elapsed.TotalMilliseconds,8:F1} ms  (streaming)")
                    .ConfigureAwait(true);
            }

            if (options.OutputDir != null)
            {
                transformer.ResultDocumentHandler = href =>
                {
                    var path = Path.Combine(options.OutputDir, href);
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    return new StreamWriter(File.Create(path));
                };
            }

            var streamTransformSw = Stopwatch.StartNew();
            if (options.OutputFile != null)
            {
                var outputPath = Path.GetFullPath(options.OutputFile);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (outputDir != null && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                using var inputStream = File.OpenRead(sourcePath);
                using var outputStream = File.Create(outputPath);
                await transformer.TransformAsync(inputStream, outputStream).ConfigureAwait(true);
            }
            else
            {
                using var inputStream = File.OpenRead(sourcePath);
                using var inputReader = new StreamReader(inputStream);
                var streamResult = await transformer.TransformAsync(inputReader).ConfigureAwait(true);
                Console.Write(streamResult);
                if (streamResult.Length > 0 && streamResult[^1] != '\n')
                    Console.WriteLine();
            }
            streamTransformSw.Stop();

            if (options.Timing)
            {
                await Console.Error.WriteLineAsync(
                    $"  transform: {streamTransformSw.Elapsed.TotalMilliseconds,6:F1} ms  (streaming)")
                    .ConfigureAwait(true);
            }
        }
        else
        {
            inputXml = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(true);
            sourceSw.Stop();

            if (options.Timing)
            {
                await Console.Error.WriteLineAsync(
                    $"  source:  {sourceSw.Elapsed.TotalMilliseconds,8:F1} ms  ({inputXml.Length:N0} chars)")
                    .ConfigureAwait(true);
            }
        }
    }
    else if (Console.IsInputRedirected)
    {
        // Read XML from stdin
        using var reader = new StreamReader(Console.OpenStandardInput());
        inputXml = await reader.ReadToEndAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(inputXml))
            inputXml = null;
    }

    // Run the transformation (non-streaming path)
    if (!options.Stream || options.SourceFile == null)
    {
        var transformSw = Stopwatch.StartNew();
        var result = await transformer.TransformAsync(inputXml).ConfigureAwait(true);
        transformSw.Stop();

        if (options.Timing)
        {
            await Console.Error.WriteLineAsync(
                $"  transform: {transformSw.Elapsed.TotalMilliseconds,6:F1} ms  ({result.Length:N0} chars output)")
                .ConfigureAwait(true);
        }

        // Write primary output
        if (options.OutputFile != null)
        {
            var outputPath = Path.GetFullPath(options.OutputFile);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (outputDir != null && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            await File.WriteAllTextAsync(outputPath, result).ConfigureAwait(true);
        }
        else
        {
            Console.Write(result);
            if (result.Length > 0 && result[^1] != '\n')
                Console.WriteLine();
        }
    }

    // Write secondary result documents (non-streaming path only)
    if (!options.Stream && transformer.SecondaryResultDocuments.Count > 0)
    {
        var baseDir = options.OutputDir ?? (options.OutputFile != null
            ? Path.GetDirectoryName(Path.GetFullPath(options.OutputFile))
            : Directory.GetCurrentDirectory());

        foreach (var (href, content) in transformer.SecondaryResultDocuments)
        {
            var secondaryPath = Path.Combine(baseDir!, href);
            var secondaryDir = Path.GetDirectoryName(secondaryPath);
            if (secondaryDir != null && !Directory.Exists(secondaryDir))
                Directory.CreateDirectory(secondaryDir);
            await File.WriteAllTextAsync(secondaryPath, content).ConfigureAwait(true);

            if (options.Verbose)
                await Console.Error.WriteLineAsync($"  wrote: {secondaryPath}").ConfigureAwait(true);
        }

        if (options.Timing)
        {
            await Console.Error.WriteLineAsync(
                $"  secondary: {transformer.SecondaryResultDocuments.Count} document(s)")
                .ConfigureAwait(true);
        }
    }

    if (options.Timing)
    {
        totalSw.Stop();
        await Console.Error.WriteLineAsync(
            $"  total:   {totalSw.Elapsed.TotalMilliseconds,8:F1} ms")
            .ConfigureAwait(true);
    }

    return 0;
}
catch (PhoenixmlDb.Xslt.Engine.XsltException ex)
{
    var locationInfo = ex.Location != null ? $" at line {ex.Location.Line}, column {ex.Location.Column}" : "";
    await Console.Error.WriteLineAsync($"XSLT error{locationInfo}: {ex.Message}").ConfigureAwait(true);
    return 2;
}
catch (PhoenixmlDb.XQuery.Parser.XQueryParseException ex)
{
    await Console.Error.WriteLineAsync($"XPath parse error: {ex.Message}").ConfigureAwait(true);
    return 2;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(true);
    if (options.Verbose)
    {
        await Console.Error.WriteLineAsync(ex.StackTrace).ConfigureAwait(true);
    }

    throw;
}

static (string Name, string? Namespace) ResolveCliQName(string qname)
{
    // Handle well-known prefixes
    const string xslNs = "http://www.w3.org/1999/XSL/Transform";

    if (qname.StartsWith("xsl:", StringComparison.Ordinal))
        return (qname[4..], xslNs);

    // Handle Clark notation {uri}local
    if (qname.StartsWith('{'))
    {
        var closing = qname.IndexOf('}', StringComparison.Ordinal);
        if (closing > 0)
            return (qname[(closing + 1)..], qname[1..closing]);
    }

    return (qname, null);
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage: xslt [options] <stylesheet> [source]
               command | xslt [options] <stylesheet>

        Transform XML documents using XSLT 3.0/4.0 stylesheets.

        Arguments:
          <stylesheet>       Path to the XSLT stylesheet (.xsl/.xslt)
          [source]           Path to the source XML document (optional)

        Options:
          -o, --output <path>
                             Write primary output to file instead of stdout
          --output-dir <dir> Base directory for secondary result documents
          -p, --param <name>=<value>
                             Set a stylesheet parameter (repeatable)
          -it, --initial-template <name>
                             Start with a named template instead of matching
          -im, --initial-mode <name>
                             Set the initial mode for template matching
          --timing           Show parse/compile/transform timing breakdown
          --trace            Log template matching, function calls, built-in rules
          --dry-run          Parse and compile only, do not execute
          --stream           Use streaming for large files (lower memory usage)
          -v, --verbose      Show detailed error information
          -h, --help         Show this help message
          --version          Show version information

        Invocation styles:
          xslt style.xsl input.xml              Match templates against source
          xslt -it main style.xsl               Call named template (no source needed)
          xslt -p year=2026 style.xsl data.xml  Pass parameters
          cat data.xml | xslt style.xsl         Read source from stdin

        Secondary output:
          xsl:result-document output is written relative to --output-dir
          (defaults to the directory of --output, or current directory).

        Examples:
          xslt identity.xsl input.xml
          xslt -o result.html report.xsl data.xml
          xslt -it main -p title="Hello" generate.xsl
          xslt --output-dir ./pages book-to-html.xsl book.xml
          xslt --timing style.xsl large-input.xml
          xslt --dry-run style.xsl
          curl http://example.com/data.xml | xslt transform.xsl
        """);
}

/// <summary>
/// Parsed command-line options.
/// </summary>
file sealed class CliOptions
{
    public string? Stylesheet { get; init; }
    public string? SourceFile { get; init; }
    public string? OutputFile { get; init; }
    public string? OutputDir { get; init; }
    public string? InitialTemplate { get; init; }
    public string? InitialMode { get; init; }
    public List<(string Name, string Value)> Parameters { get; init; } = [];
    public bool Timing { get; init; }
    public bool Trace { get; init; }
    public bool DryRun { get; init; }
    public bool Stream { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public bool Verbose { get; init; }

    public static CliOptions Parse(string[] args)
    {
        string? stylesheet = null;
        string? sourceFile = null;
        string? outputFile = null;
        string? outputDir = null;
        string? initialTemplate = null;
        string? initialMode = null;
        var parameters = new List<(string Name, string Value)>();
        var timing = false;
        var trace = false;
        var dryRun = false;
        var stream = false;
        var showHelp = false;
        var showVersion = false;
        var verbose = false;

        var expectingOutput = false;
        var expectingOutputDir = false;
        var expectingParam = false;
        var expectingTemplate = false;
        var expectingMode = false;

        foreach (var arg in args)
        {
            if (expectingOutput) { outputFile = arg; expectingOutput = false; continue; }
            if (expectingOutputDir) { outputDir = arg; expectingOutputDir = false; continue; }
            if (expectingTemplate) { initialTemplate = arg; expectingTemplate = false; continue; }
            if (expectingMode) { initialMode = arg; expectingMode = false; continue; }
            if (expectingParam)
            {
                var eqIdx = arg.IndexOf('=', StringComparison.Ordinal);
                if (eqIdx > 0)
                    parameters.Add((arg[..eqIdx], arg[(eqIdx + 1)..]));
                else
                    parameters.Add((arg, ""));
                expectingParam = false;
                continue;
            }

            switch (arg)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "-o" or "--output":
                    expectingOutput = true;
                    break;
                case "--output-dir":
                    expectingOutputDir = true;
                    break;
                case "-p" or "--param":
                    expectingParam = true;
                    break;
                case "-it" or "--initial-template":
                    expectingTemplate = true;
                    break;
                case "-im" or "--initial-mode":
                    expectingMode = true;
                    break;
                case "--timing":
                    timing = true;
                    break;
                case "--trace":
                    trace = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--stream":
                    stream = true;
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                default:
                    // Handle -p name=value (combined form)
                    if (arg.StartsWith("-p:", StringComparison.Ordinal) || arg.StartsWith("--param:", StringComparison.Ordinal))
                    {
                        var paramPart = arg.StartsWith("-p:", StringComparison.Ordinal) ? arg[3..] : arg[8..];
                        var eqIdx = paramPart.IndexOf('=', StringComparison.Ordinal);
                        if (eqIdx > 0)
                            parameters.Add((paramPart[..eqIdx], paramPart[(eqIdx + 1)..]));
                        break;
                    }

                    // Positional arguments: stylesheet first, then source
                    if (stylesheet == null)
                        stylesheet = arg;
                    else if (sourceFile == null)
                        sourceFile = arg;
                    break;
            }
        }

        return new CliOptions
        {
            Stylesheet = stylesheet,
            SourceFile = sourceFile,
            OutputFile = outputFile,
            OutputDir = outputDir,
            InitialTemplate = initialTemplate,
            InitialMode = initialMode,
            Parameters = parameters,
            Timing = timing,
            Trace = trace,
            DryRun = dryRun,
            Stream = stream,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            Verbose = verbose
        };
    }
}
