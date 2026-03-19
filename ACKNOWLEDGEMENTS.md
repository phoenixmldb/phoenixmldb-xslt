# Acknowledgements

## W3C Specifications

This XSLT engine implements specifications developed by the
[World Wide Web Consortium (W3C)](https://www.w3.org/) and the
[Qt4 Community Group](https://qt4cg.org/):

- [XSLT 3.0](https://www.w3.org/TR/xslt-30/) (W3C Recommendation)
- [XSLT 4.0](https://qt4cg.org/specifications/xslt-40/Overview.html) (Community Group Draft)
- [XPath 3.1](https://www.w3.org/TR/xpath-31/) (W3C Recommendation)
- [XPath 4.0](https://qt4cg.org/specifications/xpath-functions-40/Overview.html) (Community Group Draft)
- [XSLT and XQuery Serialization 4.0](https://qt4cg.org/specifications/xslt-xquery-serialization-40/Overview.html) (Community Group Draft)
- [XPath and XQuery Functions and Operators 4.0](https://qt4cg.org/specifications/xpath-functions-40/Overview.html) (Community Group Draft)

These specifications represent years of collaborative work by the W3C XSL Working Group
and the Qt4 Community Group. We thank all editors and contributors.

## W3C XSLT 3.0 Test Suite

Conformance is validated against the
[W3C XSLT 3.0 Test Suite](https://github.com/w3c/xslt30-test), used under the
[W3C Test Suite License](https://www.w3.org/Consortium/Legal/2008/04-testsuite-copyright.html).

We gratefully acknowledge the test suite authors and contributors:

**Primary Authors:** Michael Kay (Saxonica), Debbie Lockett (Saxonica), Abel Braaksma

**Contributors:** Charles Foster, Colin Adams, David Marston, David Rudel,
John Lumley, Norman Walsh, O'Neil Delpratt (Saxonica), Scott Boag, Toshihito Makita

**Bug reporters & test ideas:** Andy Yar, Christian Roth, Claudio Sacerdoti Coen,
Dak Tapaal, Dave Haffner, Dave Pawson, David Maus, Frank Steimke, Geoff Crowther,
Jiri Dolejsi, Joel Kalvesmaki, Julian Reschke, Ken Holman, Marcus Lauer, Mark Dunn,
Martin Honnen, Max Toro, Michael Wirth, Morten Jorgensen, Norm Tovey-Walsh, Paul Dick,
Phil Fearon, Rohit Gaikwad, Ruud Grossmann, T. Hatanaka, Tim Mills, Tom Hillman,
Vladimir Nestorovsky, Wendell Piez

*This list was generated using:*
```
xquery 'sort(distinct-values(collection()//*:created/@by))' tests/
```

## Open Source Dependencies

| Library | License | Usage |
|---------|---------|-------|
| [ANTLR 4](https://www.antlr.org/) | BSD-3-Clause | XPath expression parsing (via XQuery engine) |
| [Lucene.NET](https://lucenenet.apache.org/) | Apache-2.0 | Full-text search (via XQuery engine) |
| [.NET](https://dotnet.microsoft.com/) | MIT | Runtime and SDK |
