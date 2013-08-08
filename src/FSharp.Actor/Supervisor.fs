namespace FSharp.Actor

open System
open System.Threading
open FSharp.Actor
open FSharp.Actor.Types

module SupervisorStrategy =

    let Forward (target:ActorRef) = 
        (fun (reciever:Actor) err (originator:ActorRef)  -> 
           target <-- Errored(err, originator)
        )

    let AlwaysFail = 
        (fun (reciever:Actor) err (originator:ActorRef)  -> 
            originator <-- Shutdown("Supervisor:AlwaysFail")
        )

    let FailAll = 
        (fun (reciever:Actor) err (originator:ActorRef)  -> 
            reciever.Options.Children 
            |> List.iter ((-->) (Shutdown("Supervisor:FailAll")))
        )

    let OneForOne = 
       (fun (reciever:Actor) err (originator:ActorRef) -> 
            originator <-- Restart("Supervisor:OneForOne")
       )
       
    let OneForAll = 
       (fun (reciever:Actor) err (originator:ActorRef)  -> 
            reciever.Options.Children 
            |> List.iter ((-->) (Restart("Supervisor:OneForAll")))
       )

module Supervisors = 
    
    let upNfailsPerActor maxFailures strategy (children: seq<ActorRef>) (actor:Actor) =  
        let rec supervisorLoop (restarts:Map<ActorPath,int>) = 
            async {

                let! msg = actor.Receive()
                match msg with
                | Errored(err, targetActor) ->
                    match restarts.TryFind(targetActor.Path) with
                    | Some(count) when count < maxFailures -> 
                        strategy actor err targetActor                            
                        return! supervisorLoop (Map.add targetActor.Path (count + 1) restarts)
                    | Some(count) -> 
                        targetActor <-- SystemMessage.Shutdown("Too many restarts")                          
                        return! supervisorLoop (Map.add targetActor.Path (count + 1) restarts)
                    | None ->
                        strategy actor err targetActor                              
                        return! supervisorLoop (Map.add targetActor.Path 1 restarts)         
            }
        supervisorLoop <| Map(children |> Seq.map (fun c -> c.Path, 0))

type Supervisor(path:ActorPath, comp, ?children : seq<ActorRef>) as self =
    inherit Actor(path, comp (defaultArg children Seq.empty))

    do 
        (defaultArg children Seq.empty) |> Seq.iter ((-->) (SetSupervisor(self.Ref)))

    new(path:string, comp, ?children) =
        new Supervisor(path, comp, ?children = children)

