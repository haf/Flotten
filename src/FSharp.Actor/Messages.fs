namespace FSharp.Actor

type SystemMessage = 
    | Shutdown of string
    | Restart of string
    | Link of ActorRef
    | UnLink of ActorRef
    | SetSupervisor of ActorRef
    | RemoveSupervisor 

type SupervisorMessage =
    | Errored of exn * ActorRef
