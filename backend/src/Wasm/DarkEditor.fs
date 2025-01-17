module Wasm.DarkEditor

open System
open System.Net.Http
open System.Threading.Tasks

open Microsoft.JSInterop

open Prelude
open Tablecloth

open LibExecution.RuntimeTypes
open EvalHelpers

let debug (arg : string) = WasmHelpers.callJSFunction "console.log" [ arg ]


/// Source of the editor (types, functions)
type EditorSource = { types : List<UserType.T>; fns : List<UserFunction.T> }


let stdLib =
  LibExecution.StdLib.combine
    [ StdLibExecution.StdLib.contents; Wasm.StdLib.contents ]
    []
    []

let ensureNonFakeDval (dv : Dval) : Result<Dval, string> =
  match dv with
  | DError(_source, err) ->
    Error $"Error calling handleEvent with provided args: {err}"

  | DIncomplete(_) -> Error $"handleError returned Incomplete"

  | DFnVal(_) -> Error $"handleError returned DFnVal"

  | result -> Ok dv


/// Load the Darklang program that manages the state of and interactions with
/// the JS side of the editor.
[<JSInvokable>]
let LoadClient (canvasName : string) : Task<string> =
  let sourceURL = $"http://{canvasName}.dlio.localhost:11003/assets/client.dark"
  let parseURL = $"http://{canvasName}.dlio.localhost:11003/get-program-json"

  task {
    let httpClient = new HttpClient()

    let! clientSource =
      task {
        // text of client.dark
        let! response = httpClient.GetAsync sourceURL |> Async.AwaitTask
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        // parse client.dark with another endpoint,
        // which then serializes as JSON so we can deserialize below
        let! response =
          httpClient.PostAsync(parseURL, new StringContent(responseBody))
          |> Async.AwaitTask
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

        return Json.Vanilla.deserialize<EditorSource> responseBody
      }

    let! initialState =
      let state = getStateForEval stdLib clientSource.types clientSource.fns
      Ply.toTask (
        LibExecution.Interpreter.callFn
          state
          (gid ())
          (FQFnName.User { modules = []; function_ = "init"; version = 0 })
          []
          [ DUnit ]
      )

    match ensureNonFakeDval initialState with
    | Ok result ->
      Libs.Editor.editor <-
        { Types = clientSource.types
          Functions = clientSource.fns
          CurrentState = initialState }

      return LibExecution.DvalReprDeveloper.toRepr result

    | Error err ->
      WasmHelpers.callJSFunction "console.error" [ err ]
      return err
  }


[<JSInvokable>]
let HandleEvent (serializedEvent : string) : Task<string> =
  task {
    let state =
      getStateForEval stdLib Libs.Editor.editor.Types Libs.Editor.editor.Functions

    let! result =
      Ply.toTask (
        LibExecution.Interpreter.callFn
          state
          (gid ())
          (FQFnName.User { modules = []; function_ = "handleEvent"; version = 0 })
          []
          [ DString serializedEvent ]
      )

    match ensureNonFakeDval result with
    | Ok result -> return LibExecution.DvalReprDeveloper.toRepr result
    | Error err ->
      WasmHelpers.callJSFunction "console.error" [ err ]
      return err
  }
