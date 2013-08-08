﻿(*** hide ***)
#load "Dependencies.fsx"
open FSharp.Actor

(**
#Supervising Actors

Actors can supervise other actors, if we define an actor loop that fails on a given message
*)

let err = 
        (fun (actor:Actor) ->
            let rec loop() =
                async {
                    let! msg = actor.Receive()
                    if msg <> "fail"
                    then printfn "%s" msg
                    else failwithf "ERRRROROROR"
                    return! loop()
                }
            loop()
        )

let createErrorActor name = 
    Actor.create (ActorOptions.Create(name,
                            restartPolicy = [(fun a -> a.Log.Error(sprintf "%A restarting" a, None))]
                                     )
                 ) err

(**
then a supervisor will allow the actor to restart or terminate depending on the particular strategy that is in place

##Strategies

A supervisor strategy allows you to define the restart semantics for the actors it is watching

###OneForOne
    
A supervisor will only restart the actor that has errored
*)
let errActor = createErrorActor("err")
let oneforoneSupervisor = 
    Supervisor.createDefault "Supervisor:OneForOne" Supervisor.Strategy.OneForOne (Some 3)
    |> Supervisor.superviseAll [errActor]

errActor <-- "Foo"
errActor <-- "fail"

(**
This yields

    Restarting (OneForOne: actor://main-pc/err_0) due to error System.Exception: ERRRROROROR
       at FSI_0012.err@134-2.Invoke(String message) in D:\Appdev\fsharp.actor\samples\Actor.fsx:line 134
       at Microsoft.FSharp.Core.PrintfImpl.go@523-3[b,c,d](String fmt, Int32 len, FSharpFunc`2 outputChar, FSharpFunc`2 outa, b os, FSharpFunc`2 finalize, FSharpList`1 args, Int32 i)
       at Microsoft.FSharp.Core.PrintfImpl.run@521[b,c,d](FSharpFunc`2 initialize, String fmt, Int32 len, FSharpList`1 args)
       at Microsoft.FSharp.Core.PrintfImpl.capture@540[b,c,d](FSharpFunc`2 initialize, String fmt, Int32 len, FSharpList`1 args, Type ty, Int32 i)
       at Microsoft.FSharp.Core.PrintfImpl.gprintf[b,c,d,a](FSharpFunc`2 initialize, PrintfFormat`4 fmt)
       at FSI_0012.err@132-1.Invoke(String _arg1) in D:\Appdev\fsharp.actor\samples\Actor.fsx:line 134
       at Microsoft.FSharp.Control.AsyncBuilderImpl.args@753.Invoke(a a)
    actor://main-pc/err_0 pre-stop Status: Errored
    actor://main-pc/err_0 stopped Status: Shutdown
    actor://main-pc/err_0 pre-restart Status: Restarting
    actor://main-pc/err_0 re-started Status: OK

we can see in the last 4 lines that the supervisor restarted this actor.


###OneForAll

If any watched actor errors all children of this supervisor will be told to restart.
*)

let err_1, err_2 = createErrorActor("err_1"), createErrorActor("err_2")

let oneforall = 
    Supervisor.createDefault "Supervisor:OneForAll" Supervisor.Strategy.OneForAll (Some 3)
    |> Supervisor.superviseAll [err_1; err_2]

err_1 <-- "Boo"
err_2 <-- "fail"

(**
This yields

    Restarting (OneForAll actor://main-pc/err_1) due to error System.Exception: ERRRROROROR
       at FSI_0004.err@134-2.Invoke(String message) in D:\Appdev\fsharp.actor\samples\Actor.fsx:line 134
       at Microsoft.FSharp.Core.PrintfImpl.go@523-3[b,c,d](String fmt, Int32 len, FSharpFunc`2 outputChar, FSharpFunc`2 outa, b os, FSharpFunc`2 finalize, FSharpList`1 args, Int32 i)
       at Microsoft.FSharp.Core.PrintfImpl.run@521[b,c,d](FSharpFunc`2 initialize, String fmt, Int32 len, FSharpList`1 args)
       at Microsoft.FSharp.Core.PrintfImpl.capture@540[b,c,d](FSharpFunc`2 initialize, String fmt, Int32 len, FSharpList`1 args, Type ty, Int32 i)
       at Microsoft.FSharp.Core.PrintfImpl.gprintf[b,c,d,a](FSharpFunc`2 initialize, PrintfFormat`4 fmt)
       at FSI_0004.err@132-1.Invoke(String _arg1) in D:\Appdev\fsharp.actor\samples\Actor.fsx:line 134
       at Microsoft.FSharp.Control.AsyncBuilderImpl.args@753.Invoke(a a)
    actor://main-pc/err_2 pre-stop Status: OK
    actor://main-pc/err_2 stopped Status: Shutdown
    actor://main-pc/err_2 pre-restart Status: Restarting
    actor://main-pc/err_2 re-started Status: OK
    actor://main-pc/err_1 pre-stop Status: Errored
    actor://main-pc/err_1 stopped Status: Shutdown
    actor://main-pc/err_1 pre-restart Status: Restarting
    actor://main-pc/err_1 re-started Status: OK

we can see here that all of the actors supervised by this actor has been restarted.

###Fail

A supervisor will terminate the actor that has errored
*)

let err_3, err_4 = createErrorActor("err_3"), createErrorActor("err_4")

let alwaysFail = 
    Supervisor.createDefault "Supervisor:AlwaysFail" Supervisor.Strategy.AlwaysFail (Some 3)
    |> Supervisor.superviseAll [err_3; err_4]

err_3 <-- "fail"

(**
This causes the actor `err_3` to be shut down. If we wanted a cluster of actors to fail if one actor in the fails then we can use


If you no longer require an actor to be supervised, then you can `Unwatch` the actor, repeating the OneForAll above
*)

let err_5, err_6 = createErrorActor("err_5"), createErrorActor("err_6")

let oneforallunwatch = 
    Supervisor.createDefault "Supervisor:OneForAll" Supervisor.Strategy.OneForAll (Some 3)
    |> Supervisor.superviseAll [err_5; err_6]

Actor.unwatch [err_5]

err_6 <-- "fail"
err_5 <-- "fail"

(**
We now see that one actor `err_6` has restarted, but when we signal the failure in `err_5` this actor will just shutdown.


*)