/// The core types and functions used by the Dark language's runtime. These
/// are not idential to the serialized types or the types used in the Editor,
/// as those have unique constraints (typically, backward compatibility or
/// continuous delivery).
module LibExecution.RuntimeTypes

// The design of these types is intended to accomodate the unique design of
// Dark, that it's being run sometimes in an editor and sometimes in
// production, etc.

// This typically represents our most accurate representation of the language
// as it is today, however, slight variations of these types are expected to
// exist in other places representing different constraints, such as how
// we've put something in some kind of storage, sending it to some API, etc.
// Those types will always be converted to these types for execution.
//
// The reason these are distinct formats from the serialized types is that
// those types are very difficult to change, while we want this to be
// straightforward to change. So we transform any serialized formats into
// this one for running. We remove any "syntactic sugar" (editor/display only
// features).
//
// These formats should never be serialized/deserialized, that defeats the
// purpose. If you need to save data of this format, create a set of new
// types and convert this type into them. (even if they are identical).
// CLEANUP: we serialize Dvals though :(
//
// This format is lossy, relative to the serialized types. Use IDs to refer
// back.

open System.Threading.Tasks
open FSharp.Control.Tasks

open Prelude
open VendoredTablecloth

module J = Prelude.Json

/// Used to reference a type defined by a User, Standard Library module, or Package
module FQTypeName =
  type StdlibTypeName = { typ : string }

  /// A type written by a Developer in their canvas
  type UserTypeName = { typ : string; version : int }

  type T =
    | Stdlib of StdlibTypeName
    | User of UserTypeName

module FQFnName =

  /// Standard Library Function Name
  type StdlibFnName = { module_ : string; function_ : string; version : int }

  /// A UserFunction is a function written by a Developer in their canvas
  type UserFnName = string

  /// The name of a function in the package manager
  type PackageFnName =
    { owner : string
      package : string
      module_ : string
      function_ : string
      version : int }

  type T =
    | User of UserFnName
    | Stdlib of StdlibFnName
    | Package of PackageFnName

  /// Same as PTParser.FQFnName.modNamePat
  let modNamePat = @"^[A-Z][a-z0-9A-Z_]*$"

  /// Same as PTParser.FQFnName.fnNamePat
  let fnnamePat = @"^([a-z][a-z0-9A-Z_]*|[-+><&|!=^%/*]{1,2})$"

  let stdlibFnName
    (module_ : string)
    (function_ : string)
    (version : int)
    : StdlibFnName =
    if module_ <> "" then assertRe "modName name must match" modNamePat module_
    assertRe "stdlib function name must match" fnnamePat function_
    assert_ "version can't be negative" [ "version", version ] (version >= 0)
    { module_ = module_; function_ = function_; version = version }

  module StdlibFnName =
    let toString (std : StdlibFnName) : string =
      let name =
        if std.module_ = "" then std.function_ else $"{std.module_}::{std.function_}"
      if std.version = 0 then name else $"{name}_v{std.version}"

  module PackageFnName =
    let toString (pkg : PackageFnName) : string =
      $"{pkg.owner}/{pkg.package}/{pkg.module_}::{pkg.function_}_v{pkg.version}"

  let toString (fqfnName : T) : string =
    match fqfnName with
    | User name -> name
    | Stdlib std -> StdlibFnName.toString std
    | Package pkg -> PackageFnName.toString pkg



  let isDBQueryFn (fqfnName : T) : bool =
    match fqfnName with
    | Stdlib std when
      std.module_ = "DB"
      && String.startsWith "query" std.function_
      && not (String.includes "ExactFields" std.function_)
      ->
      true
    | _ -> false

  let isInternalFn (fqfnName : T) : bool =
    match fqfnName with
    | Stdlib std -> std.module_ = "DarkInternal"
    | _ -> false


module DarkDateTime =
  open NodaTime
  // A datetime in Dark is always in UTC, so we don't include the utc info
  type T = LocalDateTime
  let utc = DateTimeZone.Utc

  let toZonedDateTime (dt : T) = ZonedDateTime(dt, utc, Offset.Zero)

  let toInstant (dt : T) = (toZonedDateTime dt).ToInstant()

  let toDateTimeUtc (dt : T) = (toInstant dt).ToDateTimeUtc()

  let fromInstant (i : Instant) : T = i.toUtcLocalTimeZone ()

  let fromDateTime (dt : System.DateTime) : T =
    Instant.FromDateTimeUtc dt |> fromInstant

  let toIsoString (d : T) : string = (toInstant d).toIsoString ()


/// Dark runtime type
type DType =
  // simple types
  | TUnit
  | TBool
  | TInt
  | TFloat
  | TChar
  | TStr
  | TUuid
  | TBytes
  | TDateTime
  | TPassword

  // nested types
  | TList of DType
  | TTuple of DType * DType * List<DType>
  | TFn of List<DType> * DType // replaces TLambda
  | TDB of DType

  // fake types
  | TIncomplete
  | TError

  /// Used to refer to a named type argument defined in a generic type
  /// e.g. `a` in `List<a>`
  | TVariable of string

  /// A type defined by a standard library module, a canvas/user, or a package
  /// e.g. `Result<Int, String>` is represented as `TCustomType("Result", [TInt, TStr])`
  /// `typeArgs` is the list of type arguments, if any
  | TCustomType of FQTypeName.T * typeArgs : List<DType>

  // TODO: remove all of thse in favor of TCustomType
  // Enums
  | TOption of DType
  | TResult of DType * DType

  // Records
  | TDict of DType
  | TRecord of List<string * DType> // TODO: remove in favor of TCustomType
  | THttpResponse of DType

  member this.isFn() : bool =
    match this with
    | TFn _ -> true
    | _ -> false


/// Expressions here are runtime variants of the AST in ProgramTypes, having had
/// superfluous information removed.
type Expr =
  | EInteger of id * int64
  | EBool of id * bool
  | EString of id * List<StringSegment>

  /// A single Extended Grapheme Cluster
  | ECharacter of id * string
  | EFloat of id * double
  | EUnit of id

  /// <summary>
  /// Composed of binding pattern, the expression to create bindings for,
  /// and the expression that follows, where the bound values are available
  /// </summary>
  ///
  /// <code>
  /// let str = expr1
  /// expr2
  /// </code>
  | ELet of id * LetPattern * Expr * Expr

  /// Composed of condition, expr if true, and expr if false
  | EIf of id * Expr * Expr * Expr

  /// Composed of a parameters * the expression itself
  | ELambda of id * List<id * string> * Expr

  /// Access a field of some expression (e.g. `someExpr.fieldName`)
  | EFieldAccess of id * Expr * string

  /// Reference some local variable by name
  ///
  /// i.e. after a `let binding = value`, any use of `binding`
  | EVariable of id * string

  /// This is a function call, the first expression is the value of the function.
  | EApply of id * FnTarget * typeArgs : List<DType> * args : List<Expr> * IsInPipe

  | EList of id * List<Expr>
  | ETuple of id * Expr * Expr * List<Expr>
  | ERecord of id * Option<FQTypeName.T> * List<string * Expr>
  | EConstructor of
    id *
    Option<FQTypeName.T> *
    caseName : string *
    fields : List<Expr>
  | EMatch of id * Expr * List<MatchPattern * Expr>
  | EFeatureFlag of id * Expr * Expr * Expr
  | EAnd of id * Expr * Expr
  | EOr of id * Expr * Expr

and LetPattern = LPVariable of id * name : string

and StringSegment =
  | StringText of string
  | StringInterpolation of Expr

// EApply has slightly different semantics when it is in a pipe. When piping
// into Incomplete values, we ignore the Incomplete and return the piped-in
// argument (which is the first parameter). This is to allow editing live code
// by creating a new pipe entry and then filling it in.
and IsInPipe =
  | InPipe of id // the ID of the original pipe
  | NotInPipe

and FnTarget =
  | FnName of FQFnName.T
  | FnTargetExpr of Expr

and MatchPattern =
  | MPVariable of id * string
  | MPConstructor of id * caseName : string * fieldPatterns : List<MatchPattern>
  | MPInteger of id * int64
  | MPBool of id * bool
  | MPCharacter of id * string
  | MPString of id * string
  | MPFloat of id * double
  | MPUnit of id
  | MPTuple of id * MatchPattern * MatchPattern * List<MatchPattern>

type DvalMap = Map<string, Dval>

and LambdaImpl = { parameters : List<id * string>; symtable : Symtable; body : Expr }

and FnValImpl = Lambda of LambdaImpl

and DDateTime = NodaTime.LocalDate

// We use NoComparison here to avoid accidentally using structural comparison
and [<NoComparison>] Dval =
  | DInt of int64
  | DFloat of double
  | DBool of bool
  | DUnit
  | DStr of string
  | DChar of string // TextElements (extended grapheme clusters) are provided as strings

  // compound types
  | DList of List<Dval>
  | DTuple of Dval * Dval * List<Dval>

  | DFnVal of FnValImpl

  /// Represents something that shouldn't have happened in the engine,
  /// that should have been reported elsewhere. It's usually a type error of
  /// some kind, but occasionally we'll paint ourselves into a corner and need
  /// to represent a runtime error using this.
  | DError of DvalSource * string

  /// <summary>
  /// A DIncomplete represents incomplete computation, whose source is
  /// always a Blank. When the code runs into a blank, it must return
  /// incomplete because the code is not finished. An incomplete value
  /// results in a 500 because it is a developer error.
  /// </summary>
  ///
  /// <remarks>
  /// Propagating DIncompletes is straightforward: any computation
  /// relying on an incomplete must itself be incomplete.
  ///
  /// Some examples:
  /// - calling a function with an incomplete as a parameter is an
  ///   incomplete function call.
  /// - an if statement with an incomplete in the cond must be incomplete.
  ///
  /// But computation that doesn't rely on the incomplete value can
  /// ignore it:
  ///
  /// - an if statement which with a blank in the ifbody and a
  ///   complete expression in the elsebody will execute just fine if
  ///   cond is false. It has not hit any part of the program that is
  ///   being worked on.
  ///
  /// - a list with blanks in it can just ignore the blanks.
  /// - an incomplete in a list should be filtered out, because the
  ///   program has not been completed, and so that list entry just
  ///   doesn't "exist" yet.
  /// - incompletes in keys or values of objects cause the entire row
  ///   to be ignored.
  /// </remarks>
  | DIncomplete of DvalSource

  | DDB of string
  | DDateTime of DarkDateTime.T
  | DPassword of Password
  | DUuid of System.Guid
  | DBytes of byte array

  // TODO: remove DHttpResponse eventually - this should really just be a DRecord
  // of a type that is defined in the standard library (http module)
  | DHttpResponse of int64 * List<string * string> * Dval

  // TODO: replace with something like
  // `| DRecord of FQTypeName.T * DvalMap`
  | DObj of DvalMap

  // TODO: merge DOption and DResult into DConstructor once the Option and Result types
  // are defined in the Option and Result modules of the standard library
  | DOption of Option<Dval>
  | DResult of Result<Dval, Dval>

  // TODO: consider renaming - this is a _value_ so it's already been "Constructed"
  | DConstructor of
    typeName : Option<FQTypeName.T> *
    caseName : string *
    fields : List<Dval>


and DvalTask = Ply<Dval>

and Symtable = Map<string, Dval>



/// Record the source of an incomplete or error. Would be useful to add more
/// information later, such as the iteration count that led to this, or
/// something like a stack trace
and DvalSource =
  /// We do not have context to supply an identifier
  | SourceNone

  /// Caused by an expression of `id` within the given `tlid`
  | SourceID of tlid * id

and Param =
  { name : string
    typ : DType
    blockArgs : List<string>
    description : string }

  static member make (name : string) (typ : DType) (description : string) : Param =
    assert_ "make called on TFn" [ "name", name ] (not (typ.isFn ()))
    { name = name; typ = typ; description = description; blockArgs = [] }

  static member makeWithArgs
    (name : string)
    (typ : DType)
    (description : string)
    (blockArgs : List<string>)
    : Param =
    assert_ "makeWithArgs not called on TFn" [ "name", name ] (typ.isFn ())
    { name = name; typ = typ; description = description; blockArgs = blockArgs }

module CustomType =
  type RecordField = { id : id; name : string; typ : DType }

  type EnumField = { id : id; typ : DType; label : Option<string> }
  type EnumCase = { id : id; name : string; fields : List<EnumField> }

  type T =
    | Record of firstField : RecordField * additionalFields : List<RecordField>
    | Enum of firstCase : EnumCase * additionalCases : List<EnumCase>

/// Functions for working with Dark runtime expressions
module Expr =
  let toID (expr : Expr) : id =
    match expr with
    | EInteger (id, _)
    | EString (id, _)
    | ECharacter (id, _)
    | EBool (id, _)
    | EUnit id
    | EFloat (id, _)
    | EVariable (id, _)
    | EFieldAccess (id, _, _)
    | ELambda (id, _, _)
    | ELet (id, _, _, _)
    | EIf (id, _, _, _)
    | EApply (id, _, _, _, _)
    | EList (id, _)
    | ETuple (id, _, _, _)
    | ERecord (id, _, _)
    | EConstructor (id, _, _, _)
    | EFeatureFlag (id, _, _, _)
    | EMatch (id, _, _)
    | EAnd (id, _, _)
    | EOr (id, _, _) -> id

/// Functions for working with Dark Let patterns
module LetPattern =
  let toID (pat : LetPattern) : id =
    match pat with
    | LPVariable (id, _) -> id

/// Functions for working with Dark match patterns
module MatchPattern =
  let toID (pat : MatchPattern) : id =
    match pat with
    | MPInteger (id, _)
    | MPString (id, _)
    | MPCharacter (id, _)
    | MPBool (id, _)
    | MPUnit id
    | MPFloat (id, _)
    | MPVariable (id, _)
    | MPTuple (id, _, _, _)
    | MPConstructor (id, _, _) -> id

/// Functions for working with Dark runtime values
module Dval =
  /// A Fake Dval is some control-flow that's modelled in the interpreter as a
  /// Dval. This is sort of like an Exception. Anytime we see a FakeDval we return
  /// it instead of operating on it, including when they're put in a list, in a
  /// value, in a record, as a parameter to a function, etc.
  let isFake (dv : Dval) : bool =
    match dv with
    | DError _ -> true
    | DIncomplete _ -> true
    | _ -> false

  let isIncomplete (dv : Dval) : bool =
    match dv with
    | DIncomplete _ -> true
    | _ -> false

  let isDError (dv : Dval) : bool =
    match dv with
    | DError _ -> true
    | _ -> false

  let toPairs (dv : Dval) : Result<List<string * Dval>, string> =
    match dv with
    | DObj obj -> Ok(Map.toList obj)
    | _ -> Error "expecting str"

  /// Gets the Dark runtime type from a runtime value
  let rec toType (dv : Dval) : DType =
    let any = TVariable "a"

    match dv with
    | DInt _ -> TInt
    | DFloat _ -> TFloat
    | DBool _ -> TBool
    | DUnit -> TUnit
    | DChar _ -> TChar
    | DStr _ -> TStr
    | DList (head :: _) -> TList(toType head)
    | DList [] -> TList any
    | DTuple (first, second, theRest) ->
      TTuple(toType first, toType second, List.map toType theRest)
    | DObj map ->
      map |> Map.toList |> List.map (fun (k, v) -> (k, toType v)) |> TRecord
    | DFnVal _ -> TFn([], any) // CLEANUP: can do better here
    | DError _ -> TError
    | DIncomplete _ -> TIncomplete
    | DHttpResponse (_, _, dv) -> THttpResponse(toType dv)
    | DDB _ -> TDB any
    | DDateTime _ -> TDateTime
    | DPassword _ -> TPassword
    | DUuid _ -> TUuid
    | DOption None -> TOption any
    | DOption (Some v) -> TOption(toType v)
    | DResult (Ok v) -> TResult(toType v, any)
    | DResult (Error v) -> TResult(any, toType v)
    | DBytes _ -> TBytes
    | DConstructor (typeName, caseName, fields) ->
      match typeName with
      | Some typeName ->
        let typeArgs = List.map toType fields
        TCustomType(typeName, typeArgs)
      | None ->
        match caseName, fields with
        // option
        | "Nothing", [] -> TOption any
        | "Just", [ arg ] -> TOption(toType arg)

        // result
        | "Ok", [ arg ] -> TResult(any, any)
        | "Error", [ arg ] -> TResult(any, any)

        // unrecognized
        | _ -> any // might be better to error here


  /// <summary>
  /// Checks if a runtime's value matches a given type
  /// </summary>
  /// <remarks>
  /// In OCaml, we had simpler types so we could just call toType and compare.
  /// But now we have nested types so they need to be checked deeper. Note:
  /// there is also "real" type checking elsewhere - this should be unified.
  /// Note, this is primarily used to figure out which argument has ALREADY not
  /// matched the actual runtime parameter type of the called function. So more
  /// accuracy is better, as the runtime is perfectly accurate.
  /// </summary>
  let rec typeMatches (typ : DType) (dv : Dval) : bool =
    match (dv, typ) with
    | _, TVariable _ -> true
    | DInt _, TInt
    | DFloat _, TFloat
    | DBool _, TBool
    | DUnit, TUnit
    | DStr _, TStr
    | DDateTime _, TDateTime
    | DPassword _, TPassword
    | DUuid _, TUuid
    | DChar _, TChar
    | DDB _, TDB _
    | DBytes _, TBytes -> true
    | DTuple (first, second, theRest), TTuple (firstType, secondType, otherTypes) ->
      let pairs =
        [ (first, firstType); (second, secondType) ] @ List.zip theRest otherTypes

      pairs |> List.all (fun (v, subtype) -> typeMatches subtype v)
    | DList l, TList t -> List.all (typeMatches t) l
    | DObj m, TDict t -> Map.all (typeMatches t) m
    | DObj m, TRecord pairs ->
      let actual = Map.toList m |> List.sortBy Tuple2.first
      let expected = pairs |> List.sortBy Tuple2.first

      if List.length actual <> List.length expected then
        false
      else
        List.zip actual expected
        |> List.all (fun ((aField, aVal), (eField, eType)) ->
          aField = eField && typeMatches eType aVal)

    | DFnVal (Lambda l), TFn (parameters, _) ->
      List.length parameters = List.length l.parameters
    | DOption None, TOption _ -> true
    | DOption (Some v), TOption t
    | DResult (Ok v), TResult (t, _) -> typeMatches t v
    | DResult (Error v), TResult (_, t) -> typeMatches t v
    | DHttpResponse (_, _, body), THttpResponse t -> typeMatches t body

    | DObj _, TCustomType _ ->
      // UserTypeTODO revisit
      // 1. get Definition of UserType
      //   we likely need a `(userTypeMap: Map<FQTypeName.T, UserType.Definition>)` passed in
      //
      // 2. match against that
      //  match def with
      //  | Enum _ -> false
      //  | Record (...) ->
      //    ...
      false

    | DConstructor _, TCustomType _ ->
      // UserTypeTODO revisit
      // 1. get Definition of UserType
      //   we likely need a `(userTypeMap: Map<FQTypeName.T, UserType.Definition>)` passed in
      //
      // 2. match against that
      //  match def with
      //  | Record _ -> false
      //  | Enum (...) ->
      //    ...
      false

    // Dont match these fakevals, functions do not have these types
    | DError _, _
    | DIncomplete _, _ -> false
    // exhaustiveness checking
    | DInt _, _
    | DFloat _, _
    | DBool _, _
    | DUnit, _
    | DStr _, _
    | DDateTime _, _
    | DPassword _, _
    | DUuid _, _
    | DChar _, _
    | DDB _, _
    | DBytes _, _
    | DList _, _
    | DTuple _, _
    | DObj _, _
    | DObj _, _
    | DFnVal _, _
    | DOption _, _
    | DResult _, _
    | DHttpResponse _, _
    | DObj _, _
    | DConstructor _, _ -> false


  let int (i : int) = DInt(int64 i)
  let parseInt (i : string) = DInt(parseInt64 i)


  // Dvals should never be constructed that contain fakevals - the fakeval
  // should always propagate (though, there are specific cases in the
  // interpreter where they are discarded instead of propagated; still they are
  // never put into other dvals). These static members check before creating the values

  let list (list : List<Dval>) : Dval =
    List.find (fun (dv : Dval) -> isFake dv) list
    |> Option.defaultValue (DList list)

  let obj (fields : List<string * Dval>) : Dval =
    // Give a warning for duplicate keys
    List.fold
      (DObj Map.empty)
      (fun m (k, v) ->
        match m, k, v with
        // If we're propagating a fakeval keep doing it. We handle it without this line but let's be certain
        | m, _k, _v when isFake m -> m
        // Skip empty rows
        | _, "", _ -> m
        | _, _, DIncomplete _ -> m
        // Errors should propagate (but only if we're not already propagating an error)
        | DObj _, _, v when isFake v -> v
        // Error if the key appears twice
        | DObj m, k, _v when Map.containsKey k m ->
          DError(SourceNone, $"Duplicate key: {k}")
        // Otherwise add it
        | DObj m, k, v -> DObj(Map.add k v m)
        // If we haven't got a DObj we're propagating an error so let it go
        | m, _, _ -> m)
      fields



  let resultOk (dv : Dval) : Dval = if isFake dv then dv else DResult(Ok dv)

  let resultError (dv : Dval) : Dval = if isFake dv then dv else DResult(Error dv)

  // Wraps in a DResult after checking that the value is not a fakeval
  let result (dv : Result<Dval, Dval>) : Dval =
    match dv with
    | Ok dv -> resultOk dv
    | Error dv -> resultError dv

  let optionJust (dv : Dval) : Dval = if isFake dv then dv else DOption(Some dv)

  // Wraps in a DOption after checking that the value is not a fakeval
  let option (dv : Option<Dval>) : Dval =
    match dv with
    | Some dv -> optionJust dv // checks isFake
    | None -> DOption None

  let errStr (s : string) : Dval = DError(SourceNone, s)

  let errSStr (source : DvalSource) (s : string) : Dval = DError(source, s)

module Handler =
  type CronInterval =
    | EveryDay
    | EveryWeek
    | EveryFortnight
    | EveryHour
    | Every12Hours
    | EveryMinute

  type Spec =
    | HTTP of path : string * method : string
    | Worker of name : string
    | Cron of name : string * interval : Option<CronInterval>
    | REPL of name : string

  type T = { tlid : tlid; ast : Expr; spec : Spec }

module DB =
  type Col = string * DType
  type T = { tlid : tlid; name : string; cols : List<Col>; version : int }

module UserType =
  // TODO: consider flattening this (just type UserType = { ... }, without the module level)
  type Definition = CustomType.T
  type T = { tlid : tlid; name : FQTypeName.UserTypeName; definition : Definition }

module UserFunction =
  type Parameter = { name : string; typ : DType; description : string }

  type T =
    { tlid : tlid
      name : string
      typeParams : List<string>
      parameters : List<Parameter>
      returnType : DType
      description : string
      infix : bool
      body : Expr }

module Toplevel =
  type T =
    | TLHandler of Handler.T
    | TLDB of DB.T
    | TLFunction of UserFunction.T
    | TLType of UserType.T

  let toTLID (tl : T) : tlid =
    match tl with
    | TLHandler h -> h.tlid
    | TLDB db -> db.tlid
    | TLFunction f -> f.tlid
    | TLType t -> t.tlid

module Secret =
  type T = { name : string; value : string }


// ------------
// Functions
// ------------

module Package =
  type Parameter = { name : string; typ : DType; description : string }

  type Fn =
    { name : FQFnName.PackageFnName
      body : Expr
      typeParams : List<string>
      parameters : List<Parameter>
      returnType : DType
      description : string
      author : string
      deprecated : bool
      tlid : tlid }


/// <summary>
/// Used to mark whether a function can be run on the client rather than backend.
/// </summary>
/// <remarks>
/// The runtime needs to know whether to save a function's results when it
/// runs. Pure functions that can be run on the client do not need to have
/// their results saved.
/// In addition, some functions can be run without side-effects; to give
/// the user a good experience, we can run them as soon as they are added.
/// this includes DateTime::now and Int::random.
/// </remarks>
type Previewable =
  /// The same inputs will always yield the same outputs,
  /// so we don't need to save results. e.g. `DateTime::add`
  | Pure

  /// Output may vary with the same inputs, though we can safely preview.
  /// e.g. `DateTime::now`. We should save the results.
  | ImpurePreviewable

  /// Can only be run on the server. e.g. `DB::update`
  /// We should save the results.
  | Impure

/// Used to mark whether a function has been deprecated, and if so,
/// details about possible replacements/alternatives, and reasoning
type Deprecation =
  | NotDeprecated

  /// The exact same function is available under a new, preferred name
  | RenamedTo of FQFnName.StdlibFnName

  /// This has been deprecated and has a replacement we can suggest
  | ReplacedBy of FQFnName.StdlibFnName

  /// This has been deprecated and not replaced, provide a message for the user
  | DeprecatedBecause of string

/// Used to mark whether a function has an equivalent that can be
/// used within a Postgres query.
type SqlSpec =
  /// Can be implemented, but we haven't yet
  | NotYetImplemented

  /// This is not a function which can be queried
  | NotQueryable

  /// A query function (it can't be called inside a query, but its argument can be a query)
  | QueryFunction

  /// Can be implemented by a given builtin postgres 9.6 operator with 1 arg (eg `@ x`)
  | SqlUnaryOp of string

  /// Can be implemented by a given builtin postgres 9.6 operator with 2 args (eg `x + y`)
  | SqlBinOp of string

  /// Can be implemented by a given builtin postgres 9.6 function
  | SqlFunction of string

  /// Can be implemented by a given builtin postgres 9.6 function with extra arguments that go first
  | SqlFunctionWithPrefixArgs of string * List<string>

  /// Can be implemented by a given builtin postgres 9.6 function with extra arguments that go last
  | SqlFunctionWithSuffixArgs of string * List<string>

  // Can be implemented by given callback that receives 1 SQLified-string argument
  // | SqlCallback of (string -> string)

  /// Can be implemented by given callback that receives 2 SQLified-string argument
  | SqlCallback2 of (string -> string -> string)

  member this.isQueryable() : bool =
    match this with
    | NotYetImplemented
    | NotQueryable
    | QueryFunction -> false
    | SqlUnaryOp _
    | SqlBinOp _
    | SqlFunction _
    | SqlFunctionWithPrefixArgs _
    | SqlFunctionWithSuffixArgs _
    | SqlCallback2 _ -> true


/// A built-in standard library type
type BuiltInType =
  { name : FQTypeName.StdlibTypeName
    typeParams : List<string>
    definition : CustomType.T
    description : string }

/// A built-in standard library function
type BuiltInFn =
  { name : FQFnName.StdlibFnName
    typeParams : List<string>
    parameters : List<Param>
    returnType : DType
    description : string
    previewable : Previewable
    deprecated : Deprecation
    sqlSpec : SqlSpec
    fn : BuiltInFnSig }

and Fn =
  { name : FQFnName.T
    typeParams : List<string>
    parameters : List<Param>
    returnType : DType
    previewable : Previewable
    sqlSpec : SqlSpec

    // Functions can be run in JS if they have an implementation in LibExecution.
    // Functions whose implementation is in BackendOnlyStdLib can only be implemented on the server.

    /// <remarks>
    /// May throw an exception, though we're trying to get them to never throw exceptions.
    /// </remarks>
    fn : FnImpl }

and BuiltInFnSig =
  (ExecutionState *

  // type args
  List<DType> *

  // fn args
  List<Dval>)
    -> DvalTask

and FnImpl =
  | StdLib of BuiltInFnSig
  | UserFunction of tlid * Expr
  | PackageFunction of tlid * Expr


// CLEANUP consider renaming to `ExecutionType`, `EvaluationMode`, etc.
/// Represents the context in which we're evaluating some code
and RealOrPreview =
  /// We are evaluating an expression normally
  | Real

  /// We are previewing the evaluation of some expression within the editor.
  | Preview

and FunctionRecord = tlid * FQFnName.T * id

and TraceDval = bool -> id -> Dval -> unit

and TraceTLID = tlid -> unit

and LoadFnResult = FunctionRecord -> List<Dval> -> Option<Dval * NodaTime.Instant>

and StoreFnResult = FunctionRecord -> Dval list -> Dval -> unit

and LoadFnArguments = tlid -> List<DvalMap * NodaTime.Instant>

and StoreFnArguments = tlid -> DvalMap -> unit

/// Every part of a user's program
and ProgramContext =
  { canvasID : CanvasID
    accountID : UserID
    dbs : Map<string, DB.T>
    userFns : Map<string, UserFunction.T>
    userTypes : Map<FQTypeName.UserTypeName, UserType.T>
    secrets : List<Secret.T> }

  // TODO remove this, probably?
  // this is theoretically prepared for stdlibTypes and packageTypes to exist
  // I'm not sure how else to handle this, but this likely isn't ideal
  // TODO: at the _very_ least, review all usages - consider if we should treat each case special in any way
  member this.allTypes : Map<FQTypeName.T, CustomType.T> =
    this.userTypes
    // TODO: I'd normally use F#'s native "Map.map"
    // but we've overwritten that such that this turns out ugly
    // Is there a better way to do this, even with the overwriting?
    |> Map.toList
    |> List.map (fun (name, userType) -> FQTypeName.User name, userType.definition)
    |> Map

/// Set of callbacks used to trace the interpreter
and Tracing =
  { traceDval : TraceDval
    traceTLID : TraceTLID
    loadFnResult : LoadFnResult
    storeFnResult : StoreFnResult
    loadFnArguments : LoadFnArguments
    storeFnArguments : StoreFnArguments
    realOrPreview : RealOrPreview }

/// Used for testing
and TestContext =
  { mutable sideEffectCount : int

    mutable exceptionReports : List<string * string * Metadata>
    mutable expectedExceptionCount : int
    postTestExecutionHook : TestContext -> Dval -> unit }

/// Non-user-specific functionality needed to run code
and Libraries =
  { stdlibTypes : Map<FQTypeName.T, BuiltInType>
    stdlibFns : Map<FQFnName.T, BuiltInFn>

    packageFns : Map<FQFnName.T, Package.Fn> }

and ExceptionReporter = ExecutionState -> Metadata -> exn -> unit

and Notifier = ExecutionState -> string -> Metadata -> unit

/// All state used while running a program
and ExecutionState =
  { libraries : Libraries
    tracing : Tracing
    program : ProgramContext
    test : TestContext

    /// Called to report exceptions
    reportException : ExceptionReporter

    /// Called to notify that something of interest (that isn't an exception)
    /// has happened.
    ///
    /// Useful for tracking behaviour we want to deprecate, understanding what
    /// users are doing, etc.
    notify : Notifier

    /// TLID of the currently executing handler/fn
    tlid : tlid

    executingFnName : Option<FQFnName.T>

    /// <summary>
    /// Callstack of functions that have been called as part of execution
    /// </summary>
    ///
    /// <remarks>
    /// Used for recursion detection in the editor.
    /// In the editor, we call all paths to show live values,
    /// but with recursion that causes infinite recursion.
    /// </remarks>
    callstack : Set<FQFnName.T>

    /// Whether the currently executing code is really being executed
    /// (as opposed to being previewed for traces)
    onExecutionPath : bool }

let consoleReporter : ExceptionReporter =
  fun _state (metadata : Metadata) (exn : exn) ->
    printException "runtime-error" metadata exn

let consoleNotifier : Notifier =
  fun _state msg tags ->
    print $"A notification happened in the runtime:\n  {msg}\n  {tags}\n\n"

let builtInFnToFn (fn : BuiltInFn) : Fn =
  { name = FQFnName.Stdlib fn.name
    typeParams = fn.typeParams
    parameters = fn.parameters
    returnType = fn.returnType
    previewable = fn.previewable
    sqlSpec = fn.sqlSpec
    fn = StdLib fn.fn }

let userFnToFn (fn : UserFunction.T) : Fn =
  let toParam (p : UserFunction.Parameter) : Param =
    { name = p.name; typ = p.typ; description = p.description; blockArgs = [] }

  { name = FQFnName.User fn.name
    typeParams = fn.typeParams
    parameters = fn.parameters |> List.map toParam
    returnType = fn.returnType
    previewable = Impure
    sqlSpec = NotQueryable
    fn = UserFunction(fn.tlid, fn.body) }

let packageFnToFn (fn : Package.Fn) : Fn =
  let toParam (p : Package.Parameter) : Param =
    { name = p.name; typ = p.typ; description = p.description; blockArgs = [] }

  { name = FQFnName.Package fn.name
    typeParams = fn.typeParams
    parameters = fn.parameters |> List.map toParam
    returnType = fn.returnType
    previewable = Impure
    sqlSpec = NotQueryable
    fn = PackageFunction(fn.tlid, fn.body) }

// -------------------------
// renamed fns
// -------------------------

/// To cut down on the amount of code, when we rename a function and make no other
/// changes, we don't duplicate it. Instead, we rename it and add the rename to this
/// list. At startup, the renamed functions are created and added to the list.
///
/// Renames is old name first, new name second. The new one should still be in the
/// codebase, the old one should not. If a function is renamed multiple times, add the
/// latest rename first.
let renameFunctions
  (renames : List<FQFnName.StdlibFnName * FQFnName.StdlibFnName>)
  (existing : List<BuiltInFn>)
  : List<BuiltInFn> =
  let existingMap = existing |> List.map (fun fn -> fn.name, fn) |> Map
  let newFns =
    renames
    |> List.fold Map.empty (fun renamedFns (oldName, newName) ->
      let newFn =
        Map.tryFind newName (Map.mergeFavoringLeft renamedFns existingMap)
        |> Exception.unwrapOptionInternal
             $"all fns should exist {oldName} -> {newName}"
             [ "oldName", oldName; "newName", newName ]
      Map.add
        oldName
        { newFn with name = oldName; deprecated = RenamedTo newName }
        renamedFns)
    |> Map.values
  existing @ newFns
