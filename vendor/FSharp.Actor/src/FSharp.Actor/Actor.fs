namespace FSharp.Actor

open System
open System.Threading
open FSharp.Actor
open FSharp.Actor.Types

type ActorStatus = 
    | NotStarted
    | Running
    | Stopped of string
    | Disposed
    | Faulted of exn
    | Restarting of string
    with
        member x.IsShutdownState() = 
            match x with
            | Stopped(_) -> true
            | Disposed -> true
            | _ -> false

and ActorOptions = {
    Mailbox : IMailbox<MessageEnvelope>
    Supervisor : ActorRef option
    OnStartup : (Actor -> unit) list
    OnShutdown : (Actor -> unit) list
    OnRestart : (Actor -> unit) list
    Children : ActorRef list
    Status : ActorStatus
}
with 
    static member Create(?mailbox, ?children, ?supervisor, ?startupPolicy, ?shutdownPolicy, ?restartPolicy) = 
        {
            Mailbox = defaultArg mailbox (new UnboundedInMemoryMailbox<MessageEnvelope>())
            OnStartup = defaultArg shutdownPolicy [(fun (_:Actor) -> ())]
            OnShutdown = defaultArg shutdownPolicy [(fun (_:Actor) -> ())]
            OnRestart = defaultArg restartPolicy [(fun (_:Actor) -> ())]
            Supervisor = supervisor
            Children = defaultArg children []
            Status = ActorStatus.NotStarted
        }

and Actor(path:ActorPath, computation : Actor -> Async<unit>, ?options) as self =

    let mutable cts = new CancellationTokenSource()
    let mutable options = defaultArg options (ActorOptions.Create())
    let stateChangeSync = new obj()

    let updateOptions f =
        options <- (f options)

    let rec run (actor:Actor) = 
       updateOptions (fun o -> { o with Status = ActorStatus.Running})
       async {
             try
                do! computation actor
                return shutdown actor (ActorStatus.Stopped("graceful shutdown"))
             with e ->
                do! handleError actor e
       }

    and handleError (actor:Actor) (err:exn) =
        updateOptions (fun o -> { o with Status = ActorStatus.Faulted(err) })
        async {
            match actor.Options.Supervisor with
            | Some(sup) -> sup <-- Errored(err, actor.Ref)
            | None -> 
                do Logger.Current.Error(sprintf "%A errored - shutting down" actor, Some err)
                do shutdown actor (ActorStatus.Faulted(err))
        }

    and shutdown (actor:Actor) status =
        lock stateChangeSync (fun _ ->
            cts.Cancel()
            updateOptions (fun o -> { o with Status = status })
            cts <- null
            List.iter (fun f -> f(actor)) actor.Options.OnShutdown
            )

    and start (actor:Actor) = 
        lock stateChangeSync (fun _ ->
            cts <- new CancellationTokenSource()
            Async.Start(run actor, cts.Token)
        )

    and restart (actor:Actor) reason =
        lock stateChangeSync (fun _ ->
            updateOptions (fun o -> { o with Status = ActorStatus.Restarting(reason) })
            cts.Cancel()
            cts <- null
            List.iter (fun f -> f(actor)) actor.Options.OnRestart
            start actor 
        )

    let handleSystemMessage actor = function
        | SystemMessage.Shutdown(reason) -> shutdown actor (ActorStatus.Stopped(reason))
        | SystemMessage.Restart(reason) -> restart actor reason
        | SystemMessage.Link(actorRef) -> updateOptions (fun o -> { o with Children =  actorRef :: o.Children })
        | SystemMessage.UnLink(actorRef) -> updateOptions (fun o -> { o with Children = List.filter ((<>) actorRef) o.Children })
        | SystemMessage.SetSupervisor(sup) -> 
             options <- { actor.Options with Supervisor = Some(sup) } 
             sup <-- Link(actor.Ref)
        | SystemMessage.RemoveSupervisor -> 
             match actor.Options.Supervisor with
             | Some(sup) -> 
                 options <- { actor.Options with Supervisor = None } 
                 sup <-- UnLink(actor.Ref)
             | None -> ()
    
    let post (actor:Actor) msg = 
        if not <| actor.Options.Status.IsShutdownState()
        then
            if not <| (actor.Options.Status = ActorStatus.NotStarted)
            then
               match msg.Message with
               | :? SystemMessage as sysMsg -> handleSystemMessage actor sysMsg
               | _ -> options.Mailbox.Post(msg)   
            else ()
        else raise 
                <| UnableToDeliverMessageException 
                     (sprintf "Actor (%A) cannot deliver messages invalid status %A" actor.Ref actor.Options.Status)
    

    let ref = 
        lazy new ActorRef(path, post self)
    do
        start self
    
    new(path:string, comp, ?options) =
        new Actor(ActorPath.Create(path), comp, ?options = options)

    override x.ToString() = x.Ref.ToString()
    member x.Log with get() = Logger.Current
    member x.Options with get() = options
    member x.Ref with get() = ref.Value
    member x.Receive<'a>(?timeout, ?token) = 
        async {
           let! msg = x.ReceiveEnvelope(?timeout = timeout, ?token = token)
           return msg.Message |> unbox<'a>
        }
    
    member x.ReceiveEnvelope(?timeout, ?token) = options.Mailbox.Receive(timeout, defaultArg token cts.Token)
    member x.Post(msg : MessageEnvelope) = post x msg

    interface IDisposable with
        member x.Dispose() = shutdown x (ActorStatus.Disposed)

    ///Creates an actor
    static member create(path:string,computation,?options) = 
        let actor = new Actor(path, computation, ?options = options)
        actor.Ref
        
    ///Links a collection of actors to a parent
    static member link(linkees:seq<ActorRef>,actor:ActorRef) =
        Seq.iter (fun a -> actor <-- Link(a)) linkees
        actor
    
    ///Creates an actor that is linked to a set of existing actors as it children
    static member createLinked(path, computation, linkees, ?options) =
        Actor.link(linkees,(Actor.create(path, computation, ?options = options)))
        
    ///Unlinks a set of actors from their parent.
    static member unlink(linkees, (actor:ActorRef)) =
        linkees |> Seq.iter (fun l-> actor <-- UnLink(l))
        actor

    ///Sets the supervisor for a set of actors
    static member watch((actors:seq<ActorRef>),(supervisor:ActorRef)) =
        actors |> Seq.iter (fun l-> l <-- SetSupervisor(supervisor))
        
    ///Removes the supervisor for a set of actors
    static member unwatch(actors:seq<ActorRef>) = 
        actors |> Seq.iter (fun l -> l <-- RemoveSupervisor)

type DeadLetterActor(name:string) =
    inherit Actor(name, 
                    (fun (actor:Actor) -> 
                        let rec loop() = 
                            async {
                                do! actor.ReceiveEnvelope() |> Async.Ignore
                                return! loop()
                            }
                        loop()
                    ))


