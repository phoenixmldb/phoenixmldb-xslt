using System.Reflection;
using System.Text.Json;
using PhoenixmlDb.Xslt;

// Each case in cases.tsv: kind <TAB> id <TAB> code path <TAB> input path (relative to examples/).
// kind is xsl (his Transform), sch (his Schematron: SchXslt2 transpile, then validate) or docbook.
// Prints one JSON line per case: {id, rc, out, err}. rc 0 = ran, 2 = engine error, 4 = file missing.
const string Base = "http://workbench.test/examples/";
const string DocbookCdn = "https://cdn.docbook.org/release/xsltng/current/xslt/";
const string Transpiler = "xslt/schematron/schxslt2/transpile-indent-report.xsl";

var asm = Assembly.GetExecutingAssembly();
var files = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ex/", StringComparison.Ordinal)))
{
    using var s = asm.GetManifestResourceStream(name)!;
    using var r = new StreamReader(s);
    files[name[3..].Replace('\\', '/')] = r.ReadToEnd();
}

// Every example file is preloaded at its http URI, so imports, includes and doc() resolve with no
// network, as they would from the workbench's own origin. xslTNG's custom.xsl imports the docbook.org
// CDN copy; that maps to the copy the workbench ships under xslt/docbook/xslt/.
PreloadedResources Preload()
{
    var p = new PreloadedResources();
    foreach (var (rel, text) in files)
    {
        p.Add(new Uri(Base + rel), text);
        const string db = "xslt/docbook/xslt/";
        if (rel.StartsWith(db, StringComparison.Ordinal)) p.Add(new Uri(DocbookCdn + rel[db.Length..]), text);
    }
    return p;
}

async Task<string> Run(string code, string codeRel, string input, string inputRel)
{
    var t = new XsltTransformer { PreloadedResources = Preload() };
    t.MessageListener = (_, _) => { };
    await t.LoadStylesheetAsync(code, new Uri(Base + codeRel));
    t.SetSourceDocumentUri(new Uri(Base + inputRel));
    var primary = await t.TransformAsync(input);
    var outs = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["*** principal result ***"] = primary };
    foreach (var kv in t.SecondaryResultDocuments) outs[kv.Key] = kv.Value;
    return string.Join("\n", outs.Select(kv => $"===== {kv.Key}\n{kv.Value}"));
}

string cases;
using (var cs = new StreamReader(asm.GetManifestResourceStream("cases.tsv")!)) cases = cs.ReadToEnd();
foreach (var line in cases.Split('\n', StringSplitOptions.RemoveEmptyEntries))
{
    var f = line.TrimEnd('\r').Split('\t');
    string id = f[1], codeRel = f[2], inputRel = f[3];
    int rc; string outp = "", err = "";
    if (!files.TryGetValue(codeRel, out var code) || !files.TryGetValue(inputRel, out var input))
    { rc = 4; err = $"missing {codeRel} or {inputRel}"; }
    else
    {
        try
        {
            if (f[0] == "sch")
            {
                var schxsl = await Run(files[Transpiler], Transpiler, code, codeRel);
                schxsl = schxsl[(schxsl.IndexOf('\n') + 1)..];          // drop the "===== principal" frame
                var v = new XsltTransformer { PreloadedResources = Preload() };
                await v.LoadStylesheetAsync(schxsl, new Uri(Base + codeRel));
                v.SetSourceDocumentUri(new Uri(Base + inputRel));
                outp = await v.TransformAsync(input);
            }
            else outp = await Run(code, codeRel, input, inputRel);
            rc = 0;
        }
        catch (Exception e) { rc = 2; err = $"{e.GetType().FullName}: {e.Message}"; }
    }
    // Utf8JsonWriter, not JsonSerializer: the published app is trimmed like the workbench, and
    // reflection-based serialization is what trimming breaks.
    using var ms = new MemoryStream();
    using (var w = new Utf8JsonWriter(ms))
    {
        w.WriteStartObject(); w.WriteString("id", id); w.WriteNumber("rc", rc);
        w.WriteString("out", outp); w.WriteString("err", err); w.WriteEndObject();
    }
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
}
return 0;
