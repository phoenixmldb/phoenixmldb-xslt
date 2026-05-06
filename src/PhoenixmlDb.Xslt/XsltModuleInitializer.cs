using System.Runtime.CompilerServices;
using PhoenixmlDb.XQuery.Functions;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Auto-registers <see cref="XsltTransformProvider"/> with <see cref="TransformFunction.Provider"/>
/// when the assembly is loaded, so any application that references PhoenixmlDb.Xslt gets
/// <c>fn:transform()</c> wired up automatically — no explicit
/// <c>TransformFunction.Provider = new XsltTransformProvider()</c> call needed.
/// </summary>
/// <remarks>
/// <para>
/// The runtime invokes a <c>[ModuleInitializer]</c> method exactly once, the first time any
/// type in this assembly is touched. That happens when the application loads
/// <c>PhoenixmlDb.Xslt</c> — typically by referencing it. As long as the assembly is in the
/// build output (e.g. via PackageReference or ProjectReference), <c>fn:transform()</c> is
/// available.
/// </para>
/// <para>
/// Callers who explicitly set <see cref="TransformFunction.Provider"/> later still win; the
/// initializer only fires on first load and only assigns when no provider is registered.
/// </para>
/// </remarks>
internal static class XsltModuleInitializer
{
    // CA2255: ModuleInitializer is documented as for "application code or advanced source-generator
    // scenarios," but registering an XSLT provider in fn:transform is exactly the kind of
    // assembly-load-time wiring it's designed for here — the alternative (every consumer remembering
    // to call TransformFunction.Provider = new XsltTransformProvider()) is the bug Martin Honnen hit.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:ModuleInitializer attribute should not be used in libraries", Justification = "Auto-registers XSLT provider with fn:transform on assembly load — see class summary.")]
    [ModuleInitializer]
    internal static void Init()
    {
        TransformFunction.Provider ??= new XsltTransformProvider();
    }
}
