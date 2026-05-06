using System.Globalization;
using System.Text;
using System.Xml;

namespace PhoenixmlDb.Xslt.Bench;

/// <summary>
/// Generates synthetic XML documents shaped like the kind of payloads we expect from
/// Microsoft Dataverse / Dynamics SOAP services: a wide, deeply-nested
/// <c>xs:schema</c>-style document with many <c>complexType</c> declarations,
/// nested <c>sequence</c>/<c>element</c> structures, and attribute-heavy nodes.
/// </summary>
/// <remarks>
/// <para>
/// The generator does not aim to faithfully reproduce a specific Dataverse schema —
/// the goal is to stress the same code paths that real Dataverse documents stress:
/// many sibling elements at the same depth, deep-but-not-pathological nesting,
/// attribute-rich nodes, and a heavy mix of qualified names from a small set of
/// namespaces. That's representative enough to predict whether perf changes help
/// the real workload, while remaining deterministic and reproducible.
/// </para>
/// <para>
/// Output is sized to roughly match the requested target byte count by tuning the
/// number of top-level <c>xs:complexType</c> entries. Each complex type is itself
/// shaped consistently, so the wall-time cost scales linearly with size.
/// </para>
/// </remarks>
internal static class LargeDocumentSynthesizer
{
    /// <summary>
    /// Synthesizes an XSD-shaped XML document of approximately
    /// <paramref name="targetBytes"/> bytes and writes it to <paramref name="outputPath"/>.
    /// Returns the actual size in bytes (typically within ±5% of the target).
    /// </summary>
    public static long Synthesize(string outputPath, long targetBytes)
    {
        // Each complex type contributes roughly this many bytes after serialization.
        // Tuned empirically against the structure below — adjust if the structure changes.
        const int avgBytesPerComplexType = 1300;
        var typeCount = (int)(targetBytes / avgBytesPerComplexType);

        using var fileStream = File.Create(outputPath);
        var settings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Indent = false,           // Real Dataverse responses aren't indented; matches real wire shape
            CloseOutput = true,
        };
        using var writer = XmlWriter.Create(fileStream, settings);

        WriteRoot(writer, typeCount);
        writer.Flush();
        return fileStream.Length;
    }

    private static void WriteRoot(XmlWriter w, int typeCount)
    {
        w.WriteStartDocument();
        w.WriteStartElement("xs", "schema", "http://www.w3.org/2001/XMLSchema");
        w.WriteAttributeString("xmlns", "tns", null, "http://schemas.dataverse.local/contracts/2024/11");
        w.WriteAttributeString("xmlns", "msc", null, "http://schemas.microsoft.com/2003/10/Serialization/Arrays");
        w.WriteAttributeString("targetNamespace", "http://schemas.dataverse.local/contracts/2024/11");
        w.WriteAttributeString("elementFormDefault", "qualified");

        for (var i = 0; i < typeCount; i++)
            WriteComplexType(w, i);

        w.WriteEndElement();
        w.WriteEndDocument();
    }

    private static void WriteComplexType(XmlWriter w, int index)
    {
        var name = "Entity" + index.ToString(CultureInfo.InvariantCulture);
        w.WriteStartElement("xs", "complexType", null);
        w.WriteAttributeString("name", name);

        w.WriteStartElement("xs", "annotation", null);
        w.WriteStartElement("xs", "documentation", null);
        w.WriteString($"Synthetic entity #{index} — represents a Dataverse contract for benchmarking.");
        w.WriteEndElement(); // documentation
        w.WriteEndElement(); // annotation

        w.WriteStartElement("xs", "sequence", null);

        // Mix of element types: simple-typed, complex-typed, with constraints
        WriteSimpleElement(w, "Id", "tns:GuidValue", required: true);
        WriteSimpleElement(w, "Name", "xs:string", required: true);
        WriteSimpleElement(w, "Description", "xs:string", required: false);
        WriteSimpleElement(w, "CreatedOn", "xs:dateTime", required: true);
        WriteSimpleElement(w, "ModifiedOn", "xs:dateTime", required: false);
        WriteSimpleElement(w, "RecordCount", "xs:int", required: false);
        WriteSimpleElement(w, "IsActive", "xs:boolean", required: true);

        // Nested complex element with another sequence inside (depth ~3)
        w.WriteStartElement("xs", "element", null);
        w.WriteAttributeString("name", "Owner");
        w.WriteAttributeString("minOccurs", "0");
        w.WriteStartElement("xs", "complexType", null);
        w.WriteStartElement("xs", "sequence", null);
        WriteSimpleElement(w, "UserId", "tns:GuidValue", required: true);
        WriteSimpleElement(w, "DisplayName", "xs:string", required: false);
        WriteSimpleElement(w, "PrincipalType", "xs:string", required: false);
        w.WriteEndElement(); // sequence
        w.WriteEndElement(); // complexType
        w.WriteEndElement(); // element Owner

        // Repeated child collection (Dataverse-style related-record list)
        w.WriteStartElement("xs", "element", null);
        w.WriteAttributeString("name", "Attributes");
        w.WriteAttributeString("minOccurs", "0");
        w.WriteStartElement("xs", "complexType", null);
        w.WriteStartElement("xs", "sequence", null);
        w.WriteStartElement("xs", "element", null);
        w.WriteAttributeString("name", "KeyValuePair");
        w.WriteAttributeString("maxOccurs", "unbounded");
        w.WriteAttributeString("minOccurs", "0");
        w.WriteStartElement("xs", "complexType", null);
        w.WriteStartElement("xs", "sequence", null);
        WriteSimpleElement(w, "Key", "xs:string", required: true);
        WriteSimpleElement(w, "Value", "xs:anyType", required: false);
        w.WriteEndElement(); // sequence
        w.WriteEndElement(); // complexType (KeyValuePair)
        w.WriteEndElement(); // element KeyValuePair
        w.WriteEndElement(); // sequence
        w.WriteEndElement(); // complexType (Attributes)
        w.WriteEndElement(); // element Attributes

        w.WriteEndElement(); // sequence (top of the entity)
        w.WriteEndElement(); // complexType
    }

    private static void WriteSimpleElement(XmlWriter w, string name, string type, bool required)
    {
        w.WriteStartElement("xs", "element", null);
        w.WriteAttributeString("name", name);
        w.WriteAttributeString("type", type);
        if (!required)
            w.WriteAttributeString("minOccurs", "0");
        w.WriteAttributeString("nillable", required ? "false" : "true");
        w.WriteEndElement();
    }
}
