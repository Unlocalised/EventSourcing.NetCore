module ECommerce.Reactor.Program

open ECommerce.Infrastructure // Args, SourceArgs
open Serilog
open System

let [<Literal>] AppName = "ECommerce.Reactor"

module Args =

    open Argu
    open SourceArgs.Esdb

    [<NoEquality; NoComparison>]
    type Parameters =
        | [<AltCommandLine "-V"; Unique>]   Verbose
        | [<AltCommandLine "-p"; Unique>]   PrometheusPort of int
        | [<AltCommandLine "-g"; Mandatory>] Group of string
        | [<AltCommandLine "-r"; Unique>]   MaxReadAhead of int
        | [<AltCommandLine "-w"; Unique>]   MaxWriters of int
        | [<CliPrefix(CliPrefix.None); Unique(*ExactlyOnce is not supported*); Last>] Cosmos of ParseResults<SourceArgs.Cosmos.Parameters>
        | [<CliPrefix(CliPrefix.None); Unique(*ExactlyOnce is not supported*); Last>] Dynamo of ParseResults<SourceArgs.Dynamo.Parameters>
        | [<CliPrefix(CliPrefix.None); Unique(*ExactlyOnce is not supported*); Last>] Esdb of ParseResults<SourceArgs.Esdb.Parameters>
        interface IArgParserTemplate with
            member a.Usage = a |> function
                | Verbose ->                "request Verbose Logging. Default: off."
                | PrometheusPort _ ->       "port from which to expose a Prometheus /metrics endpoint. Default: off (optional if environment variable PROMETHEUS_PORT specified)"
                | Group _ ->                "Projector consumer group name."
                | MaxReadAhead _ ->         "maximum number of batches to let processing get ahead of completion. Default: 16."
                | MaxWriters _ ->           "maximum number of concurrent streams on which to process at any time. Default: 8."
                | Cosmos _ ->               "specify CosmosDB input parameters."
                | Dynamo _ ->               "specify DynamoDB input parameters."
                | Esdb _ ->                 "specify EventStoreDB input parameters."
    and Arguments(c : SourceArgs.Configuration, a : ParseResults<Parameters>) =
        member val Verbose =                a.Contains Verbose
        member val PrometheusPort =         a.TryGetResult PrometheusPort |> Option.orElseWith (fun () -> c.PrometheusPort)
        member val Group =                  a.GetResult Group
        member val MaxReadAhead =           a.GetResult(MaxReadAhead, 16)
        member val MaxConcurrentStreams =   a.GetResult(MaxWriters, 8)
        // 1ms -> 10ms reduces CPU consumption from ~5s/min to .7s/min
        member val IdleDelay =              TimeSpan.FromMilliseconds 10.
        member val StatsInterval =          TimeSpan.FromMinutes 1.
        member val StateInterval =          TimeSpan.FromMinutes 5.
        member val SourceArgs : SourceArgs.Arguments =
                                            match a.GetSubCommand() with
                                            | Cosmos a -> SourceArgs.Arguments.Cosmos(SourceArgs.Cosmos.Arguments(c, a))
                                            | Dynamo a -> SourceArgs.Arguments.Dynamo(SourceArgs.Dynamo.Arguments(c, a))
                                            | Esdb a -> SourceArgs.Arguments.Esdb(SourceArgs.Esdb.Arguments(c, a))
                                            | a -> Args.missingArg $"Unexpected Store subcommand %A{a}"
        member x.VerboseStore =             SourceArgs.verboseRequested x.SourceArgs
        member x.DumpStoreMetrics =         SourceArgs.dumpMetrics x.SourceArgs
        member val CheckpointInterval =     TimeSpan.FromHours 1.
        member val CacheSizeMb =            10
        member x.CheckpointStoreConfig(mainStore : ECommerce.Domain.Config.Store<_>) : CheckpointStore.Config =
            match mainStore with
            | ECommerce.Domain.Config.Store.Cosmos (context, cache) -> CheckpointStore.Config.Cosmos (context, cache)
            | ECommerce.Domain.Config.Store.Dynamo (context, cache) -> CheckpointStore.Config.Dynamo (context, cache)
            | ECommerce.Domain.Config.Store.Memory _ -> failwith "Unexpected"
            | ECommerce.Domain.Config.Store.Esdb (_, cache) ->
                match x.SourceArgs with
                | SourceArgs.Arguments.Esdb a ->
                    match a.TargetStoreArgs with
                    | TargetStoreArguments.Cosmos a ->
                        let context = a.Connect() |> Async.RunSynchronously |> CosmosStoreContext.create
                        CheckpointStore.Config.Cosmos (context, cache)
                    | TargetStoreArguments.Dynamo a ->
                        let context = a.Connect() |> DynamoStoreContext.create
                        CheckpointStore.Config.Dynamo (context, cache)
                 | _ -> failwith "unexpected"
        member x.CreateCheckpointStore(config) : Propulsion.Feed.IFeedCheckpointStore =
            CheckpointStore.create (x.Group, x.CheckpointInterval) ECommerce.Domain.Config.log config

    /// Parse the commandline; can throw exceptions in response to missing arguments and/or `-h`/`--help` args
    let parse tryGetConfigValue argv : Arguments =
        let programName = System.Reflection.Assembly.GetEntryAssembly().GetName().Name
        let parser = ArgumentParser.Create<Parameters>(programName=programName)
        Arguments(SourceArgs.Configuration tryGetConfigValue, parser.ParseCommandLine argv)

open Propulsion.CosmosStore.Infrastructure // AwaitKeyboardInterruptAsTaskCancelledException
open ECommerce.Domain

let buildSink (args : Args.Arguments) log (handle : FsCodec.StreamName * Propulsion.Streams.StreamSpan<byte[]> -> Async<_ * _>) =
    let stats = Reactor.Stats(log, args.StatsInterval, args.StateInterval, args.VerboseStore, logExternalStats = args.DumpStoreMetrics)
    Propulsion.Streams.StreamsProjector.Start(log, args.MaxReadAhead, args.MaxConcurrentStreams, handle, stats, args.StatsInterval, idleDelay = args.IdleDelay)

let build (args : Args.Arguments) =
    let log = Log.forGroup args.Group // needs to have a `group` tag for Propulsion.Streams Prometheus metrics
    let cache = Equinox.Cache(AppName, sizeMb = 10)
    match args.SourceArgs with
    | SourceArgs.Arguments.Cosmos a->
        let client, monitored = a.ConnectStoreAndMonitored()
        let context = client |> CosmosStoreContext.create
        let store = Config.Store.Cosmos (context, cache)
        let handle = Reactor.Config.create (store, store)
        let sink = buildSink args log handle
        let source =
            let parseFeedDoc : _ -> _ = Seq.collect Propulsion.CosmosStore.EquinoxSystemTextJsonParser.enumStreamEvents
                                        >> Seq.filter (fun {stream = s } -> Reactor.isReactionStream s)
            let observer = Propulsion.CosmosStore.CosmosStoreSource.CreateObserver(log, sink.StartIngester, parseFeedDoc)
            let leases, startFromTail, maxItems, lagFrequency = a.MonitoringParams(log)
            Propulsion.CosmosStore.CosmosStoreSource.Start(log, monitored, leases, args.Group, observer, startFromTail, ?maxItems = maxItems, lagReportFreq = lagFrequency)
        sink, source
    | SourceArgs.Arguments.Dynamo a ->
        let storeClient, streamsContext = a.Connect()
        let context = storeClient |> DynamoStoreContext.create
        let sourceId = Propulsion.DynamoStore.FeedSourceId.wellKnownId
        let store = Config.Store.Dynamo (context, cache)
        let checkpoints = args.CreateCheckpointStore(args.CheckpointStoreConfig store)
        match a.MaybeOverrideRequested() with
        | None -> ()
        | Some (trancheId, group, pos) -> Async.RunSynchronously <| async {
            let checkpointsStore : Propulsion.Feed.ReaderCheckpoint.Service = downcast checkpoints
            do! checkpointsStore.Override(sourceId, trancheId, pos) }
        let handle = Reactor.Config.create (store, store)
        let sink = buildSink args log handle
        let source =
            let indexClient, startFromTail,  maxItems, streamsDop = a.MonitoringParams(log)
            let loadMode = Propulsion.DynamoStore.LoadMode.WithBodies (Reactor.isReactionStream, streamsDop, streamsContext)
            Propulsion.DynamoStore.DynamoStoreSource(
                log, args.StatsInterval,
                indexClient, sourceId, maxItems, TimeSpan.FromSeconds 0.5,
                checkpoints, sink, loadMode, fromTail = startFromTail, storeLog = Config.log
            ).Start()
        sink, source
    | SourceArgs.Arguments.Esdb a ->
        let conn = a.Connect(log, AppName, EventStore.Client.NodePreference.Leader)
        let context = conn |> EventStoreContext.create
        let sourceId = Propulsion.EventStoreDb.FeedSourceId.wellKnownId
        let store = Config.Store.Esdb (context, cache)
        let checkpoints = args.CreateCheckpointStore(args.CheckpointStoreConfig store)
        // TODO implement checkpoint reset mechanism
        let handle = Reactor.Config.create (store, store)
        let sink = buildSink args log handle
        let source =
            let maxItems, tailSleepInterval = a.MonitoringParams(log)
            let includeBodies = true
            Propulsion.EventStoreDb.EventStoreSource(
                log, args.StatsInterval,
                conn.ReadConnection, sourceId, maxItems, tailSleepInterval,
                checkpoints, sink, includeBodies (* TODO impl , storeLog = Config.log *)
            ).Start()
        sink, source

let run (args : Args.Arguments) = async {
    use _ = args.PrometheusPort |> Option.map startMetricsServer |> Option.toObj
    let sink, source = build args
    let work = [
            Async.AwaitKeyboardInterruptAsTaskCancelledException()
            source.AwaitWithStopOnCancellation()
            sink.AwaitWithStopOnCancellation() ]
    return! Async.Parallel (build args) |> Async.Ignore<unit array> }

[<EntryPoint>]
let main argv =
    try let args = Args.parse EnvVar.tryGet argv
        let metrics = Sinks.tags AppName |> Sinks.equinoxAndPropulsionReactorMetrics
        try Log.Logger <- LoggerConfiguration().Configure(verbose=args.Verbose).Sinks(metrics, args.VerboseStore).CreateLogger()
            try run args |> Async.RunSynchronously; 0
            with e when not (e :? Args.MissingArg) -> Log.Fatal(e, "Exiting"); 2
        finally Log.CloseAndFlush()
    with Args.MissingArg msg -> eprintfn "%s" msg; 1
        | :? Argu.ArguParseException as e -> eprintfn "%s" e.Message; 1
        | e -> eprintf "Exception %s" e.Message; 1
