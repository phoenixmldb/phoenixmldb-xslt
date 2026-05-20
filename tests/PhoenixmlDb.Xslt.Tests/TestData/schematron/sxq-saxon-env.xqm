xquery version "3.1";

(:~
 : S✗Q
 :
 : MIT License
 :
 : Copyright (c) David Maus
 :
 : Permission is hereby granted, free of charge, to any person obtaining a copy
 : of this software and associated documentation files (the "Software"), to deal
 : in the Software without restriction, including without limitation the rights
 : to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 : copies of the Software, and to permit persons to whom the Software is
 : furnished to do so, subject to the following conditions:
 :
 : The above copyright notice and this permission notice shall be included in all
 : copies or substantial portions of the Software.
 :
 : THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 : IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 : FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 : AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 : LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 : OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 : SOFTWARE.
 :
 :)

module namespace sxq = "http://dmaus.name/ns/2026/sxq";

import module namespace env = "http://dmaus.name/ns/2026/sxq-env" at "environment-saxon.xqm";

declare namespace sch = "http://purl.oclc.org/dsdl/schematron";
declare namespace svrl = "http://purl.oclc.org/dsdl/svrl";

declare function sxq:validate ($schema as element(sch:schema), $document as node(), $phase as xs:string, $params as map(xs:QName, item()?)) as element(svrl:schematron-output) {
  let $schema as element(sch:schema) := sxq:compose-schema($schema)
  let $env as map(*)+ := sxq:create-global-environment($schema, $document, $params)
  let $phase as xs:string := sxq:find-effective-phase($env, $schema, $document, $phase)
  let $env as map(*)+ := sxq:declare-variables(env:create-environment($env), $document, $schema/sch:phase[@id = $phase]/sch:let)
  let $schema as element(sch:schema) := sxq:reduce-schema($schema, $phase)
  return
    element svrl:schematron-output {
      attribute phase { $phase },
      $schema/sch:ns ! element svrl:ns-prefix-in-attribute-values { @prefix, @uri },
      $schema/sch:p ! element svrl:text { @icon, text() },
      for-each($schema/sch:pattern, function ($pattern as element(sch:pattern)) as element()+ {
        sxq:instance-documents($env, $pattern, $document)
          ! sxq:validate-pattern($env, $pattern, .)
      }),
      for-each($schema/sch:group, function ($group as element(sch:group)) as element()+ {
        sxq:instance-documents($env, $group, $document)
          ! sxq:validate-group($env, $group, .)
      })
    }
};

declare %private function sxq:instance-documents ($env as map(*)+, $patternOrGroup as element(), $document as node()) as node()* {
  if (not($patternOrGroup/@documents))
    then $document
    else
      for-each(env:evaluate($env, $document, $patternOrGroup/@documents), function ($href as xs:string) as node() {
        doc(resolve-uri($href, base-uri($document)))
      })
};

declare %private function sxq:validate-group ($env as map(*)+, $group as element(sch:group), $document as node()) as element()+ {
  let $env as map(*)+ := sxq:declare-variables(env:create-environment($env), $document, $group/sch:let)
  let $res as element()* := for-each($group/sch:rule, function ($rule as element(sch:rule)) as element()* {
    for-each(env:evaluate($env, $document, $rule/@context), function ($node as node()) as element()* {
      sxq:validate-rule($env, $rule, $node)
    })
  })
  return
    (
      element svrl:active-pattern {
        $group/@id, $group/@role, $group/@documents,
        if ($group/sch:title) then attribute name { $group/sch:title } else ()
      },
      $res
    )
};

declare %private function sxq:validate-pattern ($env as map(*)+, $pattern as element(sch:pattern), $document as node()) as element()+ {
  let $env as map(*)+ := sxq:declare-variables(env:create-environment($env), $document, $pattern/sch:let)
  let $res as map(xs:string, element()+) := fold-left($pattern/sch:rule, map{}, function ($acc as map(xs:string, element()+), $rule as element(sch:rule)) as map(xs:string, element()+) {
    fold-left(env:evaluate($env, $document, $rule/@context), $acc, function ($acc as map(xs:string, element()+), $node as node()) as map(xs:string, element()+) {
      if (map:contains($acc, generate-id($node)))
        then $acc
        else map:put($acc, generate-id($node), sxq:validate-rule($env, $rule, $node))
    })
  })
  return
    (
      element svrl:active-pattern {
        $pattern/@id, $pattern/@role, $pattern/@documents,
        if ($pattern/sch:title) then attribute name { $pattern/sch:title } else ()
      },
      $res?*
    )
};

declare %private function sxq:validate-rule ($env as map(*)+, $rule as element(sch:rule), $context as node()) as element()+ {
  let $context as item()* := if ($rule/@visit-each) then env:evaluate($env, $context, $rule/@visit-each) else $context
  let $res as element()* := for-each($context, function ($item as item()) as element()* {
    let $env as map(*)+ := sxq:declare-variables(env:create-environment($env), $item, $rule/sch:let)
    return
    (
      for-each($rule/sch:assert, function ($assert as element(sch:assert)) as element()? {
        if (env:evaluate($env, $item, $assert/@test))
          then ()
          else element svrl:failed-assert {
            $assert/@flag, $assert/@id, $assert/@role, $assert/@severity, $assert/@test,
            if ($item instance of node()) then attribute location { path($item) } else (),
            element svrl:text {
              $assert/node() ! sxq:format-message($env, $item, .)
            },
            sxq:report-diagnostics($env, $context, $rule/sch:diagnostics/sch:diagnostic[@id = tokenize($assert/@diagnostics)]),
            sxq:report-properties($env, $context, $rule/sch:properties/sch:property[@id = tokenize($assert/@properties)])
          }
      }),
      for-each($rule/sch:report, function ($report as element(sch:report)) as element()? {
        if (env:evaluate($env, $item, $report/@test))
          then element svrl:successful-report {
            $report/@flag, $report/@id, $report/@role, $report/@severity, $report/@test,
            if ($item instance of node()) then attribute location { path($item) } else (),
            element svrl:text {
              $report/node() ! sxq:format-message($env, $item, .)
            },
            sxq:report-diagnostics($env, $context, $rule/sch:diagnostics/sch:diagnostic[@id = tokenize($report/@diagnostics)]),
            sxq:report-properties($env, $context, $rule/sch:properties/sch:property[@id = tokenize($report/@properties)])
          }
          else ()
      })
    )
  })
  return
    (
      element svrl:fired-rule {
        $rule/@context, $rule/@flag, $rule/@id, $rule/@role
      },
      $res
    )
};

declare %private function sxq:report-diagnostics($env as map(*)+, $context as item(), $diagnostics as element(sch:diagnostic)*) as element(svrl:diagnostic-reference)* {
  for-each($diagnostics, function ($diagnostic as element(sch:diagnostic)) as element(svrl:diagnostic-reference) {
    element svrl:diagnostic-reference {
      attribute diagnostic { $diagnostic/@id },
      element svrl:text {
        $diagnostic/node() ! sxq:format-message($env, $context, .)
      }
    }
  })
};

declare %private function sxq:report-properties($env as map(*)+, $context as item(), $properties as element(sch:property)*) as element(svrl:property-reference)* {
  for-each($properties, function ($property as element(sch:property)) as element(svrl:property-reference) {
    element svrl:property-reference {
      attribute property { $property/@id },
      $property/@role, $property/@schema,
      element svrl:text {
        $property/node() ! sxq:format-message($env, $context, .)
      }
    }
  })
};

declare %private function sxq:find-effective-phase ($env as map(*)+, $schema as element(sch:schema), $document as node(), $phase as xs:string) {
  switch ($phase)
    case '#ANY'
      return
        let $ids as xs:string* := for-each($schema/sch:phase[@when], function ($phase as element(sch:phase)) {
          if (env:evaluate($env, $document, string($phase/@when))) then $phase/@id else ()
        })
        return
          ($ids, '#ALL')[1]
    case '#DEFAULT'
      return
        ($schema/@defaultPhase, '#ALL')[1]
    default
      return $phase
};

declare %private function sxq:declare-variables ($env as map(*)+, $context as item(), $variables as element(sch:let)*) as map(*)+ {
  fold-left($variables, $env, function ($acc as map(*)+, $let as element(sch:let)) as map(*)+ {
    let $name as xs:QName := sxq:qualify-name($env, $let/@name)
    let $value as item()* := if ($let/@value) then string($let/@value) else $let/*
    let $type as xs:string :=
      if ($let/@as)
        then string($let/@as)
        else
          if ($let/*) then 'element()*' else 'item()'
    return
      env:declare-variable($acc, $context, $name, $type, $value)
  })
};

declare %private function sxq:create-global-environment ($schema as element(sch:schema), $document as item(), $params as map(xs:QName, item()?)) as map(*)+ {
  let $env as map(*)+ := env:create-environment()
  let $env as map(*)+ := fold-left($schema/sch:ns, $env, function ($acc as map(*)+, $ns as element(sch:ns)) as map(*)+ {
    env:declare-namespace($acc, string($ns/@prefix), xs:anyURI($ns/@uri))
  })
  let $env as map(*)+ := fold-left($schema/sch:param, $env, function ($acc as map(*)+, $param as element(sch:param)) as map(*)+ {
    let $name as xs:QName := sxq:qualify-name($env, $param/@name)
    let $value as item() := (map:get($params, $name), string($param/@value))[1]
    let $type as xs:string :=
      if ($param/@as)
        then string($param/@as)
        else 'item()'
    return
      env:declare-variable($env, $document, $name, $type, $value)
  })
  return
    sxq:declare-variables($env, $document, $schema/sch:let)
};

declare %private function sxq:qualify-name ($env as map(*)+, $name as xs:string) as xs:QName {
  QName(env:get-namespace-uri($env, substring-before($name, ':')), $name)
};

declare function sxq:format-message ($env as map(*)+, $context as item(), $node as node()) as item()* {
  switch ($node)
    case $node[self::sch:name]
      return
        if ($node[@path]) then env:evaluate($env, $context, $node/@path) else name($node)
    case $node[self::sch:emph]
      return element svrl:emph {
        $node/node() ! sxq:format-message($env, $context, .)
      }
    case $node[self::sch:span]
      return element svrl:span {
        $node/@class,
        $node/node() ! sxq:format-message($env, $context, .)
      }
    case $node[self::sch:value-of]
      return
        env:evaluate($env, $context, $node/@select)
    case $node[self::sch:dir]
      return element svrl:dir { attribute dir { $node/@value } }
  default
    return $node
};

declare function sxq:reduce-schema ($schema as element(sch:schema), $phase as xs:string) as element(sch:schema) {
  element {node-name($schema)} {
    $schema/@* except $schema/@defaultPhase,
    if ($phase ne '#ALL') then attribute defaultPhase { $phase } else (),
    $schema/node() except (
      if ($phase eq '#ALL') then () else $schema/sch:phase[@id != $phase],
      if ($phase eq '#ALL') then () else $schema/(sch:group | sch:pattern)[not(@id = $schema/sch:phase[@id = $phase]/sch:active/@pattern)]
     )
  }
};

declare function sxq:compose-schema ($schema as element(sch:schema)) as element(sch:schema) {
  $schema
    => sxq:assemble-schema()
    => sxq:denormalize-schema()
    => sxq:instantiate-abstract-patterns()
    => sxq:expand-abstract-rules()
};

declare function sxq:expand-abstract-rules ($schema as element(sch:schema)) as element(sch:schema) {
  $schema ! sxq:expand-abstract-rules-transform(., sxq:in-scope-language($schema))
};

declare %private function sxq:expand-abstract-rules-transform ($node as node(), $lang as xs:string) as node()* {
  switch ($node)
    case $node[self::sch:rule][@abstract = 'true']
    case $node[self::sch:rules]
      return ()
    case $node[self::sch:extends][parent::sch:rule][@rule]
      return
        let $bundle as element(sch:rule) := $node/../../../(sch:group | sch:pattern | sch:rules)/sch:rule[@id = $node/@rule]
        return
          $bundle/node() ! sxq:expand-abstract-rules-transform(., $lang)
    case $node[self::*]
      return element {node-name($node)} {
        $node/@*,
        sxq:create-language-attribute($node, $lang),
        $node/node()! sxq:expand-abstract-rules-transform(., sxq:in-scope-language(.))
      }
    default
      return $node
};

declare function sxq:instantiate-abstract-patterns ($schema as element(sch:schema)) as element(sch:schema) {
  $schema ! sxq:instantiate-abstract-patterns-transform(., sxq:in-scope-language($schema))
};

declare function sxq:instantiate-abstract-patterns-transform ($node as node(), $lang as xs:string) as node()* {
  switch ($node)
  case $node[self::sch:group][@abstract = 'true']
  case $node[self::sch:pattern][@abstract = 'true']
  case $node[self::sch:param][parent::sch:group]
  case $node[self::sch:param][parent::sch:pattern]
    return ()
  case $node[self::sch:group][@is-a]
  case $node[self::sch:pattern][@is-a]
    return
      let $template as element() := $node/../(sch:pattern | sch:group)[local-name() eq local-name($node)][@id = $node/@is-a]
      let $params as map(xs:string, xs:string) := map:merge((sxq:params-to-map($node/sch:param), sxq:params-to-map($template/sch:param)), map{'duplicates': 'use-first'})
      return element {node-name($node)} {
        $node/@* except $node/@is-a,
        $node/node() except $node/sch:param,
        ($template/node() except $template/sch:param) ! sxq:replace-abstract-pattern-params(., $params, sxq:in-scope-language($node))
      }
  case $node[self::*]
      return element {node-name($node)} {
        $node/@*,
        sxq:create-language-attribute($node, $lang),
        $node/node() ! sxq:instantiate-abstract-patterns-transform(., sxq:in-scope-language(.))
      }
    default
      return $node
};

declare %private function sxq:replace-abstract-pattern-params ($node as node(), $params as map(xs:string, xs:string), $lang as xs:string) as node() {
  switch ($node)
    case $node[self::attribute(test)][parent::sch:assert]
    case $node[self::attribute(test)][parent::sch:report]
    case $node[self::attribute(context)][parent::sch:rule]
    case $node[self::attribute(select)][parent::sch:value-of]
    case $node[self::attribute(documents)][parent::sch:group]
    case $node[self::attribute(documents)][parent::sch:pattern]
    case $node[self::attribute(path)][parent::sch:name]
    case $node[self::attribute(value)][parent::sch:let]
      return attribute {node-name($node)} {
        sxq:replace-params(string($node), $params)
      }
    case $node[self::*]
      return element {node-name($node)} {
        sxq:create-language-attribute($node, $lang),
        $node/@* ! sxq:replace-abstract-pattern-params(., $params, sxq:in-scope-language(.)),
        $node/node() ! sxq:replace-abstract-pattern-params(., $params, sxq:in-scope-language(.))
      }
    default
      return $node
};

declare %private function sxq:replace-params ($expr as xs:string, $params as map(xs:string, xs:string)) as xs:string {
  fold-left(sort(map:keys($params), (), string-length#1), $expr, function ($acc as xs:string, $name as xs:string) as xs:string {
    let $value as xs:string := replace(replace(map:get($params, $name), '\\', '\\\\'), '\$', '\\\$')
    return
      replace($acc, concat('(\W*)\$', $name, '(\W*)'), concat('$1', $value, '$2'))
  })
};

declare %private function sxq:params-to-map ($params as element(sch:param)*) as map(xs:string, xs:string?) {
  fold-left($params, map{}, function ($acc as map(xs:string, xs:string?), $param as element(sch:param)) as map(xs:string, xs:string?) {
    if ($param/@value) then map:put($acc, string($param/@name), string($param/@value)) else $acc
  })
};

declare function sxq:denormalize-schema ($schema as element(sch:schema)) as element(sch:schema) {
  $schema ! sxq:denormalize-schema-transform($schema, sxq:in-scope-language($schema))
};

declare %private function sxq:denormalize-schema-transform ($node as node(), $lang as xs:string) as node()* {
  switch ($node)
    case $node[self::sch:properties]
      return ()
    case $node[self::sch:diagnostics]
      return ()
    case $node[self::sch:rule]
      return
        let $diagnostics as xs:string* := $node/(sch:assert | sch:report)/@diagnostics ! tokenize(.)
        let $properties as xs:string* := $node/(sch:assert | sch:report)/@properties ! tokenize(.)
        return element {node-name($node)} {
          $node/@*,
          $node/node(),
          element sch:diagnostics {
            $node/../../sch:diagnostics/sch:diagnostic[@id = $diagnostics] ! sxq:denormalize-schema-transform(., $lang)
          },
          element sch:properties {
            $node/../../sch:properties/sch:property[@id = $properties] ! sxq:denormalize-schema-transform(., $lang)
          }
        }
    case $node[self::*]
      return element {node-name($node)} {
        $node/@*,
        sxq:create-language-attribute($node, $lang),
        $node/node() ! sxq:denormalize-schema-transform(., sxq:in-scope-language(.))
      }
    default
      return $node
};

declare function sxq:assemble-schema ($schema as element(sch:schema)) as element(sch:schema) {
  $schema ! sxq:assemble-schema-transform($schema, sxq:in-scope-language($schema))
};

declare %private function sxq:assemble-schema-transform ($node as node(), $lang as xs:string) as node()* {
  switch ($node)
    case $node[self::sch:extends][@href]
      return
        let $external as element() := sxq:load-external(resolve-uri($node/@href, base-uri($node)))
        return
          $external/node() ! sxq:assemble-schema-transform(., $lang)
    case $node[self::sch:include]
      return
        sxq:load-external(resolve-uri($node/@href, base-uri($node))) ! sxq:assemble-schema-transform(., $lang)
    case $node[self::*]
      return element {node-name($node)} {
        $node/@*,
        sxq:create-language-attribute($node, $lang),
        $node/node() ! sxq:assemble-schema-transform(., sxq:in-scope-language(.))
      }
    default
      return $node
};

declare %private function sxq:load-external ($href as xs:anyURI) as element() {
  let $url as xs:string := tokenize($href, '#')[1]
  let $fragment as xs:string? := tokenize($href, '#')[2]
  let $document as document-node() := doc($url)
  let $element as element()* := if ($fragment) then $document//sch:*[@id = $fragment] else $document/*[1]
  return
    if (count($element) eq 1) then $element else error()
};

declare %private function sxq:in-scope-language ($node as node()) as xs:string {
  $node/ancestor-or-self::*[@xml:lang][1]/@xml:lang => string()
};

declare %private function sxq:create-language-attribute ($node as node(), $lang as xs:string) as attribute(xml:lang)? {
  if (sxq:in-scope-language($node) ne $lang and not($node/@xml:lang))
    then attribute xml:lang { sxq:in-scope-language($node) }
    else ()
};