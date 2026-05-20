xquery version "3.1";

(:~
 : S✗Q Environment for Dynamic Evaluation, Saxon Implementation.
 :
 : This module provides an environment for dynamic evaluation of XPath expressions.
 :
 : MIT License
 :
 : Copyright (c) David Maus
 : adaption for Saxon Copyright (c) Martin Honnen
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

module namespace env = "http://dmaus.name/ns/2026/sxq-env";

declare function env:create-environment () as map(*)+ {
  env:create-environment(())
};

declare function env:create-environment ($enclosing as map(*)*) as map(*)+ {
  (
    map{'namespaces': map{}, 'variables': map{}},
    $enclosing
  )
};

declare function env:declare-namespace ($env as map(*)+, $prefix as xs:string, $name as xs:anyURI) as map(*)+ {
  (
    env:set-namespaces(head($env), map:put(env:get-namespaces(head($env)), $prefix, $name)),
    tail($env)
  )
};

declare function env:declare-variable ($env as map(*)+, $context as item(), $name as xs:QName, $typeExpr as xs:string, $valueExpr as item()*) as map(*)+ {
  let $value as item()* := if ($valueExpr instance of node()*) then $valueExpr else env:evaluate($env, $context, $valueExpr)
  return
    (
      env:set-variables(head($env), map:put(env:get-variables(head($env)), $name, map{'type': $typeExpr, 'value': $value})),
      tail($env)
    )
};

declare function env:evaluate ($env as map(*)+, $context as item(), $expr as xs:string) as item()* {
  let $variables as map(xs:QName, map(*)) := map:merge($env ! env:get-variables(.), map{'duplicates': 'use-first'})
  let $bindings as map(*) := fold-left(map:keys($variables), map{}, function ($acc as map(xs:QName, item()*), $key as xs:QName) as map(xs:QName, item()*) {
    map:put($acc, $key, map:get(map:get($variables, $key), 'value'))
  }) (: => map:put('', $context) :)
  let $namespaces as map(xs:string, xs:anyURI) := map:merge($env ! env:get-namespaces(.), map{'duplicates': 'use-first'})
  let $xslt-evaluate := 
    <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                    xmlns:xs="http://www.w3.org/2001/XMLSchema"
                    xmlns:map="http://www.w3.org/2005/xpath-functions/map"
                    xmlns:mf="http://example.com/mf" exclude-result-prefixes="#all">
    <xsl:function name="mf:evaluate" as="item()*" visibility="public">
       <xsl:param name="context-item" as="item()"/>
       <xsl:param name="xpath-expression" as="xs:string"/>
       <xsl:param name="variables" as="map(xs:QName, item()*)"/>
       <xsl:param name="namespaces" as="map(xs:string, xs:anyURI)"/>
       <xsl:variable name="namespace-context" as="element()">
         <xsl:element name="namespace-context" namespace="{{$namespaces('')}}">
           <xsl:for-each select="map:keys($namespaces)[. != '']">
             <xsl:namespace name="{{.}}" select="$namespaces(.)"/>
           </xsl:for-each>
         </xsl:element>
       </xsl:variable>
       
       <xsl:evaluate context-item="$context-item" xpath="$xpath-expression" with-params="$variables" namespace-context="$namespace-context"/>
    </xsl:function>
  </xsl:stylesheet>
  return
    (: xquery:eval(env:create-prolog($env) || $expr, $bindings) :)
    transform( 
      map {
        'stylesheet-node' : $xslt-evaluate,
        'initial-function' : QName('http://example.com/mf', 'evaluate'),
        'function-params' : [$context, $expr, $bindings, $namespaces],
        'delivery-format' : 'raw'
      }
    )?output
};

declare function env:get-namespace-uri ($env as map(*)+, $prefix as xs:string) as xs:anyURI? {
  map:get(map:merge($env ! env:get-namespaces(.), map{'duplicates': 'use-first'}), $prefix)
};

declare %private function env:create-prolog ($env as map(*)+) as xs:string* {
  env:create-prolog-namespaces($env) || env:create-prolog-variables($env)
};

declare %private function env:create-prolog-namespaces ($env as map(*)+) as xs:string* {
  let $namespaces as map(xs:string, xs:anyURI) := map:merge($env ! env:get-namespaces(.), map{'duplicates': 'use-first'})
  return
    map:keys($namespaces) ! concat('declare namespace ', ., ' = "', map:get($namespaces, .) , '";')
};

declare %private function env:create-prolog-variables ($env as map(*)+) as xs:string* {
  let $variables as map(xs:QName, map(*)) := map:merge($env ! env:get-variables(.), map{'duplicates': 'use-first'})
  return
    map:keys($variables) ! concat('declare variable $', ., ' as ', map:get(map:get($variables, .), 'type'), ' external;')
};

declare %private function env:get-namespaces ($frame as map(*)) as map(xs:string, xs:anyURI) {
  map:get($frame, 'namespaces')
};

declare %private function env:set-namespaces ($frame as map(*), $namespaces as map(xs:string, xs:anyURI)) as map(*) {
  map:put($frame, 'namespaces', $namespaces)
};

declare %private function env:get-variables ($frame as map(*)) as map(xs:QName, map(*)) {
  map:get($frame, 'variables')
};

declare %private function env:set-variables ($frame as map(*), $variables as map(xs:QName, map(*))) as map(*) {
  map:put($frame, 'variables', $variables)
};