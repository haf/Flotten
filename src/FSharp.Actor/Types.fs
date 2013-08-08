namespace FSharp.Actor

open System
open System.Threading

[<AutoOpen>]
module Types = 
    
    type ActorNotFound(name) = 
        inherit Exception(sprintf "Unable to find actor %A" name)

    type UnableToDeliverMessageException(msg) = 
        inherit Exception(msg)

    type InvalidDispatchConfiguration(msg) =
        inherit Exception(msg)

    type InvalidActorPath(invalidPath:string) =
        inherit Exception(sprintf "%A is an invalid path should be of the form actor.{transport}://{host}:{port}/{actor}" invalidPath)

    type ActorPath = {
        Actor : string    }
    with
        override x.ToString() =  x.Actor
        static member Create(actor, ?transport, ?host, ?port) =
            { Actor = actor; }
        static member op_Implicit(path:string) = ActorPath.Create(path) 

    type MessageEnvelope = { 
        mutable Message : obj
        mutable Properties : Map<string, obj>
        mutable Target : ActorPath
        mutable Sender : ActorPath
        mutable Topic : string
    }
    with
        static member Default = { Message = null; Topic = "*"; Properties = Map.empty; Sender = ActorPath.Create(""); Target = ActorPath.Create("*") }
        static member Factory = new Func<_>(fun () ->  MessageEnvelope.Default)
        static member Create(message, target, ?sender, ?props) = 
            { Message = message; Topic = "*"; Properties = defaultArg props Map.empty; Sender = defaultArg sender (ActorPath.Create("")); Target = target }
    
    type ActorRef(path:ActorPath, onPost : (MessageEnvelope -> unit)) =
         member val Path = path with get
         
         member x.Post (msg: MessageEnvelope) = onPost msg
        
         override x.ToString() =  x.Path.ToString()
         override x.Equals(y:obj) = 
             match y with
             | :? ActorRef as y -> x.Path = y.Path
             | _ -> false
         override x.GetHashCode() = x.Path.GetHashCode()
         static member (<--) (ref:ActorRef, msg) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path))
         static member (<--) (ref:ActorRef, (msg, sender)) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path, sender))
         static member (<--) (ref:ActorRef, (msg, sender, props)) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path, sender, props))
         static member (<--) (ref:ActorRef, msg) = 
            ref.Post(msg)
         static member (-->) (msg, ref:ActorRef) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path))
         static member (-->) ((msg, sender), ref:ActorRef) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path, sender))
         static member (-->) ((msg, sender, props),ref:ActorRef) = 
            ref.Post(MessageEnvelope.Create(msg, ref.Path, sender, props))
         static member (-->) (msg, ref:ActorRef) = ref.Post(msg)
    

    type IRegister = 
        inherit IDisposable
        abstract TryResolve : ActorPath -> ActorRef option
        abstract Resolve : ActorPath -> ActorRef 
        abstract ResolveAll : ActorPath -> seq<ActorRef> 
        abstract Register : ActorRef -> unit
        abstract Remove : ActorRef -> unit

    type ITransport = 
        inherit IDisposable
        abstract Post : MessageEnvelope -> unit
        abstract Receive : IEvent<MessageEnvelope> with get
        abstract Start : unit -> unit

    type IDispatcher = 
        inherit IDisposable
        abstract Post : MessageEnvelope -> unit
        abstract Configure : IRegister * seq<ITransport> * ActorRef -> unit  

    type ILogger = 
        abstract Debug : string * exn option -> unit
        abstract Info : string * exn option -> unit
        abstract Warning : string * exn option -> unit
        abstract Error : string * exn option -> unit

    type IReplyChannel<'a> =
        abstract Reply : 'a -> unit
    
    type IMailbox<'a> = 
         inherit IDisposable
         abstract Receive : int option * CancellationToken -> Async<'a>
         abstract Post : 'a -> unit
         abstract Length : int with get
         abstract IsEmpty : bool with get
         abstract Restart : unit -> unit

