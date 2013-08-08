namespace FSharp.Actor.Tests

open System
open NUnit.Framework
open FsUnit
open FSharp.Actor
open System.Threading

type RequestReply = 
    | Add of int
    | Reply of IReplyChannel<int list>

[<TestFixture; Category("Unit")>]
type ``Given an Actor``() = 

    let delegateComp = 
        (fun (actor:IActor<_>) ->
            let rec loop() = 
                async {
                    let! (msg,_) = actor.Receive()
                    do msg(actor :> IActor)
                    return! loop()
                }
            loop()
        )

    let statefulComp = 
        (fun (actor:IActor<_>) -> 
            let rec loop store = 
                async { 
                    let! (msg, _) = actor.Receive()
                    return! loop (msg :: store)
                }
            loop []
        )

    let requestReply = 
        (fun (actor:IActor<_>) -> 
            let rec loop store = 
                async {
                    let! (msg, _) = actor.Receive()
                    match msg with
                    | Add(v) -> return! loop (v :: store)
                    | Reply(reply) -> 
                        reply.Reply(store)
                        return! loop store
                }
            loop []
        )

    let createActor name comp = 
        Actor.create (Actor.Options.Create(?id = name)) comp |> Actor.start 
    
    [<Test>]
    member x.``I can send a message to it``() =
        use actor = createActor None delegateComp
        let are = new AutoResetEvent(false) 
        let wasCalled = ref false
        actor.Post((fun (_:IActor) -> wasCalled := true; are.Set() |> ignore), None)
        are.WaitOne() |> ignore
        !wasCalled |> should equal true

    [<Test>]
    member x.``I can send a message and get a reply``() = 
        use actor = (createActor None requestReply :?> IActor<_>)
        actor <-- Add(2)
        actor <-- Add(7)
        let result = actor.PostAndTryReply((fun r -> Reply(r)), None).Value
        result |> should equal [7;2]

    [<Test>]
    member x.``I can shutdown the actor with a message``() = 
        let actor = createActor None delegateComp
        actor <!- Shutdown("Shutdown")
        actor.Status |> should equal (ActorStatus.Shutdown("Shutdown"))

    [<Test>]
    member x.``I can shutdown the actor with dispose``() = 
        let actor = createActor None delegateComp
        actor.Dispose()
        actor.Status |> should equal (ActorStatus.Disposed)

    [<Test>]
    member x.``I can restart the actor``() = 
        let actor = createActor None delegateComp
        let are = new AutoResetEvent(false)
        let wasCalled = ref false
        actor.PreRestart |> Event.add (fun x -> x.Status |> should equal (ActorStatus.Restarting))
        actor.OnRestarted |> Event.add (fun x -> wasCalled := true; are.Set() |> ignore)
        actor <!- Restart("Restarted")
        actor.Status |> should equal (ActorStatus.Running)
        !wasCalled |> should be True

    [<Test>]
    member x.``Two actors are equal via there Id``() = 
        use actorA = createActor (Some "AnActor") delegateComp
        use actorB = createActor (Some "AnActor") delegateComp
        actorA |> should equal actorB

    [<Test>]
    member x.``I can link actors together``() = 
        use parent = createActor (Some "Parent") delegateComp
        let child = createActor (Some "Child") delegateComp
        parent.Link(child)
        parent.Children |> List.ofSeq |> should equal [child]

    [<Test>]
    member x.``I can unlink actors``() = 
        use parent = createActor (Some "Parent") delegateComp
        use child = createActor (Some "Child") delegateComp
        parent.Link(child)
        parent.UnLink(child)
        parent.Children |> List.ofSeq |> should equal []

    [<Test>]
    member x.``Linked actors should not shutdown when parent is shutdown when shutdown policy is default``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", shutdownPolicy = Actor.ShutdownPolicy.Default)) delegateComp |> Actor.start
        let child = createActor (Some "Child") delegateComp
        parent.Link(child) 
        parent <!- Shutdown("Shutting down")
        child.Status.IsShutdownState() |> should be False

    [<Test>]
    member x.``Only some Linked actors should be shutdown when parent is shutdown when shutdown policy is selective``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", 
                                                         shutdownPolicy = Actor.ShutdownPolicy.Selective(fun actor -> actor.Id.Contains("1")))
                                   ) delegateComp |> Actor.start
        let child = createActor (Some "Child") delegateComp
        let child1 = createActor (Some "Child1") delegateComp
        parent.Link(child) 
        parent.Link(child1)
        parent <!- Shutdown("Shutting down")
        child.Status.IsShutdownState() |> should be False
        child1.Status.IsShutdownState() |> should be True

    [<Test>]
    member x.``Linked actors should shutdown when parent is shutdown when shutdown policy is cascade``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", shutdownPolicy = Actor.ShutdownPolicy.Cascade)) delegateComp |> Actor.start
        let child = createActor (Some "Child") delegateComp
        parent.Link(child) 
        parent <!- Shutdown("Shutting down")
        child.Status.IsShutdownState() |> should be True 

    [<Test>]
    member x.``I should not be able to send a message to an actor that is shutdown``() = 
        let actor = createActor (None) delegateComp
        actor <!- Shutdown("")
        Assert.Throws<Actor.UnableToDeliverMessageException>((fun _ -> actor <-- (fun (_:IActor)-> ())), """Cannot send message actor status invalid Shutdown("")""") |> ignore
    
    [<Test>]
    member x.``Spawning an actor should register the actor``() = 
        use actor = Actor.spawn (Actor.Options.Create("test/foo")) delegateComp
        let foundActor = Registry.Actor.find (Path.create "test/foo")
        foundActor |> should equal actor

    [<Test>]
    member x.``Shutting down a spawned actor should deregister it``() =
        let actor = Actor.spawn (Actor.Options.Create("test/foo")) delegateComp
        actor <!- Shutdown("")
        let foundActor = Registry.Actor.tryFind (Path.create "test/foo")
        foundActor |> should equal None

    [<Test>]
    member x.``Disposing a spawned actor should deregister it``() =
        let actor = Actor.spawn (Actor.Options.Create("test/foo")) delegateComp
        actor.Dispose()
        let foundActor = Registry.Actor.tryFind (Path.create "test/foo")
        foundActor |> should equal None

    [<Test>]
    member x.``If an actor is beign supervised then on an error should notify the supervisor``() = 
        let actor = createActor (Some "err") delegateComp
        let are = new AutoResetEvent(false)
        let wasHandled = ref false
        let error = Exception("Error")
        let supervisor = 
            Supervisor.create (Supervisor.Options.Default) (fun options sup -> 
                let rec loop() = 
                    async {
                        let! (ActorErrored(err, originator),sender) = sup.Receive()
                        originator |> should equal actor
                        err |> should equal error
                        wasHandled := true
                        are.Set() |> ignore
                        return! loop()
                    }
                loop()
            ) |> Actor.start
        actor.Watch(supervisor)
        actor.Post((fun (_:IActor) -> raise(error) |> ignore), None)
        are.WaitOne() |> ignore
        !wasHandled |> should be True

    [<Test>]
    member x.``Linked actors should not restart when parent is restarted when restart policy is default``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", restartPolicy = Actor.RestartPolicy.Default)) requestReply |> Actor.start
        let child = (createActor (Some "Child") requestReply) :?> IActor<RequestReply>
        parent.Link(child) 
        child <-- Add(2)
        child <-- Add(7)
        parent <!- Restart("Restart")
        child <-- Add(5)
        let result = child.PostAndTryReply((fun r -> Reply(r)), None)
        result |> should equal (Some [5; 7 ;2])

    [<Test>]
    member x.``Only some Linked actors should be restarted when parent is restarted when restart policy is selective``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", 
                                                         restartPolicy = Actor.RestartPolicy.Selective(fun actor -> actor.Id.Contains("1")))
                                   ) requestReply |> Actor.start
        let child = (createActor (Some "Child") requestReply) :?> IActor<RequestReply>
        let child1 = (createActor (Some "Child1") requestReply) :?> IActor<RequestReply>
        parent.Link(child) 
        parent.Link(child1)
        [child; child1] <-* Add(2)
        [child; child1] <-* Add(7)
        Thread.Sleep(50)
        parent <!- Restart("Restart")
        [child; child1] <-* Add(5)
        let result1 = child1 <-!> (fun r -> Reply(r))
        let result = child <-!> (fun r -> Reply(r))
        result1 |> should equal (Some [5])
        result |> should equal (Some [5; 7 ;2])
        

    [<Test>]
    member x.``Linked actors should restart when parent is restart when restart policy is cascade``() = 
        let parent = Actor.create (Actor.Options.Create("Parent", restartPolicy = Actor.RestartPolicy.Cascade)) requestReply |> Actor.start
        let child = (createActor (Some "Child") requestReply) :?> IActor<RequestReply>
        parent.Link(child) 
        child <-- Add(2)
        child <-- Add(7)
        Thread.Sleep(50)
        parent <!- Restart("Restart")
        child <-- Add(5)
        let result = child <-!> (fun r -> Reply(r))
        result |> should equal (Some [5])
        


