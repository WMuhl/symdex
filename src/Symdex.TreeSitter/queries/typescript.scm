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
