namespace FSharp.Actor

open System
open FSharp.Actor

type ActorSystemAlreadyConfigured() =
     inherit Exception("ActorSystem is already configured.")

type ActorSystemNotConfigured() =
    inherit Exception("An ActorSystem must be configured. Make a call to ActorSystem.configure")

type ActorSystemConfiguration = {
    Register : IRegister
    Transports : seq<ITransport>
    Dispatcher : IDispatcher
    Supervisor : ActorRef
}
with
    static member Create(?transports, ?supervisorStrategy, ?register, ?dispatch) = 
        let strategy = defaultArg supervisorStrategy SupervisorStrategy.OneForOne
        {
            Register = defaultArg register (new TrieBasedRegistry())
            Transports = defaultArg transports []
            Dispatcher = defaultArg dispatch (new DisruptorBasedDispatcher())
            Supervisor = (new Supervisor(ActorPath.Create("ActorSystem/supervisor"), Supervisors.upNfailsPerActor 3 strategy)).Ref
        } 
    member x.Dispose() = 
         x.Register.Dispose()
         x.Dispatcher.Dispose()
         x.Supervisor <-- Shutdown("ActorSystem disposed")

type ActorSystem() =

    static let mutable config = None

    static let execute f = 
        match config with
        | Some(n) -> f n
        | _ -> raise(ActorSystemNotConfigured())

    static member configure(?configuration:ActorSystemConfiguration) =
        let configuration = defaultArg configuration (ActorSystemConfiguration.Create())
        match config with
        | Some(sys) -> 
            //raise an exception as it is hard to reason about the impacts of
            //reconfiguring actors in terms of Async Dispatch
            raise(ActorSystemAlreadyConfigured())
        | None ->
            configuration.Dispatcher.Configure(configuration.Register, configuration.Transports, configuration.Supervisor)
            config <- Some(configuration)
            Logger.Current.Debug("Created ActorSystem", None)

    static member resolve(path) =
        execute (fun sys -> sys.Register.Resolve(path))
      
    static member post path msg = 
        execute (fun sys -> sys.Dispatcher.Post(MessageEnvelope.Create(msg, path)))
    
    static member broadcast paths msg = 
        paths |> List.iter (fun (path:string) -> ActorSystem.post (ActorPath.Create(path)) msg)

    static member actorOf(name:string, computation, ?options) =
        execute (fun sys -> 
                        let actor = (new Actor(ActorPath.Create(name), computation, ?options = options)).Ref
                        sys.Register.Register actor
                        actor
                        )

[<AutoOpen>]
module Operators =

    let (!!) (path:ActorPath) = ActorSystem.resolve path
    let (?<--) (path:string) msg = ActorSystem.post (ActorPath.Create(path)) msg
    let (?<-*) (refs:seq<string>) (msg:'a) = ActorSystem.broadcast (refs  |> Seq.toList) msg
    let (<-*) (refs:seq<ActorRef>) (msg:'a) = refs |> Seq.iter (fun r -> r.Post(MessageEnvelope.Create(msg, r.Path)))

