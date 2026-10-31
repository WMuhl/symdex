; Pattern order matters: TreeSitterFeeder maps ts_query_cursor match.pattern_index positionally
; to a symbol kind (see PythonPatternKinds in GrammarRegistry.cs). Keep the two in sync.
; function_definition is both functions and methods. TreeSitterFeeder stores a function whose
; enclosing block belongs directly to a class_definition as a method.
; A definition nested under decorated_definition is skipped so the decorator stays inside the span.

(class_definition name: (identifier) @name) @def

(decorated_definition definition: (class_definition name: (identifier) @name)) @def

(function_definition name: (identifier) @name) @def

(decorated_definition definition: (function_definition name: (identifier) @name)) @def

(module (expression_statement (assignment left: (identifier) @name) @def))

(class_definition
  body: (block
    (expression_statement
      (assignment left: (identifier) @name) @def)))

(import_statement (dotted_name (identifier) @source)) @def

(import_from_statement (dotted_name (identifier) @source)) @def

(aliased_import alias: (identifier) @source) @def

(future_import_statement (dotted_name (identifier) @source)) @def

(call function: (identifier) @source) @def

(call
  function: (attribute
    attribute: (identifier) @source)) @def

(identifier) @source
