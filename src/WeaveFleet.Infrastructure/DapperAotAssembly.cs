// Enable Dapper.AOT source-generated interceptors for all Dapper calls in this assembly.
// This replaces runtime reflection-based column mapping with compile-time generated code,
// which is required for Native AOT publishing.
[assembly: Dapper.DapperAot]

// Bind tuple fields by name (column name → tuple element name) for all tuple-returning queries.
// Without this, Dapper.AOT cannot determine whether to bind by name or position.
[assembly: Dapper.BindTupleByName(true)]
