module LibExecution.DvalReprInternalHash

open Prelude

let supportedHashVersions : int list = [ 2 ]

// CLEANUP switch to 2 once rolled out to clients. Should just work
let currentHashVersion : int = 2

let hash (version : int) (arglist : List<RuntimeTypes.Dval>) : string =
  match version with
  | 2 -> DvalReprInternalNew.toHashV2 arglist
  | _ -> Exception.raiseInternal $"Invalid Dval.hash version" [ "version", version ]
