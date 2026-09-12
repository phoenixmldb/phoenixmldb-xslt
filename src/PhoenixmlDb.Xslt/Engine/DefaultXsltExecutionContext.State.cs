using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{

    /// <summary>
    /// Sentinel stamped on <see cref="XdmNode.CopySourceBaseUri"/> of a COPIED DOCUMENT node
    /// whose source document had NO base URI (dm:base-uri = ()). Its sole purpose is to mark
    /// the copy as "base-URI-less by preservation, not by omission" so the sequence-materialize
    /// re-stamp in <c>AddAccItem</c> (guarded on <c>CopySourceBaseUri == null</c>) does NOT
    /// overwrite the copy's null base with the construction (stylesheet) base — a genuinely
    /// constructed document/LRE, which never passes through the doc-copy sites, still gets the
    /// construction base. The VALUE is never read as a base URI: <see cref="XsltTransformEngine"/>'s
    /// base-uri computation returns <c>doc.BaseUri</c> (null → ()) for document nodes and only
    /// consults <c>CopySourceBaseUri</c> for ELEMENT nodes, and this sentinel is only ever set on
    /// XdmDocument copies (never propagated to element children). See fn/base-uri review finding #2.
    /// </summary>
    private const string DocCopyNullSourceBaseSentinel = "copy-of-base-uri-less-document";


    internal readonly XsltStylesheet _stylesheet;

    private readonly TemplateIndex _templateIndex;

    private readonly Stack<Scope> _scopes = new();

    private readonly Stack<object?> _contextItems = new();

    private readonly Stack<(int position, int last)> _contextPositions = new();

    private readonly Stack<object?> _currentItems = new(); // For XSLT current() function

    // Direct appends to `_output` are now confined to buffer-level string passes only
    // (attribute dedup/prefix-fixup re-emission, buffer save/restore swaps, result-document
    // JSON/text serialization, suppress-indentation). ALL structured emission — element/
    // attribute/namespace/text/comment/PI — goes through `_sink`. Do not add new structured
    // emission as a raw `_output.Append`; route it through `_sink` so the future
    // TreeConstructor-backed sink (SP-B) receives it as a node event.
    private readonly StringBuilder _output;

    // Emission abstraction (SP-A). All structured emission routes through this sink;
    // `StringOutputSink` reproduces today's markup byte-for-byte (gated by SerializationMatrixTests).
    // Deliberately typed as the interface, not StringOutputSink: a second implementation
    // (TreeConstructor-backed node builder) is planned, per SP1 decomposition.
#pragma warning disable CA1859
    private readonly IOutputSink _sink;

#pragma warning restore CA1859

    // SP-B: when non-null, from-scratch element emitters build XDM nodes directly into
    // _nodeStore via this constructor (in addition to the unchanged string emission, so the
    // legacy serialize-reparse path still produces the production value). Installed for the
    // duration of an as="element()"-typed body's execution by the differential dual-run seam,
    // and only when TempTreeDifferential.Enabled — production (toggle off) never sets it, so
    // this field is inert and behavior is byte-unchanged.
    private TreeConstructor? _activeTreeConstructor;


    // SP-B slice 4: set while an active constructor's body executed content that could NOT be
    // faithfully routed into the node tree (an attribute whose prefix could not be resolved, or
    // — reported empirically by the differential — a node-producing construct that serializes to
    // the string buffer without a routed emitter, e.g. xsl:copy-of / built-in deep copy). When
    // set, RunTreeConstructorDifferential skips the structural comparison for that body: the live
    // tree is known-incomplete, so a divergence would be a false positive, not a real parity bug.
    // Reset each time the body seam installs a fresh constructor.
    private bool _tcFragmentIncomplete;


    // SP-C slice 1: set only while an xsl:copy-of of source nodes serializes to the string buffer
    // AFTER its clones were already routed into the active constructor (see CopyOfCoreAsync). The
    // live tree is NOT missing those children, so SerializeNode's MarkTcIncompleteIfActive must be
    // suppressed — the body stays a migrated candidate and can flip — while the serialization still
    // runs to keep the reparse fallback / flip count-guard / differential a byte-parity target.
    private bool _suppressTcIncomplete;


    // SP-C targeted: true only while an untyped-RTF-flip constructor is installed (the copy-of-only
    // body seam). Gates the divergent copy-of routing (mixed element+text sequences, and clones that
    // would over-declare relative to the enclosing frame) so that path never runs in the typed as=
    // seam or nested contexts, which must stay byte-parity with the reparse.
    private bool _untypedRtfFlipActive;


    // SP-C targeted: set when a copy-of routed into the untyped-RTF-flip constructor and the node
    // build is the AUTHORITATIVE result that intentionally diverges from the serialize-reparse
    // oracle (XSLT 3.0 §11.7.2 namespace fixup the reparse gets wrong — W3C copy-1220/1221). When
    // set, the flip is delivered without the IsUntypedRtfFlipByteParitySafe veto and the differential
    // comparison for that body is skipped (the reparse is a known-wrong oracle for this shape).
    private bool _untypedRtfFlipDivergent;


    internal readonly XsltTransformOptions _options;

    internal readonly XdmInMemoryStore? _nodeStore;

    private readonly PhoenixmlDb.XQuery.Functions.FunctionLibrary _functionLibrary;

    private readonly XsltDocumentResolver _documentResolver;

    private readonly PhoenixmlDb.XQuery.Security.PolicyEnforcingResolver? _policyResolver;

    private readonly PhoenixmlDb.XQuery.ISchemaProvider? _schemaProvider;


    /// <summary>
    /// Per-package overlay for package-local global variables that could not occupy the
    /// principal QName-keyed <see cref="GlobalVariables"/> slot (a same-named global from
    /// another package won it). Keyed by (owning package, variable name). A variable
    /// reference resolves here FIRST when executed inside its owning package, so a diamond
    /// override / different-version import (use-package-175 / use-package-176) sees each
    /// package's own value. Empty (and never consulted) for non-package stylesheets.
    /// </summary>
    internal readonly Dictionary<(Ast.XsltStylesheet Package, QName Name), object?> _packageShadowGlobals = new();


    /// <summary>
    /// The package whose package-local global variable is currently being initialized, so
    /// that CurrentComponentPackage()/GetCurrentPackageStylesheet() report it while no
    /// template/function is on the stack (shadow-global eager evaluation). Null otherwise.
    /// </summary>
    internal Ast.XsltStylesheet? _currentGlobalPackage;


    // Cache optimized query plans by expression identity to avoid re-optimizing in loops
    private readonly Dictionary<XQueryExpression, PhoenixmlDb.XQuery.Execution.ExecutionPlan> _planCache = new(ReferenceEqualityComparer.Instance);


    // Attribute collection mode for xsl:element
    // Use a stack to support nested element construction
    private readonly Stack<StringBuilder> _collectedAttributesStack = new();

    private int _textContentDepth; // >0 when collecting simple text content (attribute/comment/PI body)

    private int _attributeContentDepth; // >0 when collecting attribute value content (no space joining)

    private int _documentNodeDepth; // >0 when inside xsl:document (cannot add attributes/namespaces)

    private SourceLocation? _currentInstructionLocation; // Most-recent attribute-emitting instruction location, for XTDE0410/0420 messages


    /// <summary>
    /// Location of the XPath expression whose evaluation raised the exception currently in
    /// flight, for binding <c>$err:module</c> and <c>$err:line-number</c> in xsl:catch.
    /// </summary>
    /// <remarks>
    /// fn:error() raises an XQueryException, which carries no source location — only
    /// XsltException does, so xsl:catch previously left both variables empty for every error a
    /// stylesheet raised itself. The evaluator already resolves the location to build its
    /// "[module:line] " diagnostic prefix; this records the same value structurally instead of
    /// only rendering it into a string. It is cleared on entry to every xsl:try so a handler can
    /// never read a location left behind by an earlier, unrelated failure.
    /// </remarks>
    private SourceLocation? _lastExpressionErrorLocation;

#pragma warning restore

    // Offset in `_output` below which content is considered "outer" — owned by an enclosing
    // scope, not by anything currently executing. Updated by `ScopedOutputBuffer` to the
    // saved length on entry and restored to the previous value on dispose.
    //
    // Why it exists: the historical pattern was to do `_output.ToString()` + `_output.Clear()`
    // before executing a body that could produce e.g. an `xsl:attribute`, then restore via
    // `_output.Clear()` + `_output.Append(savedOutput)`. The `Clear()` step had a functional
    // side effect beyond restoration: it made `_output.Length == 0` inside the body, which
    // is what the XTDE0410 / XTDE0420 / simple-content-spacing checks compare against to
    // decide "has any child content been produced yet?". When we replace the save/restore
    // pattern with a length-cursor (eliminating the O(N) string copy on big documents), we
    // need a separate signal for "logical start of the current scope" so those checks still
    // see "no children yet." Otherwise an inner xsl:attribute fires a spurious XTDE0410
    // because outer content makes `_output.Length > 0`.
    private int _outputLogicalStart;


    /// <summary>
    /// Construction state saved on entry to an <c>as=</c>-typed sequence-constructor body,
    /// restored on exit. See <see cref="EnterTypedBody"/>.
    /// </summary>
    private readonly struct TypedBodyState
    {
        public int LogicalStart { get; init; }
        public int DocumentNodeDepth { get; init; }
        public int TextContentDepth { get; init; }
        public int AttributeContentDepth { get; init; }
        public int SerializingElementDepth { get; init; }
        public bool CollectTextAsSequenceItems { get; init; }
        public bool LastResultWasAtomic { get; init; }
        public List<StringBuilder> CollectedAttributes { get; init; }
        public TreeConstructor? ActiveTreeConstructor { get; init; }
        public bool TcFragmentIncomplete { get; init; }
        public bool SuppressTcIncomplete { get; init; }
        public bool UntypedRtfFlipActive { get; init; }
        public bool UntypedRtfFlipDivergent { get; init; }
    }


    /// <summary>
    /// Establishes the construction context shared by every <c>as=</c>-typed body — the seams
    /// behind <c>xsl:variable</c>, <c>xsl:param</c>, <c>xsl:with-param</c>, a parameter's
    /// default value, <c>xsl:function</c> and a typed template.
    /// </summary>
    /// <remarks>
    /// These seams each grew their own save/reset/restore block, and they diverged. Every one of
    /// the five capture bugs found while unblocking XSpec was a field one seam handled and
    /// another did not:
    /// <list type="bullet">
    /// <item><c>_documentNodeDepth</c> — variable had it, param/with-param/default did not, so an
    /// <c>xsl:attribute</c> inside <c>as="attribute()*"</c> raised a spurious XTDE0420.</item>
    /// <item><c>_collectTextAsSequenceItems</c> — variable had it, function did not, so a typed
    /// function returned a string where the caller declared <c>node()*</c> (XTTE0780).</item>
    /// <item><c>_outputLogicalStart</c> — variable had it, function did not, so a nested flush
    /// truncated the caller's buffer and crashed with ArgumentOutOfRangeException.</item>
    /// </list>
    /// Centralising the context makes that class of divergence unrepresentable. Result ASSEMBLY
    /// stays per-seam: each genuinely differs in how it turns the captured channels into a value,
    /// and unifying that is a separate change.
    ///
    /// A typed body constructs a SEQUENCE, not a temporary tree (XSLT 3.0 §9.3), so the document
    /// depth is neutralized — except for <c>as="document-node()"</c>, whose body really is
    /// wrapped in a document node and must keep the guard armed.
    /// </remarks>
    /// <summary>
    /// The default return type of an <c>xsl:function</c> that declares no <c>as=</c>:
    /// <c>item()*</c> (XSLT 3.0 §10.3).
    /// </summary>
    private static readonly XdmSequenceType ItemStarSequenceType =
        new() { ItemType = ItemType.Item, Occurrence = Occurrence.ZeroOrMore };


    /// <summary>
    /// Optional external sink for incremental streaming output. When non-null, the
    /// <see cref="StreamingXmlProcessor"/> calls <see cref="DrainStreamingOutputAsync"/>
    /// at each safe event boundary to flush <see cref="_output"/> here and reset its
    /// length to zero, bounding peak memory for large streamed transforms. The sink is
    /// only meaningful when the context is driving a streaming transform; non-streaming
    /// callers leave it null.
    /// </summary>
    internal TextWriter? _streamingOutputSink;


    // When a template/function body is being captured for `as=` type-checking, we have two
    // parallel output channels: `_output` (LREs, text) and `_sequenceAccumulator`
    // (xsl:attribute, xsl:sequence, xsl:document). To recover document order at result
    // assembly, an `AsBodyCapture` records the `_output` offset at the moment each
    // accumulator item is added. Position recording only fires when the current accumulator
    // is the capture's accumulator — items routed through *inner* accumulators (e.g. an
    // `xsl:variable` body that allocates its own accumulator) don't pollute the outer
    // capture's position list.
    private sealed class AsBodyCapture
    {
        public required List<object?> Accumulator { get; init; }
        public required int OutputBaseLen { get; init; }
        public List<int> Positions { get; } = [];

        /// <summary>
        /// For each accumulator item, the output offset it consumed UP TO. Normally equal to
        /// its <see cref="Positions"/> entry — the item contributed no serialized output.
        /// </summary>
        /// <remarks>
        /// WriteTextItem deliberately writes text to BOTH the accumulator and _output inside a
        /// function body, so text and literal result elements keep their source order when the
        /// two are woven back together. The weave assumed they were disjoint: it inserted the
        /// item at its recorded offset and then emitted the rest of the output, which re-emitted
        /// the very same text. A template declared as="text()" returned TWO items — one
        /// TextNodeItem and one String, identical content — and failed XTTE0505.
        ///
        /// Recording the end offset lets the weave skip the region an item already covers.
        /// </remarks>
        public List<int> ConsumedTo { get; } = [];

        /// <summary>
        /// Depth of <c>_collectedAttributesStack</c> when this typed body began.
        /// </summary>
        /// <remarks>
        /// Distinguishes "an element started OUTSIDE this template is still collecting its
        /// attributes" from "the body itself started an element". Only the first case means a
        /// copied attribute is part of the template's RETURN VALUE rather than content of an
        /// element the body is building. Without the distinction a typed template that copies
        /// an attribute silently contributed it to the caller's element and returned nothing.
        /// </remarks>
        public required int AttrDepthAtStart { get; init; }
    }


    private AsBodyCapture? _currentAsBodyCapture;

    /// <summary>
    /// Buffered-subtree root id → the streamed element it was materialised from. A template body
    /// that needs the whole subtree runs on a copy with fresh node ids, which carries none of the
    /// accumulator values the streaming pass recorded; this is how the pre-descent value is found
    /// again. See <see cref="TryGetStreamedAccumulatorBefore"/>.
    /// </summary>
    internal Dictionary<NodeId, NodeId>? _bufferedSubtreeOrigin;


    private int _textOutputModeDepth; // >0 when inside method="text" result-document; text from xsl:sequence/value-of gets sentinel-escaped to protect from StripXmlMarkup

    private int _insideXslEvaluateDepth; // >0 when inside xsl:evaluate; XSLT-specific functions raise XTDE3160

    private int _templateDepth; // Nesting depth for trace output

    private Dictionary<string, string>? _evaluateNamespaceBindings; // Namespace bindings override during xsl:evaluate

    private readonly HashSet<QName> _activeAttributeSets = new(); // Detect circular attribute set references (XTDE0640)

    // Tracks the attribute-set currently being expanded so that use-attribute-sets="xsl:original"
    // within an overriding xsl:attribute-set can resolve to its overridden component.
    private readonly Stack<XsltAttributeSet> _currentAttributeSetStack = new();

    internal readonly HashSet<QName> _globalsBeingEvaluated = new(); // Detect circular global variable/param references (XTDE0640)


    // Streaming execution state: when true, built-in template recursion into children is
    // a no-op because the streaming loop (StreamingXmlProcessor) handles child dispatch.
    internal bool _isStreamingExecution;

    // Stack of element qualified names whose start tags have been written during streaming
    // but whose end tags must be deferred until the StreamingXmlProcessor sees EndElement.
    internal readonly Stack<string> _streamingOpenElements = new();

    // Active streaming processor reference for triggering streaming from apply-templates
    // inside xsl:source-document Content bodies.
    internal StreamingXmlProcessor? _activeStreamingProcessor;

    internal XmlReader? _activeStreamingReader;

    internal CancellationToken _activeStreamingCancellationToken;

    internal IReadOnlyList<StreamWatcher>? _activeStreamWatchers;


    /// <summary>
    /// Set by streaming-aware operators (notably xsl:for-each-group) when they consumed
    /// an event from <see cref="_activeStreamingReader"/> that the
    /// <see cref="StreamingXmlProcessor"/> still needs to process. The processor checks
    /// this flag at the top of its event loop; if true, it skips its own
    /// <c>ReadAsync</c> and processes the current reader position, then clears the flag.
    /// Use case: for-each-group consumes the parent's EndElement to detect end-of-children;
    /// the processor still needs to do its EndElement bookkeeping (pop ancestor stack,
    /// fire end-phase accumulators, write deferred close tag).
    /// </summary>
    internal bool _streamingDeferReadOnNextIteration;


    /// <summary>
    /// Parameters forwarded into the processor-driven streamed descent by an
    /// <c>xsl:apply-templates</c> carrying <c>xsl:with-param</c> (si-apply-templates-008/009).
    /// The whole-document forward pass is driven by <see cref="StreamingXmlProcessor"/>,
    /// which dispatches every node through <see cref="MatchAndExecuteStreamingNodeAsync"/>
    /// without a per-call param list. The built-in shallow-copy template rule forwards all
    /// caller params (both tunnel and non-tunnel) UNCHANGED at every level of the streamed
    /// descent, so a single ambient param set is correct for the whole pass. Set before
    /// <c>proc.ProcessAsync</c> / the inline <see cref="ApplyTemplatesStreamingAsync"/> loop
    /// and restored after; <see cref="MatchAndExecuteStreamingNodeAsync"/> binds it into the
    /// matched rule's scope (mirrors the non-streaming apply-templates binding).
    /// </summary>
    internal List<XsltWithParam> _streamingForwardedParams = [];


    /// <summary>
    /// Set by <see cref="MatchAndExecuteStreamingNodeAsync"/> when the matched
    /// template required snapshot()/copy-of() of the matched subtree, so the
    /// engine consumed events from <see cref="_activeStreamingReader"/> via
    /// <c>ReadSubtree()</c>, parsed them into an in-memory XdmElement, and ran
    /// the body against that buffered tree. The processor checks this flag
    /// after MatchAndExecute returns: when set it skips ancestor-stack push
    /// and fires End-phase accumulator + watcher events manually (the matching
    /// EndElement event was consumed by ReadSubtree and won't arrive separately).
    /// </summary>
    internal bool _streamingSubtreeBufferConsumed;


    /// <summary>
    /// One entry on <see cref="_streamingDeferredExecutions"/>. Captures everything
    /// needed to run a template body at the parent EndElement event with watcher
    /// results substituted into consuming aggregates.
    /// </summary>
    internal sealed class DeferredStreamingExecution
    {
        public required Ast.XsltTemplate Template;
        public required object Element;          // the matched node (XdmElement)
        public required QName? Mode;
        public required int Position;
        public required int ParentDepth;          // streaming reader depth where this fired
        public required IReadOnlyList<StreamWatcher> Watchers;
        public IReadOnlyList<StreamWatcher>? PriorActiveWatchers;
    }


    /// <summary>
    /// Stack of templates whose bodies were deferred because they contain consuming
    /// aggregates (count(*), sum(*), etc.). Watchers accumulate as the streaming
    /// processor reads the matched element's children; on the parent EndElement,
    /// the body is executed with <see cref="_activeStreamWatchers"/> set so the
    /// existing TryResolveFromWatchers path substitutes accumulated values into the
    /// expressions.
    /// </summary>
    internal readonly Stack<DeferredStreamingExecution> _streamingDeferredExecutions = new();


    internal Dictionary<QName, GlobalDeclaration>? _pendingGlobals; // Lazy global init: declarations not yet evaluated

    private bool _lastResultWasAtomic; // Track adjacent atomic values for space separation

    private string? _itemSeparatorOverride; // When set, overrides default space separator between sequence items


    // Ambient operand-usage role (XSLT 3.0 §19.6/§19.7), threaded as the executor descends.
    // Root default is Absorption so any path reaching the serializer without an explicit push
    // behaves as before (atomic separators preserved). Set to Transmission when descending into
    // element/document complex content (§5.7.1); the serializer's atomic-separator insertion
    // gates on this so §5.7.1 content is concatenated, not space-separated.
    private PostureContext _posture = new();


    // §5.7.2 sequence normalization: when an explicit (non-absent) item-separator is in effect
    // on the TOP LEVEL of a result sequence (xsl:result-document / principal output content, not
    // inside any constructed element/attribute/text-content), a copy of the separator is inserted
    // between EVERY pair of adjacent items — not only between adjacent atomic values (so comment
    // and PI nodes participate too). This flag records whether a top-level item has already been
    // emitted, so the next one is preceded by the separator.
    private bool _topLevelItemEmitted;

    private List<QName>? _principalOutputCharacterMaps; // Character maps from xsl:result-document targeting principal output

    private readonly Stack<string?> _baseUriStack = new(); // Effective base URI for node construction


    private readonly Stack<string?> _staticBaseUriStack = new(); // Static base URI for static-base-uri() function


    private readonly Stack<SourceLocation?> _locationStack = new();

    private bool _simpleContentLastWasNode; // Track node→text transitions for space insertion in simple content


    // Map/array construction stacks
    private readonly Stack<IDictionary<object, object?>> _mapBuildStack = new();

    private readonly Stack<List<object?>> _arrayBuildStack = new();


    // Track current template for next-match/apply-imports
    private XsltTemplate? _currentTemplate;

    private QName? _currentMode;


    // Recursion depth guard
    private int _recursionDepth;

    private const int MaxRecursionDepth = 1200;


    /// <summary>
    /// Stack tracking the current xsl:function being executed, for xsl:original resolution.
    /// When a function overrides a package component, its OriginalFunction chain is used
    /// to resolve calls to xsl:original.
    /// </summary>
    internal readonly Stack<XsltFunction> _currentXsltFunctionStack = new();


    /// <summary>QName of the xsl:original pseudo-function used for package function overrides.</summary>
    private static readonly QName XslOriginalFunctionName = new(NamespaceId.Xslt, "original");


    /// <summary>
    /// Stack tracking the current named template being executed, for xsl:original resolution.
    /// </summary>
    internal readonly Stack<XsltTemplate> _currentTemplateStack = new();


    // Memoized stylesheet function results — cache="yes" and new-each-time="no" (XSLT 3.0 §10.3).
    private readonly Dictionary<FunctionMemoKey, object?> _functionCache = new();


    // Cooperative cancellation and output size guard
    private readonly CancellationToken _ct;

    private readonly int _maxOutputSize;


    // Namespace scope tracking for deduplication during serialization
    private readonly Stack<Dictionary<string, string>> _outputNsScopes = new();


    // Sequence accumulator: when active, xsl:sequence items are collected into this list
    // instead of being serialized to the output. Used for variable/param bodies with as="type *".
    private List<object?>? _sequenceAccumulator;


    // When true, WriteText redirects text to _sequenceAccumulator as individual items
    // instead of appending to _output. Used by xsl:perform-sort content to collect
    // individual text items for sorting rather than concatenating them.
    internal bool _collectTextAsSequenceItems;

    internal int _serializingElementDepth; // >0 when inside element serialization

    private bool _forceDefaultNsUndeclaration; // inherit-namespaces="no" requires children to emit xmlns=""

    // SP-C copy-0612 family: true while constructing the content of an element (xsl:copy / LRE /
    // xsl:element) declared inherit-namespaces="no". Under it, constructed content — including a
    // grafted xsl:copy-of subtree — must NOT acquire any namespace from the constructing element
    // (XSLT 3.0 §11.7.2), not just the default namespace that _forceDefaultNsUndeclaration covers.
    // The untyped-RTF-flip copy-of routing reads it to clone into Document 0 (no ancestor walk) so
    // the copied element carries only its own copy-namespaces-filtered in-scope set.
    private bool _inheritNamespacesNo;

    // SP-C copy-0612 family: the base URI of the untyped-RTF-flip variable currently executing (the
    // base the content-reparse fallback uses). Non-null only while a flip constructor is installed;
    // read by CopyAsync to force a base sentinel for a copy of a base-URI-bearing SOURCE element so
    // the content fallback keeps base-uri() without a global temp-tree-depth raise.
    private string? _untypedFlipBaseContext;


    // Base-URI preservation across the temp-tree serialize→reparse boundary.
    //
    // Temp trees (xsl:document, as=document-node()/element() variable bodies) serialize
    // child output to TEXT in _output, then reparse it. Plain XML text carries no
    // per-node base URI, so a SOURCE element copied into the temp tree loses its base URI
    // and base-uri() later returns empty. To bridge the gap, the temp-tree serializer
    // EMITS a sentinel attribute (_pxbase_:base) carrying the source base URI on the root
    // of each copied subtree whose base differs from the enclosing serialization context;
    // the reparse RECOVERS it onto XdmNode.CopySourceBaseUri and DROPS the attribute so it
    // never becomes a real attribute or reaches user-visible output.
    //
    // _tempTreeSerializeDepth gates EMIT: it is >0 ONLY while serializing into a buffer
    // that will be reparsed (temp-tree boundaries). The final-output serializer never
    // raises it, so the sentinel is structurally impossible to leak into the result.
    // _serializeBaseContext tracks the base URI currently in effect during serialization
    // so the sentinel is emitted only on subtree roots where the base actually changes
    // (descendants inherit and stay silent).
    private int _tempTreeSerializeDepth; // >0 while serializing content destined for reparse

    private string? _serializeBaseContext; // base URI currently in effect during temp-tree serialization


    // Element name stack for CDATA section serialization
    private readonly Stack<QName> _outputElementStack = new();

    // Track whether the current output element has a namespace (for XTDE0440)
    private readonly Stack<bool> _outputElementHasNsStack = new();

    // Track whether the current element is a literal result element (for XTDE0430 scope)
    private readonly Stack<bool> _outputElementIsLreStack = new();

    // Track namespace bindings from xsl:namespace instructions per element (for XTDE0430)
    private readonly Stack<Dictionary<string, string>> _xslNamespaceBindings = new();


    // Tracks whether "real" content has been added during xsl:where-populated execution.
    // This is a stack because where-populated can be nested.
    // When active (count > 0), content-producing operations should call MarkContentProduced().
    private readonly Stack<bool> _populatedTracking = new();


    // Effective XSLT version stack — tracks xsl:version overrides on literal result elements.
    // When empty, the stylesheet's version is used. Top of stack is current effective version.
    private readonly Stack<string> _effectiveVersionStack = new();


    // Default collation stack — tracks default-collation overrides on XSLT elements.
    // When empty, the stylesheet's default collation (or codepoint) is used.
    private readonly Stack<string> _defaultCollationStack = new();


    // XSLT 3.0 accumulator state: maps (accumulator name, node ID) → (before-value, after-value)
    internal Dictionary<QName, Dictionary<NodeId, (object? before, object? after)>>? _accumulatorValues;

    private HashSet<(QName, NodeId)>? _evaluatingAccEndPhase; // Cycle detection for accumulator-after

    private HashSet<NodeId>? _accumulatorComputedDocuments; // Documents that have had accumulators pre-computed


    // XTDE1490: Track result-document output URIs to detect duplicates.
    private readonly HashSet<string> _resultDocumentUris = new(StringComparer.OrdinalIgnoreCase);

    // Secondary result documents: href → serialized content
    private readonly Dictionary<string, string> _secondaryResultDocuments = new(StringComparer.OrdinalIgnoreCase);

    // XTDE1480: Track whether we're in temporary output state (variable/param body)
    private int _temporaryOutputDepth;

    // Track nesting depth of secondary result-document redirects
    private int _resultDocumentRedirectDepth;

    // XTDE1490: Set when an explicit xsl:result-document href="" has claimed the primary output
    private bool _primaryOutputClaimedByResultDocument;

    private XsltOutput? _primaryOutputMatchedDeclaration;

    // Pending primary output from nested xsl:result-document href="" inside a secondary redirect
    private string? _pendingPrimaryContent;

    /// <summary>
    /// The base output URI (XSLT 3.0 §2.3) — the URI of the principal output destination, supplied
    /// by the host. Null when the result goes somewhere with no URI, such as stdout.
    /// </summary>
    private readonly Uri? _baseOutputUri;

    /// <summary>
    /// The URI currently being written to, as reported by <c>fn:current-output-uri()</c>. Equals
    /// <see cref="_baseOutputUri"/> while the principal result is being produced, and the resolved
    /// destination inside an <c>xsl:result-document</c>. Null means "absent", which the spec says
    /// current-output-uri() reports as the empty sequence.
    /// </summary>
    private Uri? _currentOutputUri;

    /// <summary>The destination URI fn:current-output-uri() should report, or null if absent.</summary>
    internal Uri? CurrentOutputUri => _currentOutputUri;

    // Active output declaration for current result-document (used for cdata-section-elements)
    private XsltOutput? _activeResultDocumentOutput;

    // Track when inside function body so xsl:value-of routes to sequence accumulator
    private int _functionBodyDepth;

    // The accumulator the innermost executing stylesheet function collects its result into.
    // "Directly in a function body" is _sequenceAccumulator being THIS list — not
    // _functionBodyDepth > 0, which is also true inside an xsl:variable nested in the body. See
    // InFunctionBodyProper.
    private List<object?>? _functionBodyAccumulator;

    /// <summary>
    /// True when the sequence being built is the executing function's own result — the only
    /// place text is written to BOTH the accumulator and <c>_output</c>, because only the
    /// function-result assembly knows to treat accumulated text as a duplicate of the output.
    /// </summary>
    /// <remarks>
    /// This was <c>_functionBodyDepth &gt; 0</c>, which also holds inside an xsl:variable in the
    /// body. That variable's accumulator got the text as an item AND its buffer got the text, and
    /// its drain emitted both: <c>string($v)</c> of
    /// <c>&lt;xsl:variable name="v"&gt;&lt;xsl:text&gt;x&lt;/xsl:text&gt;&lt;/xsl:variable&gt;</c>
    /// inside a function was <c>xx</c>. It has been wrong at least since before 1.7.0.
    /// </remarks>
    // The accumulator an untyped (temporary-tree) xsl:variable body is building behind. It only
    // catches typed results from nested instructions and is drained AFTER the body's text, so text
    // written to it lands out of order: <xsl:variable>a<b>c</b><xsl:text>d</xsl:text>e</xsl:variable>
    // read "aced", and copying it gave a<b>c</b>ed. Text in such a body goes to the buffer.
    private List<object?>? _treeBodyAccumulator;

    private bool InTreeBody =>
        _sequenceAccumulator != null && ReferenceEquals(_sequenceAccumulator, _treeBodyAccumulator);

    private bool InFunctionBodyProper =>
        _sequenceAccumulator != null && ReferenceEquals(_sequenceAccumulator, _functionBodyAccumulator);

    // Track when inside xsl:where-populated so XTDE0410 is suppressed during trial evaluation
    private int _wherePopulatedDepth;

    // Count of characters written to _output as atomic value separators (spaces between
    // adjacent atomic values in xsl:sequence). Used by content tracking to distinguish
    // separator-only growth from significant content growth.
    private int _separatorCharsWritten;

    // When > 0, text nodes should not break atomic value adjacency chain.
    // Used in Phase 2 of on-non-empty (when !wasPopulated) so that non-conditional
    // instructions producing empty text don't disrupt separator spacing for on-empty content.
    private int _preserveAtomicState;

    // Principal source document ID for accumulator applicability checks (§18.2.2)
    private DocumentId? _principalSourceDocId;


    /// <summary>
    /// Counts instructions that ALWAYS produce a text node, even a zero-length one — today just
    /// xsl:value-of (XSLT 3.0 11.4.3: it "creates a new text node" unconditionally).
    /// </summary>
    /// <remarks>
    /// The typed-variable body below manufactures a zero-length text node when the body produced
    /// no items and no characters, so that
    /// <c>&lt;xsl:variable as="text()"&gt;&lt;xsl:value-of select="()"/&gt;&lt;/xsl:variable&gt;</c>
    /// still yields one. That condition could not distinguish "an xsl:value-of ran and produced
    /// empty text" from "the body produced nothing at all", so
    /// <c>&lt;xsl:variable as="item()*"&gt;&lt;xsl:sequence select="()"/&gt;&lt;/xsl:variable&gt;</c>
    /// came back holding one empty text node instead of the empty sequence. This counter is the
    /// missing distinction.
    /// </remarks>
    private int _alwaysTextInstructionCount;


    // Snapshot-based content tracking for xsl:on-empty/xsl:on-non-empty.
    // Unlike where-populated tracking (which only counts significant child content),
    // on-empty tracking counts ALL output including attributes and whitespace text.
    private readonly Stack<(int outputLen, int attrsLen, int accumLen, bool lastAtomic)> _contentTrackingStack = new();


    public DefaultXsltExecutionContext(
        XsltStylesheet stylesheet,
        TemplateIndex templateIndex,
        XdmNode source,
        StringBuilder output,
        XsltTransformOptions options,
        XdmInMemoryStore? nodeStore = null,
        PhoenixmlDb.XQuery.ISchemaProvider? schemaProvider = null)
    {
        _stylesheet = stylesheet;
        _templateIndex = templateIndex;
        _output = output;
        _sink = new StringOutputSink(_output);
        _options = options;
        // Base output URI (XSLT 3.0 §2.3). Absent unless the host says where the principal result
        // is going — current-output-uri() then reports the empty sequence, which is correct for
        // stdout or an in-memory result.
        _baseOutputUri = options.BaseOutputUri;
        _currentOutputUri = options.BaseOutputUri;
        _nodeStore = nodeStore;
        _schemaProvider = schemaProvider;
        _ct = options.CancellationToken;
        _maxOutputSize = options.MaxOutputSize;
        _documentResolver = new XsltDocumentResolver(stylesheet, nodeStore)
        {
            PreloadedResources = options.PreloadedResources
        };
        if (options.Collections != null)
            _documentResolver.SetCollections(options.Collections);
        if (options.ResourcePolicy != null)
            _policyResolver = new PhoenixmlDb.XQuery.Security.PolicyEnforcingResolver(_documentResolver, options.ResourcePolicy);
        // Seed the resolver cache with the source document for identity preservation
        if (source is XdmDocument sourceDoc)
        {
            _documentResolver.SeedSourceDocument(sourceDoc);
            // Only track principal source doc ID when there IS a real source document.
            // When HasSourceDocument is false, the source is a synthetic <empty/> placeholder
            // and should not trigger XTDE3362 accumulator applicability checks.
            if (options.HasSourceDocument)
                _principalSourceDocId = sourceDoc.Document;
        }

        // Build function library: standard XPath/XQuery functions + XSLT user-defined functions
        _functionLibrary = BuildFunctionLibrary(stylesheet);

        _scopes.Push(new Scope());
        _contextItems.Push(source);
        _contextPositions.Push((1, 1));
    }


    // Pooled Scope reuse. Profiling showed ~8% of post-context-pool streaming alloc
    // was the Scope object itself. PushScope draws from the pool when available;
    // PopScope resets and returns to the pool (bounded).
    private readonly Stack<Scope> _scopePool = new();

    private const int MaxPooledScopes = 32;


    /// <summary>The QName of the pseudo-variable <c>$xsl:original</c>.</summary>
    private static readonly QName XslOriginalVariableName = new(NamespaceId.Xslt, "original");


    /// <summary>
    /// Streaming-group node-id allocator. Sits above the principal source's id range
    /// and below <see cref="StreamingXmlProcessor"/>'s allocator (1_000_000) by enough
    /// margin that practical streams don't collide. Reset on each group flush would be
    /// nice but the buffered nodes outlive the loop iteration, so we just monotonically
    /// increment.
    /// </summary>
    private ulong _nextStreamGroupNodeId = 5_000_000;


    /// <summary>
    /// Equality on group-adjacent keys. A composite key (composite="yes") is a
    /// SEQUENCE of atomic values (e.g. 007's <c>(year, week)</c>) surfaced as an
    /// <see cref="object"/> array; equality is member-wise so two distinct tuples
    /// that happen to string-concatenate alike are still separated. Single atomic
    /// keys fall back to value-then-string comparison.
    /// </summary>
    /// <summary>
    /// Accumulates <c>xsl:for-each-group group-by</c> members into groups, holding the whole
    /// of the grouping semantics in one place: collation, composite keys, sequence-valued keys
    /// (the item joins one group per value), an empty-sequence key (the item joins none), a key
    /// sequence with duplicates (the item joins each group once), numeric cross-type promotion
    /// so 1.0 and 1 group together, and first-appearance group order.
    /// </summary>
    /// <remarks>
    /// Shared by the buffered path, which feeds it items from an evaluated <c>select</c>, and by
    /// the streaming path, which feeds it members materialized off the reader. Both must agree —
    /// group-by is far too subtle to implement twice, and this engine has already paid for
    /// letting two seams reimplement one contract.
    ///
    /// Key EVALUATION stays with the callers: they differ in what context they push (buffered
    /// knows the population size up front, streaming does not), and only the accumulation
    /// itself is common.
    /// </remarks>
    private sealed class GroupByAccumulator(bool composite, StringComparer comparer)
    {
        private readonly List<(List<object?> Key, List<object> Items)> _composite = new();
        private readonly Dictionary<string, (object Key, List<object> Items)> _byKey = new(comparer);
        private readonly List<string> _orderedKeys = new();

        /// <summary>Adds one member under the grouping key its expression produced.</summary>
        public void Add(object item, object? key, Func<object?, string> keyString,
                        Func<object?, bool> isNumeric, Func<object?, object?, bool> valuesEqual,
                        Func<List<object?>, List<object?>, StringComparer, bool> compositeEquals,
                        StringComparer compositeComparer)
        {
            if (composite)
            {
                // composite="yes" permits empty and multi-value keys; the key is the whole tuple.
                var keyList = key switch
                {
                    null => new List<object?>(),
                    object?[] arr => arr.ToList(),
                    string s => new List<object?> { s },
                    IEnumerable<object?> seq => seq.ToList(),
                    _ => new List<object?> { key }
                };
                foreach (var group in _composite)
                {
                    if (compositeEquals(group.Key, keyList, compositeComparer))
                    {
                        group.Items.Add(item);
                        return;
                    }
                }
                _composite.Add((keyList, new List<object> { item }));
                return;
            }

            // Non-composite: one group per key VALUE. An empty sequence contributes no key, so
            // the item joins no group at all (XSLT 3.0 §19.2).
            IEnumerable<object?> keyValues = key switch
            {
                null => [],
                object?[] keyArr => keyArr,
                string => [key],
                IEnumerable<object?> keySeq => keySeq,
                _ => [key]
            };

            // A key sequence with duplicates (e.g. (5, 5)) joins the item to that group once.
            var joinedLists = new HashSet<List<object>>();
            foreach (var singleKey in keyValues)
            {
                var keyStr = keyString(singleKey);
                List<object>? targetItems = null;
                if (_byKey.TryGetValue(keyStr, out var entry))
                {
                    targetItems = entry.Items;
                }
                else if (isNumeric(singleKey))
                {
                    // Numeric keys compare by VALUE across types, so xs:double 1 and
                    // xs:decimal 1.0 land in the same group despite differing lexically.
                    foreach (var existingKey in _orderedKeys)
                    {
                        var existingEntry = _byKey[existingKey];
                        if (isNumeric(existingEntry.Key) && valuesEqual(singleKey, existingEntry.Key))
                        {
                            targetItems = existingEntry.Items;
                            break;
                        }
                    }
                }
                if (targetItems == null)
                {
                    targetItems = new List<object>();
                    _byKey[keyStr] = (singleKey ?? (object)keyStr, targetItems);
                    _orderedKeys.Add(keyStr);
                }
                if (joinedLists.Add(targetItems))
                    targetItems.Add(item);
            }
        }

        /// <summary>The groups in first-appearance order.</summary>
        public List<(object Key, List<object> Items)> ToGroupList() =>
            composite
                ? _composite.Select(g => ((object)g.Key, g.Items)).ToList()
                : _orderedKeys.Select(k => _byKey[k]).ToList();
    }


    private IReadOnlyDictionary<string, string>? _xpathNamespaceBindings;


    private sealed class UntypedRtfFlipBlockScanner : XsltInstructionWalker
    {
        // LEXICAL-only: this walks the STATIC body instructions. It blocks an xml:base written
        // literally in the body (StaticBaseUri), but it does NOT and cannot see an xml:base
        // reached at RUNTIME through dispatch — an xsl:copy/xsl:copy-of/xsl:apply-templates of a
        // SOURCE element that itself carries xml:base. Those flip; their base URI is preserved by
        // the base-sentinel path at the xsl:copy site (TryEmitBaseSentinel, force under the flip),
        // not by this scanner. (apply-imports/next-match/call-template are blocked below because
        // they can copy out of view through another module, not for base-uri reasons.)
        public bool Blocked { get; private set; }

        public override void Walk(XsltInstruction insn)
        {
            if (Blocked)
                return;
            // xml:base anywhere in the body drives base-uri() resolution the reparse performs.
            if (insn.StaticBaseUri != null)
            {
                Blocked = true;
                return;
            }
            base.Walk(insn);
        }

        // xsl:copy is NOT blocked (SP-C copy-0612 family): when a tree constructor is active,
        // CopyAsync builds the copied element natively via TcOpenElement/TcFinishElement and its
        // string emission still runs byte-identically (flip ⊆ compared). Its base-uri rerouting is
        // gated on the xml:base block below (StaticBaseUri) — the shapes that perturbed base-uri()
        // (fn/snapshot) all carry xml:base and stay on the reparse. xsl:copy-of is likewise not
        // blocked: the copy-of routing (CopyOfCoreAsync) clones source nodes directly into the
        // active constructor. A benign (namespace-free) copy flips byte-identically to the reparse
        // (verified by the differential); a namespace-bearing copy that would diverge is either
        // vetoed by IsUntypedRtfFlipByteParitySafe (no flip, reparse unchanged) or — for the
        // §11.7.2 fixup shapes the reparse gets wrong (W3C copy-1220/1221/0613/0615/0621/0623) —
        // delivered as the authoritative node build with the differential skipped
        // (_untypedRtfFlipDivergent).
        // xsl:apply-templates is NOT blocked (SP-C copy-0612 family): the recursive
        // shallow-copy + xsl:copy identity chain builds nodes into the active constructor. Template
        // dispatch that could copy a node out of view via a different module is still blocked below
        // (apply-imports / next-match / call-template) — those are not exercised by the copy-0612
        // family and keep the base-uri/namespace risk contained.
        public override object? VisitApplyImports(XsltApplyImports insn) { Blocked = true; return null; }
        public override object? VisitNextMatch(XsltNextMatch insn) { Blocked = true; return null; }
        public override object? VisitCallTemplate(XsltCallTemplate insn) { Blocked = true; return null; }
    }


    private enum ValidationKind { Document, Fragment }


    private static readonly System.Xml.Linq.XNamespace RdSerializationParamsNs =
        "http://www.w3.org/2010/xslt-xquery-serialization";


    // Cached delegates for CreateMatchContext. Method-group conversions allocate
    // a fresh delegate every time, and CreateMatchContext is on the per-element
    // template-match hot path — caching is worth ~37% of streaming alloc traffic.
    // Built once on first use; safe to share because the captured `this` and `_nodeStore`
    // are stable for the lifetime of the transformer.
    private Func<NodeId, XdmNode?>? _matchCtxNodeResolver;

    private Func<object, XQueryExpression, int, int, object?, bool>? _matchCtxPredicateEvaluator;

    private Func<object, IReadOnlyList<XQueryExpression>, NodeTest, object?, bool>? _matchCtxSequencePredicateEvaluator;

    private Func<object, NodeTest, object?, (int, int)>? _matchCtxPositionComputer;

    private Func<string, XQueryExpression, object, bool>? _matchCtxKeyPatternEvaluator;

    private Func<XQueryExpression, object, bool>? _matchCtxIdPatternEvaluator;

    private Func<QName, object?>? _matchCtxVariablePatternEvaluator;

    private Func<string, XdmNode?>? _matchCtxDocPatternEvaluator;

    private Func<object, IReadOnlyList<object>>? _matchCtxTreeNodesInDocumentOrder;


    // Pool of XsltContext instances. Each call to AcquireMatchContext pops one
    // (or allocates if empty) and stamps the per-call fields; the returned lease
    // pushes it back to the pool on Dispose. Bounded to keep nested-match cases
    // from inflating the pool unboundedly.
    private readonly Stack<XsltContext> _matchCtxPool = new();

    private const int MaxPooledMatchContexts = 8;


    /// <summary>
    /// Pooled lease over an <see cref="XsltContext"/>. Dispose returns the context
    /// to the transformer's pool. Use with `using var lease = AcquireMatchContext();`
    /// and pass <see cref="Value"/> to <c>Matches</c> / <c>FindMatchingTemplate</c>.
    /// </summary>
    internal readonly ref struct MatchContextLease
    {
        private readonly DefaultXsltExecutionContext _owner;
        public readonly XsltContext Value;
        internal MatchContextLease(DefaultXsltExecutionContext owner, XsltContext value)
        {
            _owner = owner;
            Value = value;
        }
        public void Dispose() => _owner.ReleaseMatchContext(Value);
    }


    private int _nsPrefixCounter;


    /// <summary>
    /// HTML empty (void) elements that never have an end tag. Union of the classic
    /// HTML 4.01 / XHTML 1.0 empty set (area, base, basefont, br, col, frame, hr, img,
    /// input, isindex, link, meta, param) and the HTML5 additions (embed, source, track,
    /// wbr), per the XSLT/XQuery Serialization HTML/XHTML output method. Matching is
    /// case-insensitive.
    /// </summary>
    private static readonly HashSet<string> HtmlVoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "basefont", "br", "col", "embed", "frame", "hr", "img", "input",
        "isindex", "link", "meta", "param", "source", "track", "wbr"
    };


    /// <summary>
    /// Post-processes output for HTML/XHTML method.
    /// Converts self-closing void elements to HTML style and expands non-void self-closing elements.
    /// </summary>
    /// <summary>
    /// The three namespaces that are serialized as HTML5 content by the XHTML output method:
    /// XHTML, SVG, and MathML. Elements in these namespaces are emitted with their local name and
    /// a default <c>xmlns</c> declaration (prefixes are stripped); elements in any other namespace
    /// are foreign content and keep their prefixes (XML rules).
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> Html5ContentNamespaces = new(StringComparer.Ordinal)
    {
        "http://www.w3.org/1999/xhtml",
        "http://www.w3.org/2000/svg",
        "http://www.w3.org/1998/Math/MathML",
    };


    private sealed class Scope
    {
        // Lazy-init Variables / TunnelParameters dictionaries: most scopes (e.g. the
        // shallow-copy identity template fire) bind no variables, so eagerly allocating
        // two Dictionaries per PushScope burned ~28% of the streaming bench's
        // post-delegate-cache allocation. Readers use `VariablesOrNull`/`TunnelParametersOrNull`
        // to short-circuit on empty; the public getters lazy-allocate only on write.
        private Dictionary<QName, object?>? _variables;
        private Dictionary<QName, object?>? _tunnelParameters;

        public Dictionary<QName, object?> Variables => _variables ??= new();
        public Dictionary<QName, object?> TunnelParameters => _tunnelParameters ??= new();

        /// <summary>Returns the backing variables dictionary or <c>null</c> if no variable has been bound.</summary>
        public Dictionary<QName, object?>? VariablesOrNull => _variables;
        /// <summary>Returns the backing tunnel-parameter dictionary or <c>null</c> if no parameter has been bound.</summary>
        public Dictionary<QName, object?>? TunnelParametersOrNull => _tunnelParameters;

        public Scope? Parent { get; set; }
        /// <summary>
        /// When true, tunnel parameter search stops at this scope boundary.
        /// Used for xsl:function calls where tunnel params must not leak through.
        /// </summary>
        public bool IsTunnelBarrier { get; set; }

        public Scope(Scope? parent = null)
        {
            Parent = parent;
        }

        /// <summary>
        /// Clears all state so this Scope can be returned to a pool. Keeps the
        /// dictionaries allocated (if any) so subsequent xsl:variable-heavy scopes
        /// reuse them via Clear() rather than re-allocating.
        /// </summary>
        public void Reset()
        {
            Parent = null;
            IsTunnelBarrier = false;
            _variables?.Clear();
            _tunnelParameters?.Clear();
        }
    }


    private sealed class BreakException : XsltException
    {
        public BreakException() : base("Break") { }
        public BreakException(string message) : base(message) { }
        public BreakException(string message, Exception innerException) : base(message, innerException) { }
    }


    private sealed class NextIterationException : XsltException
    {
        public NextIterationException() : base("NextIteration") { }
        public NextIterationException(string message) : base(message) { }
        public NextIterationException(string message, Exception innerException) : base(message, innerException) { }
        public List<XsltWithParam> WithParams { get; init; } = new();
    }

}
