namespace Sutil

open System

type IStoreDebugger =
    interface
        abstract Value : obj
        abstract NumSubscribers : int
    end


module DevToolsControl =

    type SutilOptions =
        { SlowAnimations: bool
          LoggingEnabled: bool }

    let mutable Options =
        { SlowAnimations = false
          LoggingEnabled = false }

    type Version =
        { Major: int
          Minor: int
          Patch: int }
        override v.ToString() = $"{v.Major}.{v.Minor}.{v.Patch}"

    type IMountPoint =
        interface
            abstract Id : string
            abstract Remount : unit -> unit
        end

    type IControlBlock =
        interface
            abstract ControlBlockVersion : int
            abstract Version : Version
            abstract GetOptions : unit -> SutilOptions
            abstract SetOptions : SutilOptions -> unit
            abstract GetStores : unit -> int array
            abstract GetStoreById : int -> IStoreDebugger
            abstract GetLogCategories : unit -> (string * bool) array
            abstract SetLogCategories : (string * bool) array -> unit
            abstract GetMountPoints : unit -> IMountPoint array
            abstract PrettyPrint : int -> unit
        end

    let getControlBlock doc : IControlBlock = Interop.get doc "__sutil_cb"
    let setControlBlock doc (cb: IControlBlock) = Interop.set doc "__sutil_cb" cb

    let initialise doc controlBlock = setControlBlock doc controlBlock

type Unsubscribe = unit -> unit

type IStore<'T> =
    interface
        inherit IObservable<'T>
        inherit IDisposable
        abstract Update : f: ('T -> 'T) -> unit
        abstract Value : 'T
        abstract Debugger : IStoreDebugger
    end

type Store<'T> = IStore<'T>

type Update<'Model> = ('Model -> 'Model) -> unit // A store updater. Store updates by being passed a model updater

type Dispatch<'Msg> = 'Msg -> unit // Message dispatcher

type Cmd<'Msg> = (Dispatch<'Msg> -> unit) list // List of commands. A command needs a dispatcher to execute

//
// All Cmd code take from Fable.Elmish/src/cmd.fs, by Maxel Mangime
// TODO: Refactor this into Sutil.Elmish module
//
#if FABLE_COMPILER
module internal Timer =
    open System.Timers

    let delay interval callback =
        let t =
            new Timer(float interval, AutoReset = false)

        t.Elapsed.Add callback
        t.Enabled <- true
        t.Start()
#endif

module Cmd =

    let none : Cmd<'Msg> = []

    let map (f: 'MsgA -> 'MsgB) (cmd: Cmd<'MsgA>) : Cmd<'MsgB> =
        cmd
        |> List.map (fun g -> (fun dispatch -> f >> dispatch) >> g)

    let ofMsg msg : Cmd<'Msg> = [ fun d -> d msg ]

    let batch (cmds: Cmd<'Msg> list) : Cmd<'Msg> = cmds |> List.concat

    module OfFunc =
        let either (task: 'args -> _) (a: 'args) (success: _ -> 'msg') (error: _ -> 'msg') =
            [ fun d ->
                  try
                      task a |> (success >> d)
                  with x -> x |> (error >> d) ]

        let perform (task: 'args -> _) (a: 'args) (success: _ -> 'msg') =
            [ fun d ->
                  try
                      task a |> (success >> d)
                  with _ -> () ]

        let attempt (task: 'args -> unit) (a: 'args) (error: _ -> 'msg') =
            [ fun d ->
                  try
                      task a
                  with x -> x |> (error >> d) ]

    module OfAsyncWith =
        /// Command that will evaluate an async block and map the result
        /// into success or error (of exception)
        let either
            (start: Async<unit> -> unit)
            (task: 'a -> Async<_>)
            (arg: 'a)
            (ofSuccess: _ -> 'msg)
            (ofError: _ -> 'msg)
            : Cmd<'msg> =
            let bind dispatch =
                async {
                    let! r = task arg |> Async.Catch

                    dispatch (
                        match r with
                        | Choice1Of2 x -> ofSuccess x
                        | Choice2Of2 x -> ofError x
                    )
                }

            [ bind >> start ]

        /// Command that will evaluate an async block and map the success
        let perform (start: Async<unit> -> unit) (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch =
                async {
                    let! r = task arg |> Async.Catch

                    match r with
                    | Choice1Of2 x -> dispatch (ofSuccess x)
                    | _ -> ()
                }

            [ bind >> start ]

        /// Command that will evaluate an async block and map the error (of exception)
        let attempt (start: Async<unit> -> unit) (task: 'a -> Async<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            let bind dispatch =
                async {
                    let! r = task arg |> Async.Catch

                    match r with
                    | Choice2Of2 x -> dispatch (ofError x)
                    | _ -> ()
                }

            [ bind >> start ]

        /// Command that will evaluate an async block to the message
        let result (start: Async<unit> -> unit) (task: Async<'msg>) : Cmd<'msg> =
            let bind dispatch =
                async {
                    let! r = task
                    dispatch r
                }

            [ bind >> start ]

    module OfAsync =
#if FABLE_COMPILER
        let start x =
            Timer.delay 0 (fun _ -> Async.StartImmediate x)
#else
        let inline start x = Async.Start x
#endif
        /// Command that will evaluate an async block and map the result
        /// into success or error (of exception)
        let inline either (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.either start task arg ofSuccess ofError

        /// Command that will evaluate an async block and map the success
        let inline perform (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.perform start task arg ofSuccess

        /// Command that will evaluate an async block and map the error (of exception)
        let inline attempt (task: 'a -> Async<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.attempt start task arg ofError

        /// Command that will evaluate an async block to the message
        let inline result (task: Async<'msg>) : Cmd<'msg> = OfAsyncWith.result start task

    module OfAsyncImmediate =
        /// Command that will evaluate an async block and map the result
        /// into success or error (of exception)
        let inline either (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.either Async.StartImmediate task arg ofSuccess ofError

        /// Command that will evaluate an async block and map the success
        let inline perform (task: 'a -> Async<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.perform Async.StartImmediate task arg ofSuccess

        /// Command that will evaluate an async block and map the error (of exception)
        let inline attempt (task: 'a -> Async<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsyncWith.attempt Async.StartImmediate task arg ofError

        /// Command that will evaluate an async block to the message
        let inline result (task: Async<'msg>) : Cmd<'msg> =
            OfAsyncWith.result Async.StartImmediate task

#if FABLE_COMPILER
    module OfPromise =
        /// Command to call `promise` block and map the results
        let either
            (task: 'a -> Fable.Core.JS.Promise<_>)
            (arg: 'a)
            (ofSuccess: _ -> 'msg)
            (ofError: #exn -> 'msg)
            : Cmd<'msg> =
            let bind dispatch =
                (task arg)
                    .``then``(ofSuccess >> dispatch)
                    .catch (unbox >> ofError >> dispatch)
                |> ignore

            [ bind ]

        /// Command to call `promise` block and map the success
        let perform (task: 'a -> Fable.Core.JS.Promise<_>) (arg: 'a) (ofSuccess: _ -> 'msg) =
            let bind dispatch =
                (task arg).``then`` (ofSuccess >> dispatch)
                |> ignore

            [ bind ]

        /// Command to call `promise` block and map the error
        let attempt (task: 'a -> Fable.Core.JS.Promise<_>) (arg: 'a) (ofError: #exn -> 'msg) : Cmd<'msg> =
            let bind dispatch =
                (task arg).catch (unbox >> ofError >> dispatch)
                |> ignore

            [ bind ]

        /// Command to dispatch the `promise` result
        let result (task: Fable.Core.JS.Promise<'msg>) =
            let bind dispatch = task.``then`` dispatch |> ignore
            [ bind ]

    [<Obsolete("Use `OfPromise.either` instead")>]
    let inline ofPromise
        (task: 'a -> Fable.Core.JS.Promise<_>)
        (arg: 'a)
        (ofSuccess: _ -> 'msg)
        (ofError: _ -> 'msg)
        : Cmd<'msg> =
        OfPromise.either task arg ofSuccess ofError
#else
    open System.Threading.Tasks

    module OfTask =
        /// Command to call a task and map the results
        let inline either (task: 'a -> Task<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsync.either (task >> Async.AwaitTask) arg ofSuccess ofError

        /// Command to call a task and map the success
        let inline perform (task: 'a -> Task<_>) (arg: 'a) (ofSuccess: _ -> 'msg) : Cmd<'msg> =
            OfAsync.perform (task >> Async.AwaitTask) arg ofSuccess

        /// Command to call a task and map the error
        let inline attempt (task: 'a -> Task<_>) (arg: 'a) (ofError: _ -> 'msg) : Cmd<'msg> =
            OfAsync.attempt (task >> Async.AwaitTask) arg ofError

        /// Command and map the task success
        let inline result (task: Task<'msg>) : Cmd<'msg> =
            OfAsync.result (task |> Async.AwaitTask)

    [<Obsolete("Use OfTask.either instead")>]
    let inline ofTask (task: 'a -> Task<_>) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg) : Cmd<'msg> =
        OfTask.either task arg ofSuccess ofError
#endif
