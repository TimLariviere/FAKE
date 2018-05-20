﻿namespace Fake.Core

open System
open System.Collections.Generic
open Fake.Core
open System.Threading.Tasks
open System.Threading
module internal TargetCli =
    let targetCli =
        """
Usage:
  fake-run --list
  fake-run --version
  fake-run --help | -h
  fake-run [target_opts] [target <target>] [--] [<targetargs>...]

Target Module Options [target_opts]:
    -t, --target <target>
                          Run the given target (ignored if positional argument 'target' is given)
    -e, --environment-variable <keyval> [*]
                          Set an environment variable. Use 'key=val'. Consider using regular arguments, see https://fake.build/core-targets.html 
    -s, --single-target    Run only the specified target.
    -p, --parallel <num>  Run parallel with the given number of tasks.
        """
    let doc = Docopt(targetCli)
    let parseArgs args = doc.Parse args
/// [omit]
type TargetDescription = string

[<NoComparison>]
[<NoEquality>]
type TargetResult =
    { Error : exn option; Time : TimeSpan; Target : Target; WasSkipped : bool }

and [<NoComparison>] [<NoEquality>] TargetContext =
    { PreviousTargets : TargetResult list
      AllExecutingTargets : Target list
      FinalTarget : string
      Arguments : string list
      IsRunningFinalTargets : bool
      CancellationToken : CancellationToken }
    static member Create ft all args isRunningFinalTargets (token:CancellationToken)= { 
        FinalTarget = ft
        AllExecutingTargets = all
        PreviousTargets = []
        Arguments = args
        IsRunningFinalTargets = isRunningFinalTargets
        CancellationToken = token}
    member x.HasError =
        x.PreviousTargets
        |> List.exists (fun t -> t.Error.IsSome)
    member x.TryFindPrevious name =
        x.PreviousTargets |> List.tryFind (fun t -> t.Target.Name = name)      
    member x.TryFindTarget name =
        x.AllExecutingTargets |> List.tryFind (fun t -> t.Name = name)        

and [<NoComparison>] [<NoEquality>] TargetParameter =
    { TargetInfo : Target
      Context : TargetContext }

/// [omit]
and [<NoComparison>] [<NoEquality>] Target =
    { Name: string;
      Dependencies: string list;
      SoftDependencies: string list;
      Description: TargetDescription option;
      Function : TargetParameter -> unit}

/// Exception for request errors
#if !NETSTANDARD1_6
[<System.Serializable>]
#endif
type BuildFailedException =
    val private info : TargetContext option
    inherit Exception
    new (msg:string, inner:exn) = {
      inherit Exception(msg, inner)
      info = None }
    new (info:TargetContext, msg:string, inner:exn) = {
      inherit Exception(msg, inner)
      info = Some info }
#if !NETSTANDARD1_6
    new (info:System.Runtime.Serialization.SerializationInfo, context:System.Runtime.Serialization.StreamingContext) = {
      inherit Exception(info, context)
      info = None
    }
#endif
    member x.Info with get () = x.info
    member x.Wrap() =
        match x.info with
        | Some info ->
            BuildFailedException(info, x.Message, x:>exn)
        | None ->
            BuildFailedException(x.Message, x:>exn)

module Target =

    type private DependencyType =
        | Hard = 1
        | Soft = 2

    /// [omit]
    //let mutable PrintStackTraceOnError = false
    let private printStackTraceOnErrorVar = "Fake.Core.Target.PrintStackTraceOnError"
    let private getPrintStackTraceOnError, _, (setPrintStackTraceOnError:bool -> unit) = 
        Fake.Core.Context.fakeVar printStackTraceOnErrorVar
    
    /// [omit]
    //let mutable LastDescription = null
    let private lastDescriptionVar = "Fake.Core.Target.LastDescription"
    let private getLastDescription, removeLastDescription, setLastDescription = 
        Fake.Core.Context.fakeVar lastDescriptionVar

    /// Sets the Description for the next target.
    /// [omit]
    let Description text =
        match getLastDescription() with
        | Some (v:string) ->
            failwithf "You can't set the description for a target twice. There is already a description: %A" v
        | None ->
           setLastDescription text

    /// TargetDictionary
    /// [omit]
    let internal getVarWithInit name f =
        let varName = sprintf "Fake.Core.Target.%s" name
        let getVar, _, setVar = 
            Fake.Core.Context.fakeVar varName
        fun () ->
            match getVar() with
            | Some d -> d
            | None ->
                let d = f () // new Dictionary<_,_>(StringComparer.OrdinalIgnoreCase)
                setVar d
                d
            
    let internal getTargetDict =
        getVarWithInit "TargetDict" (fun () -> new Dictionary<_,_>(StringComparer.OrdinalIgnoreCase))

    /// Final Targets - stores final targets and if they are activated.
    let internal getFinalTargets =
        getVarWithInit "FinalTargets" (fun () -> new Dictionary<_,_>(StringComparer.OrdinalIgnoreCase))

    /// BuildFailureTargets - stores build failure targets and if they are activated.
    let internal getBuildFailureTargets =
        getVarWithInit "BuildFailureTargets" (fun () -> new Dictionary<_,_>(StringComparer.OrdinalIgnoreCase))


    /// Resets the state so that a deployment can be invoked multiple times
    /// [omit]
    let internal reset() =
        getTargetDict().Clear()
        getBuildFailureTargets().Clear()
        getFinalTargets().Clear()

    /// Returns a list with all target names.
    let internal getAllTargetsNames() = getTargetDict() |> Seq.map (fun t -> t.Key) |> Seq.toList

    /// Gets a target with the given name from the target dictionary.
    let get name =
        let d = getTargetDict()
        match d.TryGetValue (name) with
        | true, target -> target
        | _  ->
            Trace.traceError <| sprintf "Target \"%s\" is not defined. Existing targets:" name
            for target in d do
                Trace.traceError  <| sprintf "  - %s" target.Value.Name
            failwithf "Target \"%s\" is not defined." name
    
    let internal runSimpleInternal context target=
        let watch = System.Diagnostics.Stopwatch.StartNew()
        let error =
            try
                if not context.IsRunningFinalTargets then
                    context.CancellationToken.ThrowIfCancellationRequested()|>ignore
                target.Function { TargetInfo = target; Context = context }
                None
            with e -> Some e
        watch.Stop()
        { Error = error; Time = watch.Elapsed; Target = target; WasSkipped = false }
    let internal runSimpleContextInternal target context =
        let result = runSimpleInternal context target
        { context with PreviousTargets = context.PreviousTargets @ [result] }


    /// This simply runs the function of a target without doing anything (like tracing, stopwatching or adding it to the results at the end)
    let runSimple name args =
        let target = get name
        target
        |> runSimpleInternal (TargetContext.Create name [target] args false CancellationToken.None)
    
    /// This simply runs the function of a target without doing anything (like tracing, stopwatching or adding it to the results at the end)
    let runSimpleWithContext name ctx =
        let target = get name
        target
        |> runSimpleInternal ctx
        
    /// Returns the DependencyString for the given target.
    let internal dependencyString target =
        if target.Dependencies.IsEmpty then String.Empty else
        target.Dependencies
          |> Seq.map (fun d -> (get d).Name)
          |> String.separated ", "
          |> sprintf "(==> %s)"

    /// Returns the soft  DependencyString for the given target.
    let internal softDependencyString target =
        if target.SoftDependencies.IsEmpty then String.Empty else
        target.SoftDependencies
          |> Seq.map (fun d -> (get d).Name)
          |> String.separated ", "
          |> sprintf "(?=> %s)"

    /// Do nothing - Can be used to define empty targets.
    let DoNothing = (fun (_:TargetParameter) -> ())

    /// Checks whether the dependency (soft or normal) can be added.
    /// [omit]
    let internal checkIfDependencyCanBeAddedCore fGetDependencies targetName dependentTargetName =
        let target = get targetName
        let dependentTarget = get dependentTargetName

        let rec checkDependencies dependentTarget =
              fGetDependencies dependentTarget
              |> List.iter (fun dep ->
                   if String.toLower dep = String.toLower targetName then
                      failwithf "Cyclic dependency between %s and %s" targetName dependentTarget.Name
                   checkDependencies (get dep))

        checkDependencies dependentTarget
        target,dependentTarget

    /// Checks whether the dependency can be added.
    /// [omit]
    let internal checkIfDependencyCanBeAdded targetName dependentTargetName =
       checkIfDependencyCanBeAddedCore (fun target -> target.Dependencies) targetName dependentTargetName

    /// Checks whether the soft dependency can be added.
    /// [omit]
    let internal checkIfSoftDependencyCanBeAdded targetName dependentTargetName =
       checkIfDependencyCanBeAddedCore (fun target -> target.SoftDependencies) targetName dependentTargetName

    /// Adds the dependency to the front of the list of dependencies.
    /// [omit]
    let internal dependencyAtFront targetName dependentTargetName =
        let target,_ = checkIfDependencyCanBeAdded targetName dependentTargetName

        getTargetDict().[targetName] <- { target with Dependencies = dependentTargetName :: target.Dependencies }

    /// Appends the dependency to the list of dependencies.
    /// [omit]
    let internal dependencyAtEnd targetName dependentTargetName =
        let target,_ = checkIfDependencyCanBeAdded targetName dependentTargetName

        getTargetDict().[targetName] <- { target with Dependencies = target.Dependencies @ [dependentTargetName] }


    /// Appends the dependency to the list of soft dependencies.
    /// [omit]
    let internal softDependencyAtEnd targetName dependentTargetName =
        let target,_ = checkIfDependencyCanBeAdded targetName dependentTargetName

        getTargetDict().[targetName] <- { target with SoftDependencies = target.SoftDependencies @ [dependentTargetName] }

    /// Adds the dependency to the list of dependencies.
    /// [omit]
    let internal dependency targetName dependentTargetName = dependencyAtEnd targetName dependentTargetName

    /// Adds the dependency to the list of soft dependencies.
    /// [omit]
    let internal softDependency targetName dependentTargetName = softDependencyAtEnd targetName dependentTargetName

    /// Adds the dependencies to the list of dependencies.
    /// [omit]
    let internal Dependencies targetName dependentTargetNames = dependentTargetNames |> List.iter (dependency targetName)

    /// Adds the dependencies to the list of soft dependencies.
    /// [omit]
    let internal SoftDependencies targetName dependentTargetNames = dependentTargetNames |> List.iter (softDependency targetName)

    /// Backwards dependencies operator - x is dependent on ys.
    let inline internal (<==) x ys = Dependencies x ys

    /// Creates a target from template.
    /// [omit]
    let internal addTarget target name =
        getTargetDict().Add(name, target)
        name <== target.Dependencies
        removeLastDescription()
        
    /// add a target with dependencies
    /// [omit]
    let internal addTargetWithDependencies dependencies body name =
        let template =
            { Name = name
              Dependencies = dependencies
              SoftDependencies = []
              Description = getLastDescription()
              Function = body }
        addTarget template name

    /// Creates a Target.
    let create name body = addTargetWithDependencies [] body name

    /// Runs all activated final targets (in alphabetically order).
    /// [omit]
    let internal runFinalTargets context =
        getFinalTargets()
          |> Seq.filter (fun kv -> kv.Value)     // only if activated
          |> Seq.map (fun kv -> kv.Key)
          |> Seq.fold (fun context name ->
               Trace.tracefn "Starting FinalTarget: %s" name
               let target = get name
               runSimpleContextInternal target context) context  

    /// Runs all build failure targets.
    /// [omit]
    let internal runBuildFailureTargets (context) =
        getBuildFailureTargets()
          |> Seq.filter (fun kv -> kv.Value)     // only if activated
          |> Seq.map (fun kv -> kv.Key)
          |> Seq.fold (fun context name ->
               Trace.tracefn "Starting BuildFailureTarget: %s" name
               let target = get name
               runSimpleContextInternal target context) context

    /// List all targets available.
    let listAvailable() =
        Trace.log "The following targets are available:"
        for t in getTargetDict().Values do
            Trace.logfn "   %s%s" t.Name (match t.Description with Some s -> sprintf " - %s" s | _ -> "")


    // Maps the specified dependency type into the list of targets
    let private withDependencyType (depType:DependencyType) targets =
        targets |> List.map (fun t -> depType, t)

    // Helper function for visiting targets in a dependency tree. Returns a set containing the names of the all the
    // visited targets, and a list containing the targets visited ordered such that dependencies of a target appear earlier
    // in the list than the target.
    let private visitDependencies fVisit targetName =
        let visit fGetDependencies fVisit targetName =
            let visited = new HashSet<_>()
            let ordered = new List<_>()
            let rec visitDependenciesAux level (depType,targetName) =
                let target = get targetName
                let isVisited = visited.Contains targetName
                visited.Add targetName |> ignore
                fVisit (target, depType, level, isVisited)
                (fGetDependencies target) |> Seq.iter (visitDependenciesAux (level + 1))
                if not isVisited then ordered.Add targetName
            visitDependenciesAux 0 (DependencyType.Hard, targetName)
            visited, ordered

        // First pass is to accumulate targets in (hard) dependency graph
        let visited, _ = visit (fun t -> t.Dependencies |> withDependencyType DependencyType.Hard) ignore targetName

        let getAllDependencies (t: Target) =
             (t.Dependencies |> withDependencyType DependencyType.Hard) @
             // Note that we only include the soft dependency if it is present in the set of targets that were
             // visited.
             (t.SoftDependencies |> List.filter visited.Contains |> withDependencyType DependencyType.Soft)

        // Now make second pass, adding in soft depencencies if appropriate
        visit getAllDependencies fVisit targetName

    /// <summary>Writes a dependency graph.</summary>
    /// <param name="verbose">Whether to print verbose output or not.</param>
    /// <param name="target">The target for which the dependencies should be printed.</param>
    let printDependencyGraph verbose target =
        match getTargetDict().TryGetValue (target) with
        | false,_ -> listAvailable()
        | true,target ->
            let sb = System.Text.StringBuilder()
            let appendfn fmt = Printf.ksprintf (sb.AppendLine >> ignore) fmt

            appendfn "%sDependencyGraph for Target %s:" (if verbose then String.Empty else "Shortened ") target.Name
            let logDependency ((t: Target), depType, level, isVisited) =
                if verbose ||  not isVisited then
                    let indent = (String(' ', level * 3))
                    if depType = DependencyType.Soft then
                        appendfn "%s<=? %s" indent t.Name
                    else
                        appendfn "%s<== %s" indent t.Name

            let _ = visitDependencies logDependency target.Name
            //appendfn ""
            //sb.Length <- sb.Length - Environment.NewLine.Length
            Trace.log <| sb.ToString()

    let internal printRunningOrder (targetOrder:Target[] list) =
        let sb = System.Text.StringBuilder()
        let appendfn fmt = Printf.ksprintf (sb.AppendLine >> ignore) fmt
        appendfn "The running order is:"
        targetOrder
        |> List.iteri (fun index x ->
                                //if (environVarOrDefault "parallel-jobs" "1" |> int > 1) then
                                appendfn "Group - %d" (index + 1)
                                Seq.iter (appendfn "  - %s") (x|>Seq.map (fun t -> t.Name)))

        sb.Length <- sb.Length - Environment.NewLine.Length
        Trace.log <| sb.ToString()

    /// <summary>Writes a build time report.</summary>
    /// <param name="total">The total runtime.</param>
    let internal writeTaskTimeSummary total context =
        Trace.traceHeader "Build Time Report"
        let executedTargets = context.PreviousTargets        
        if executedTargets.Length > 0 then
            let width =
                executedTargets
                  |> Seq.map (fun (tres) -> tres.Target.Name.Length)
                  |> Seq.max
                  |> max 8

            let alignedString (name:string) (duration) extra =
                let durString = sprintf "%O" duration
                if (String.IsNullOrEmpty extra) then
                    sprintf "%s   %s" (name.PadRight width) durString
                else sprintf "%s   %s   (%s)" (name.PadRight width) (durString.PadRight "00:00:00.0000824".Length) extra
            let aligned (name:string) duration extra = alignedString name duration extra |> Trace.trace
            let alignedWarn (name:string) duration extra = alignedString name duration extra |> Trace.traceFAKE "%s"
            let alignedError (name:string) duration extra = alignedString name duration extra |> Trace.traceError

            aligned "Target" "Duration" null
            aligned "------" "--------" null
            executedTargets
              |> Seq.iter (fun (tres) ->
                    let name = tres.Target.Name
                    let time = tres.Time
                    match tres.Error with
                    | None when tres.WasSkipped -> alignedWarn name time "skipped" // Yellow
                    | None -> aligned name time null
                    | Some e -> alignedError name time e.Message)

            aligned "Total:" total null
            if not context.HasError then aligned "Status:" "Ok" null
            else
                alignedError "Status:" "Failure" null
        else
            Trace.traceError "No target was successfully completed"

        Trace.traceLine()


    /// Determines a parallel build order for the given set of targets
    let internal determineBuildOrder (target : string) =
        let _ = get target

        let rec visitDependenciesAux fGetDependencies (visited:string list) level (_depType, targetName) =
            let target = get targetName
            let isVisited = visited |> Seq.contains targetName
            //fVisit (target, depType, level, isVisited)
            let dependencies =
                fGetDependencies target
                |> Seq.collect (visitDependenciesAux fGetDependencies (targetName::visited) (level + 1))
                |> Seq.distinctBy (fun t -> t.Name)
                |> Seq.toList
            if not isVisited then target :: dependencies
            else dependencies

        // first find the list of targets we "have" to build
        let targets = visitDependenciesAux (fun t -> t.Dependencies |> withDependencyType DependencyType.Hard) [] 0 (DependencyType.Hard, target)
        
        // Try to build the optimal tree by starting with the targets without dependencies and remove them from the list iteratively
        let rec findOrder (targetLeft:Target list) =
            let isValidTarget name = targetLeft |> Seq.exists (fun t -> t.Name = name)
            let canBeExecuted (t:Target) =
                t.Dependencies @ t.SoftDependencies
                |> Seq.filter isValidTarget
                |> Seq.isEmpty
            let map =
                targetLeft
                    |> Seq.groupBy canBeExecuted
                    |> Seq.map (fun (t, g) -> t, Seq.toList g)
                    |> dict
            let execute, left =
                (match map.TryGetValue true with
                | true, ts -> ts
                | _ -> []),
                match map.TryGetValue false with
                | true, ts -> ts
                | _ -> []
            if List.isEmpty execute then failwithf "Could not progress build order in %A" targetLeft
            List.toArray execute :: if List.isEmpty left then [] else findOrder left
        findOrder targets

    /// Runs a single target without its dependencies... only when no error has been detected yet.
    let internal runSingleTarget (target : Target) (context:TargetContext) =
        if not context.HasError then
            use t = Trace.traceTarget target.Name (match target.Description with Some d -> d | _ -> "NoDescription") (dependencyString target)
            let res = runSimpleContextInternal target context
            if res.HasError
            then t.MarkFailed()
            else t.MarkSuccess()
            res
        else
            { context with PreviousTargets = context.PreviousTargets @ [{ Error = None; Time = TimeSpan.Zero; Target = target; WasSkipped = true }] }

    module internal ParallelRunner =
        let internal mergeContext (ctx1:TargetContext) (ctx2:TargetContext) =
            let known =
                ctx1.PreviousTargets
                |> Seq.map (fun tres -> tres.Target.Name, tres)
                |> dict
            let filterKnown targets =
                targets
                |> List.filter (fun tres -> not (known.ContainsKey tres.Target.Name))
            { ctx1 with
                PreviousTargets =
                    ctx1.PreviousTargets @ filterKnown ctx2.PreviousTargets
            }
  
        // Centralized handling of target context and next target logic...
        [<NoComparison>] 
        [<NoEquality>]      
        type RunnerHelper =
            | GetNextTarget of TargetContext * AsyncReplyChannel<TargetContext * Async<Target option>>
        type IRunnerHelper =
            abstract GetNextTarget : TargetContext -> Async<TargetContext * Async<Target option>>
        let createCtxMgr (order:Target[] list) (ctx:TargetContext) =
            let body (inbox:MailboxProcessor<RunnerHelper>) = async {
                let targetCount =
                    order |> Seq.sumBy (fun t -> t.Length)
                let resolution = Set.ofSeq(order |> Seq.concat |> Seq.map (fun t -> t.Name))
                let inResolution (t:string) = resolution.Contains t               
                let mutable ctx = ctx
                let mutable waitList = []
                let mutable runningTasks = []
                //let mutable remainingOrders = order
                try
                    while true do
                        let! msg = inbox.Receive()
                        match msg with
                        | GetNextTarget (newCtx, reply) ->
                            // semantic is:
                            // - We never return a target twice!
                            // - we fill up the waitlist first
                            ctx <- mergeContext ctx newCtx
                            let known =
                                ctx.PreviousTargets
                                |> Seq.map (fun tres -> tres.Target.Name, tres)
                                |> dict
                            runningTasks <-
                                runningTasks
                                |> List.filter (fun t -> not(known.ContainsKey t.Name))
                            if known.Count = targetCount then
                                for (w:System.Threading.Tasks.TaskCompletionSource<Target option>) in waitList do
                                    w.SetResult None
                                waitList <- []
                                reply.Reply (ctx, async.Return None)
                            else

                                let isRunnable (t:Target) =
                                    not (known.ContainsKey t.Name) && // not already finised
                                    not (runningTasks |> Seq.exists (fun r -> r.Name = t.Name)) && // not already running
                                    t.Dependencies @ List.filter inResolution t.SoftDependencies // all dependencies finished
                                    |> Seq.forall (fun d -> known.ContainsKey d)
                                let runnable =
                                    order
                                    |> Seq.concat
                                    |> Seq.filter isRunnable
                                    |> Seq.toList

                                let rec getNextFreeRunableTarget (r) =
                                    match r with
                                    | t :: rest ->
                                        match waitList with
                                        | h :: restwait ->
                                            // fill some idle worker
                                            runningTasks <- t :: runningTasks
                                            h.SetResult (Some t)
                                            waitList <- restwait
                                            getNextFreeRunableTarget rest
                                        | [] -> Some t
                                    | [] -> None
                                match getNextFreeRunableTarget runnable with
                                | Some free ->
                                    runningTasks <- free :: runningTasks
                                    reply.Reply (ctx, async.Return(Some free))
                                | None ->
                                    // queue work
                                    let tcs = new TaskCompletionSource<Target option>()
                                    waitList <- waitList @ [ tcs ]
                                    reply.Reply (ctx, tcs.Task |> Async.AwaitTask)
                with e ->
                    while true do
                        let! msg = inbox.Receive()
                        match msg with
                        | GetNextTarget (_, reply) ->
                            reply.Reply (ctx, async { return raise <| exn("mailbox failed", e) })
            }

            let mbox = MailboxProcessor.Start(body)
            { new IRunnerHelper with
                member __.GetNextTarget (ctx) = mbox.PostAndAsyncReply(fun reply -> GetNextTarget(ctx, reply))
            }

        let runOptimal workerNum (order:Target[] list) targetContext=
            let mgr = createCtxMgr order targetContext
            let targetRunner () =
                async {
                    let token = targetContext.CancellationToken
                    let! (tctx, att) = mgr.GetNextTarget(targetContext)
                    let! tt = att
                    let mutable ctx = tctx
                    let mutable nextTarget = tt
                    while nextTarget.IsSome && not token.IsCancellationRequested do
                        let newCtx = runSingleTarget nextTarget.Value ctx
                        let! (tctx, att) = mgr.GetNextTarget(newCtx)
                        let! tt = att
                        ctx <- tctx
                        nextTarget <- tt
                    return ctx
                } |> Async.StartAsTask
            Array.init workerNum (fun _ -> targetRunner())
            |> Task.WhenAll
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Seq.reduce mergeContext
    module internal Observable =
        let subscribeOnce callback (observable:IObservable<'T>) =
            let removeObj : IDisposable option ref = ref None
            let removeLock = new obj()
            let setRemover r = 
                lock removeLock (fun () -> removeObj := Some r)
                r
            let remove() = 
                lock removeLock (fun () -> 
                    match removeObj.Value with
                    | Some d -> 
                        removeObj := None
                        d.Dispose()
                    | None -> ())
            let observer = 
                { new IObserver<'T> with
                    member __.OnNext(v) =
                        remove()
                        callback v
                    member __.OnError(_) =
                        remove()          
                    member __.OnCompleted()=                           
                        remove()
                }
            observable.Subscribe observer 
            |> setRemover
    /// Runs the given array of targets in parallel using count tasks
    let internal runTargetsParallel (_count : int) (targets : Target[]) context =
        let known =
            context.PreviousTargets
            |> Seq.map (fun tres -> tres.Target.Name, tres)
            |> dict
        let filterKnown targets =
            targets
            |> List.filter (fun tres -> not (known.ContainsKey tres.Target.Name))
        targets
        |> Array.map (fun t -> async { return runSingleTarget t context })
        |> Async.Parallel
        |> Async.RunSynchronously
        |> Seq.reduce (fun ctx1 ctx2 ->
            { ctx1 with
                PreviousTargets = 
                    context.PreviousTargets @ filterKnown ctx1.PreviousTargets @ filterKnown ctx2.PreviousTargets
             })
    
    let private handleUserCancelEvent (cts:CancellationTokenSource) (e:ConsoleCancelEventArgs)=
        e.Cancel <- true
        printfn "Gracefully shutting down.."
        printfn "Press ctrl+c again to force quit"
        let __ = Console.CancelKeyPress |> Observable.subscribeOnce (fun _ -> 
            Environment.Exit 1)
        Process.killAllCreatedProcesses()|>ignore
        cts.Cancel()
        
    /// Runs a target and its dependencies.
    let internal runInternal singleTarget parallelJobs targetName args =
        match getLastDescription() with
        | Some d -> failwithf "You set a task description (%A) but didn't specify a task. Make sure to set the Description above the Target." d
        | None -> ()

        printfn "run %s" targetName
        let watch = new System.Diagnostics.Stopwatch()
        watch.Start()
        
        Trace.tracefn "Building project with version: %s" BuildServer.buildVersion
        printDependencyGraph false targetName

        // determine a build order
        let order = determineBuildOrder targetName
        printRunningOrder order
        if singleTarget
        then Trace.traceImportant "Single target mode ==> Skipping dependencies."
        let allTargets = List.collect Seq.toList order
        use cts = new CancellationTokenSource()    
        let context = TargetContext.Create targetName allTargets args false cts.Token
          
        let context =
            let captureContext (f:'a->unit) = 
                let ctx = Context.getExecutionContext()
                (fun a -> 
                    let nctx = Context.getExecutionContext()
                    if ctx <> nctx then Context.setExecutionContext ctx
                    f a)
                      
            let cancelHandler  = captureContext (handleUserCancelEvent cts)            
            use __ = Console.CancelKeyPress |> Observable.subscribeOnce cancelHandler
            
            let context =
                // Figure out the order in in which targets can be run, and which can be run in parallel.
                if parallelJobs > 1 && not singleTarget then
                    Trace.tracefn "Running parallel build with %d workers" parallelJobs

                    // always try to keep "parallelJobs" runners busy
                    ParallelRunner.runOptimal parallelJobs order context
                    //order
                    //    |> Seq.fold (fun context par -> runTargetsParallel parallelJobs par context) context
                else
                    let targets = order |> Seq.collect id |> Seq.toArray
                    let lastTarget = targets |> Array.last
                    if singleTarget then
                        runSingleTarget lastTarget context
                    else
                        targets |> Array.fold (fun context target -> runSingleTarget target context) context
            
            if context.HasError && not context.CancellationToken.IsCancellationRequested then
                    runBuildFailureTargets context
            else context       
                  
        let context = runFinalTargets {context with IsRunningFinalTargets=true}
        writeTaskTimeSummary watch.Elapsed context
        if context.HasError && not context.CancellationToken.IsCancellationRequested then
            let errorTargets =
                context.PreviousTargets
                |> List.choose (fun tres ->
                    match tres.Error with
                    | Some er -> Some (er, tres.Target)
                    | None -> None)
            let targets = errorTargets |> Seq.map (fun (_er, target) -> target.Name) |> Seq.distinct
            let targetStr = String.Join(", ", targets)
            let errorMsg =
                if errorTargets.Length = 1 then
                    sprintf "Target '%s' failed." targetStr
                else
                    sprintf "Targets '%s' failed." targetStr          
            let inner = AggregateException(AggregateException().Message, errorTargets |> Seq.map fst)
            BuildFailedException(context, errorMsg, inner)                
            |> raise

        context

    /// Creates a target in case of build failure (not activated).
    let createBuildFailure name body =
        create name body
        getBuildFailureTargets().Add(name,false)

    /// Activates the build failure target.
    let activateBuildFailure name =
        let _ = get name // test if target is defined
        getBuildFailureTargets().[name] <- true

    /// Creates a final target (not activated).
    let createFinal name body =
        create name body
        getFinalTargets().Add(name,false)

    /// Activates the final target.
    let activateFinal name =
        let _ = get name // test if target is defined
        getFinalTargets().[name] <- true

    /// Runs a target and its dependencies, used for testing - usually not called in scripts.
    let runAndGetContext parallelJobs targetName args = runInternal false parallelJobs targetName args

    /// Runs a target and its dependencies
    let run parallelJobs targetName args = runInternal false parallelJobs targetName args |> ignore

    let internal runWithDefault allowArgs fDefault =
        let ctx = Fake.Core.Context.forceFakeContext ()
        let trySplitEnvArg (arg:string) =
            let idx = arg.IndexOf('=')
            if idx < 0 then
                Trace.traceError (sprintf "Argument for -e should contain '=' but was '%s', the argument will be ignored." arg)
                None
            else            
                Some (arg.Substring(0, idx), arg.Substring(idx + 1))
        let results =
            try 
                let res = TargetCli.parseArgs (ctx.Arguments |> List.toArray)
                res |> Choice1Of2
            with :? DocoptException as e -> Choice2Of2 e
        match results with
        | Choice1Of2 results ->
            let envs =
                match DocoptResult.tryGetArguments "--environment-variable" results with
                | Some args ->
                    args |> List.choose trySplitEnvArg
                | None -> []
            for (key, value) in envs do Environment.setEnvironVar key value

            if DocoptResult.hasFlag "--list" results then
                listAvailable()
            elif DocoptResult.hasFlag "-h" results || DocoptResult.hasFlag "--help" results then
                printfn "%s" TargetCli.targetCli
                printfn "Hint: Run 'fake run <build.fsx> target <target> --help' to get help from your target."
            elif DocoptResult.hasFlag "--version" results then
                printfn "Target Module Version: %s" AssemblyVersionInformation.AssemblyInformationalVersion
            else
                let target =
                    match DocoptResult.tryGetArgument "<target>" results with
                    | None ->
                        match DocoptResult.tryGetArgument "--target" results with
                        | None -> Environment.environVarOrNone "target"
                        | Some arg -> Some arg
                    | Some arg ->
                        match DocoptResult.tryGetArgument "--target" results with
                        | None -> ()
                        | Some innerArg ->
                            Trace.traceImportant
                                <| sprintf "--target '%s' is ignored when 'target %s' is given" innerArg arg
                        Some arg
                let parallelJobs =
                    match DocoptResult.tryGetArgument "--parallel" results with
                    | Some arg ->
                        match System.Int32.TryParse(arg) with
                        | true, i -> i
                        | _ -> failwithf "--parallel needs an integer argument, could not parse '%s'" arg
                    | None ->
                        Environment.environVarOrDefault "parallel-jobs" "1" |> int
                let singleTarget =
                    match DocoptResult.hasFlag "--single-target" results with
                    | true -> true
                    | false -> Environment.hasEnvironVar "single-target"
                let arguments =
                    match DocoptResult.tryGetArguments "<targetargs>" results with
                    | Some args -> args
                    | None -> []
                if not allowArgs && arguments <> [] then
                    failwithf "The following arguments could not be parsed: %A\nTo forward arguments to your targets you need to use \nTarget.runOrDefaultWithArguments instead of Target.runOrDefault" arguments
                match target with
                | Some t -> runInternal singleTarget parallelJobs t arguments |> ignore
                | None -> fDefault singleTarget parallelJobs arguments
        | Choice2Of2 e ->
            // To ensure exit code.
            raise <| exn (sprintf "Usage error: %s\n%s" e.Message TargetCli.targetCli, e)

    /// Runs the command given on the command line or the given target when no target is given
    let runOrDefault defaultTarget =
        runWithDefault false (fun singleTarget parallelJobs arguments ->
            runInternal singleTarget parallelJobs defaultTarget arguments |> ignore)

    /// Runs the command given on the command line or the given target when no target is given
    let runOrDefaultWithArguments defaultTarget =
        runWithDefault true (fun singleTarget parallelJobs arguments ->
            runInternal singleTarget parallelJobs defaultTarget arguments |> ignore)

    /// Runs the target given by the target parameter or lists the available targets
    let runOrList() =
        runWithDefault false (fun _ _ _ -> listAvailable())
