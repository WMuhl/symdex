; Pattern order matters: TreeSitterFeeder maps ts_query_cursor match.pattern_index positionally
; to a symbol kind (see CSharpPatternKinds in GrammarRegistry.cs). Keep the two in sync.

(class_declaration name: (identifier) @name) @def

(interface_declaration name: (identifier) @name) @def

(struct_declaration name: (identifier) @name) @def

(enum_declaration name: (identifier) @name) @def

(method_declaration name: (identifier) @name) @def

(constructor_declaration name: (identifier) @name) @def

(property_declaration name: (identifier) @name) @def

(field_declaration (variable_declaration (variable_declarator name: (identifier) @name))) @def

(using_directive (_) @source) @def
