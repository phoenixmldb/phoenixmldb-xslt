using System.Collections;
using System.Reflection;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XPath plans are kept per expression, weakly: an expression made while a transform runs (the target
/// of xsl:evaluate) is a new object in each transform, and its plan must not outlive it. A strong map
/// kept one more entry for every transform of a transformer that is used again.
/// </summary>
[Collection(nameof(SharedPlanStoreSerial))]
public class SharedPlanStoreTests
{
    private static int LiveEntries()
    {
        var ctxType = typeof(XsltTransformer).Assembly.GetType("PhoenixmlDb.Xslt.Engine.DefaultXsltExecutionContext")!;
        var table = (IEnumerable)ctxType.GetField("s_sharedPlans", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // Each live entry is one plan; a store that keeps a map per stylesheet counts its plans.
        var n = 0;
        foreach (var entry in table)
        {
            var value = entry.GetType().GetProperty("Value")!.GetValue(entry);
            n += value is ICollection plans ? plans.Count : 1;
        }
        return n;
    }

    private static void FullCollect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public async Task PlansOfEvaluatedExpressions_DoNotAccumulateAcrossTransforms()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:for-each select="1 to 3"><xsl:evaluate xpath="'. * 7919'" context-item="."/></xsl:for-each></xsl:template>
            </xsl:stylesheet>
            """);
        (await transformer.TransformAsync("<r/>")).Should().Contain("23757"); // 3 * 7919: the evaluate ran
        FullCollect();
        var before = LiveEntries();

        for (var i = 0; i < 20; i++)
            await transformer.TransformAsync("<r/>");
        FullCollect();

        (LiveEntries() - before).Should().BeLessThan(10, "each transform's evaluated expression is a new object, and its plan goes with it");
    }
}

[CollectionDefinition(nameof(SharedPlanStoreSerial), DisableParallelization = true)]
public sealed class SharedPlanStoreSerial;
