; Pattern order matters: TreeSitterFeeder maps ts_query_cursor match.pattern_index positionally
; to a symbol kind (see TypeScriptPatternKinds in GrammarRegistry.cs). Keep the two in sync.
; Shared by the typescript and tsx grammars (both expose the same node types used here).

(class_declaration name: (type_identifier) @name) @def

(interface_declaration name: (type_identifier) @name) @def

(type_alias_declaration name: (type_identifier) @name) @def

(function_declaration name: (identifier) @name) @def

(method_definition name: (property_identifier) @name) @def

(public_field_definition name: (property_identifier) @name) @def

(export_statement (lexical_declaration (variable_declarator name: (identifier) @name))) @def

(import_statement source: (string) @source) @def

(import_specifier name: (identifier) @source) @def

; Calls, including obj?.foo() (optional_chain is a sibling of property) and tagged templates
; (call_expression arguments may be a template_string). new is a call, matching Roslyn,
; which stores constructor invocations as call because constructors are methods.
(call_expression function: (identifier) @source) @def

(call_expression
  function: (member_expression
    property: (property_identifier) @source)) @def

(new_expression constructor: (identifier) @source) @def

(new_expression
  constructor: (member_expression
    property: (property_identifier) @source)) @def

; Class and interface heritage. Recorded as implementation so refs lists subclasses and
; implementers, and callers (call + reference) does not. Type arguments stay references.
(extends_clause value: (identifier) @source) @def

(extends_clause
  value: (member_expression
    property: (property_identifier) @source)) @def

(implements_clause (type_identifier) @source) @def

(implements_clause
  (generic_type
    name: (type_identifier) @source)) @def

(implements_clause
  (nested_type_identifier
    name: (type_identifier) @source)) @def

(implements_clause
  (generic_type
    name: (nested_type_identifier
      name: (type_identifier) @source))) @def

(extends_type_clause type: (type_identifier) @source) @def

(extends_type_clause
  type: (generic_type
    name: (type_identifier) @source)) @def

(extends_type_clause
  type: (nested_type_identifier
    name: (type_identifier) @source)) @def

(extends_type_clause
  type: (generic_type
    name: (nested_type_identifier
      name: (type_identifier) @source))) @def

; Re-export module paths (export { a } from "./mod"). The exported name is an identifier.
(export_statement source: (string) @source) @def

; Other identifier uses. JSX element names are identifier nodes, so this matches <Foo /> on the
; tsx grammar without naming JSX node types (those exist only in tsx; this query is shared).
; TreeSitterFeeder drops declaration names, parameters, property keys, and the identifier
; already recorded as a call or implementation.
(identifier) @source

(type_identifier) @source

(shorthand_property_identifier) @source
