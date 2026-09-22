using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Static parameters and variables must come into scope in DECLARATION ORDER (XSLT 3.0 §3.9),
/// with an included module's declarations taking effect at the point of its xsl:include.
///
/// Four separate defects in that one path each produced the same symptom — a static variable
/// that quietly never got registered, so the next declaration to reference it failed with
/// "Variable $x is not defined" and the shadow attribute that depended on it was left
/// unresolved. Between them they took out the whole of W3C <c>fn/system-property-gen</c>
/// (0 of 166), which chains all four shapes in one stylesheet.
///
/// Each test below names the defect it pins and is built so the old and the new code predict
/// OPPOSITE results, not merely different ones.
/// </summary>
public class StaticDeclarationScopeOrderTests
{
    /// <summary>
    /// Runs a stylesheet written to a temp directory. <paramref name="modules"/> is
    /// name → content; "main.xsl" is the entry point.
    /// </summary>
    private static async Task<string> Run(
        Dictionary<string, string> modules,
        Dictionary<string, string>? staticParams = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "phx-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, content) in modules)
                await File.WriteAllTextAsync(Path.Combine(dir, name), content);

            var main = Path.Combine(dir, "main.xsl");
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(
                await File.ReadAllTextAsync(main), new Uri(main), staticParams, null);
            transformer.SetInitialTemplate("main", null);
            return await transformer.TransformAsync("<dummy/>");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Head =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">\n";

    // ---- defect 1: included modules were collected BEFORE the principal module's own params --

    /// <summary>
    /// The included module references a static param declared ABOVE the xsl:include. Every
    /// include used to be swept first, which put the principal module's own params after the
    /// modules that reference them — so a correctly ordered stylesheet failed.
    /// </summary>
    [Fact]
    public async Task Included_module_sees_a_param_declared_before_the_include()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"SCOPE\" static=\"yes\" select=\"'alpha'\"/>\n" +
                "  <xsl:include href=\"mod.xsl\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$DERIVED}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
            ["mod.xsl"] = Head +
                "  <xsl:variable name=\"DERIVED\" static=\"yes\" select=\"concat($SCOPE,'-seen')\"/>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<alpha-seen>");
    }

    /// <summary>
    /// A module included by an included module contributes its declarations too. The old sweep
    /// looked exactly one level deep, so this was invisible however it was ordered.
    /// </summary>
    [Fact]
    public async Task Declarations_of_a_transitively_included_module_are_collected()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"SCOPE\" static=\"yes\" select=\"'alpha'\"/>\n" +
                "  <xsl:include href=\"mid.xsl\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$DEEP}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
            ["mid.xsl"] = Head + "  <xsl:include href=\"leaf.xsl\"/>\n</xsl:stylesheet>\n",
            ["leaf.xsl"] = Head +
                "  <xsl:variable name=\"DEEP\" static=\"yes\" select=\"concat($SCOPE,'-deep')\"/>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<alpha-deep>");
    }

    /// <summary>
    /// GUARD, not a defect test: this passes before the change as well, and is here because the
    /// first version of the declaration-order fix BROKE it (W3C attr/static static-022).
    ///
    /// An IMPORTED module has lower import precedence, so its declaration must lose to the
    /// importing module's whatever their relative position — the xsl:import here is written
    /// BELOW the declaration it must not override. Import and include therefore cannot be
    /// handled alike: this case needs imports hoisted, and
    /// <see cref="Included_module_sees_a_param_declared_before_the_include"/> needs includes
    /// left in document order.
    /// </summary>
    [Fact]
    public async Task An_imported_declaration_does_not_override_the_importing_module()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:variable name=\"P\" static=\"yes\" select=\"'high'\"/>\n" +
                "  <xsl:import href=\"low.xsl\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$P}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
            ["low.xsl"] = Head +
                "  <xsl:variable name=\"P\" static=\"yes\" select=\"'low'\"/>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<high>");
    }

    // ---- defect 2: externally supplied params were applied AFTER the declarations ------------

    /// <summary>
    /// A supplied value outranks the declared default, so a later declaration that reads the
    /// param must see the SUPPLIED value. External params used to be merged in after every
    /// declaration had already been evaluated, so <c>$DERIVED</c> was computed from the default
    /// while the shadow attributes around it were resolved against the supplied one — the two
    /// halves of the same stylesheet disagreeing about the same parameter.
    /// </summary>
    [Fact]
    public async Task A_supplied_param_is_visible_to_the_declarations_that_read_it()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"SCOPE\" static=\"yes\" select=\"'alpha'\"/>\n" +
                "  <xsl:variable name=\"DERIVED\" static=\"yes\" select=\"concat($SCOPE,'-seen')\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$DERIVED}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
        }, new() { ["SCOPE"] = "'beta'" });

        output.Should().Contain("<beta-seen>");
        output.Should().NotContain("alpha", "the declared default must not survive a supplied value");
    }

    /// <summary>Guard: with nothing supplied, the declared default is still what applies.</summary>
    [Fact]
    public async Task The_declared_default_still_applies_when_nothing_is_supplied()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"SCOPE\" static=\"yes\" select=\"'alpha'\"/>\n" +
                "  <xsl:variable name=\"DERIVED\" static=\"yes\" select=\"concat($SCOPE,'-seen')\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$DERIVED}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<alpha-seen>");
    }

    // ---- defect 3: a declaration carrying its expression in _select was never registered ------

    /// <summary>
    /// Collection read only the <c>select</c> attribute, so a static variable whose expression
    /// arrives through the shadow attribute <c>_select</c> was registered nowhere and the next
    /// declaration to reference it failed.
    /// </summary>
    [Fact]
    public async Task A_static_variable_declared_with_shadow_select_is_in_scope_for_the_next_one()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"PFX\" static=\"yes\" select=\"'p'\"/>\n" +
                "  <xsl:variable name=\"A\" static=\"yes\" _select=\"'{$PFX}q'\"/>\n" +
                "  <xsl:variable name=\"B\" static=\"yes\" select=\"concat($A,'-b')\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$B}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<pq-b>");
    }

    // ---- defect 4: any expression BEGINNING with $ was read as a bare variable name -----------

    /// <summary>
    /// <c>{$PFX || 'q'}</c> was looked up as a static variable literally named
    /// <c>PFX || 'q'</c>. Nothing is called that, so the whole shadow attribute was reported
    /// unresolvable — a silent degradation, since a genuinely unknown variable produces the
    /// same answer.
    /// </summary>
    [Fact]
    public async Task A_shadow_expression_that_merely_starts_with_a_variable_is_evaluated()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"PFX\" static=\"yes\" select=\"'p'\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$PFX || 'q'}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<pq>");
    }

    /// <summary>
    /// Guard for the same change: a BARE reference still takes the direct lookup path, so
    /// narrowing that branch did not simply route everything through the evaluator.
    /// </summary>
    [Fact]
    public async Task A_bare_variable_reference_still_resolves()
    {
        var output = await Run(new()
        {
            ["main.xsl"] = Head +
                "  <xsl:param name=\"PFX\" static=\"yes\" select=\"'plain'\"/>\n" +
                "  <xsl:template name=\"main\">\n" +
                "    <out><xsl:element _name=\"{$PFX}\">x</xsl:element></out>\n" +
                "  </xsl:template>\n" +
                "</xsl:stylesheet>\n",
        });

        output.Should().Contain("<plain>");
    }
}
