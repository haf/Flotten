#load "Dependencies.fsx"
open FSharp.Actor

(**
#Basic Actors
*)

ActorSystem.createWithDefaultConfiguration "example" []

let multiplication = 
    (fun (actor:Actor) ->
        let log = actor.Log
        let rec loop() =
            async {
                let! (a,b) = actor.Receive()
                let result = a * b
                do log.Debug(sprintf "%A: %d * %d = %d" actor a b result, None)
                return! loop()
            }
        loop()
    )

let addition = 
    (fun (actor:Actor) ->
        let log = actor.Log
        let rec loop() =
            async {
                let! (a,b) = actor.Receive()
                let result = a + b
                do log.Debug(sprintf "%A: %d + %d = %d" actor a b result, None)
                return! loop()
            }
        loop()
    )

let calculator = 
    [
       Actor.create (ActorOptions.Create("calculator/addition")) addition
       Actor.create (ActorOptions.Create("calculator/multiplication")) multiplication
    ]

(**
The above code creates two actors `calcualtor/addition` and `calculator/multiplication`

    val calculator : FSharp.Actor.ActorRef list =
      [calculator/addition; calculator/multiplication]

We can send messages directly to the actors by picking them out the list and using the `<--` operator.
This is just an alias for the `Post` method which can be used directly aswell.
*)

calculator.[0] <-- (5,2)
calculator.[1] <-- (5,2)


(**
We can also resolve actors by name. However this requires that we setup up a dispatcher. 

Dispatchers are `FSharp.Actors` way of loosely sending/dispatching messages. The following code sets-up
a dispatcher with the two actors above registered within it. 
*)

Dispatcher.set(new DisruptorBasedDispatcher(calculator, onUndeliverable = (fun msg -> Logger.Current.Error("Could not deliver message", None))))

!!"calculator/addition" <-- (5,2)
!!"calculator/multiplication" <-- (5,2)

(**
This also yields 

    calculator/addition: 5 + 2 = 7

Or we could have broadcast to all of the actors in that collection
*)

calculator <-* (5,2)

(**
This also yields 

    calculator/addition: 5 + 2 = 7
    calculator/multiplication: 5 * 2 = 10

We can also resolve _systems_ of actors.
*)
"calculator" ?<-* (5,2)

(**
This also yields 

    calculator/addition: 5 + 2 = 7
    calculator/multiplication: 5 * 2 = 10

However this actor wont be found because it does not exist
*)

"calculator/addition/foo" ?<-- (5,2)

(**
We can also kill actors 
*)

calculator.[1] <-- (Shutdown("Cause I want to"))

(** or *)

"calculator/addition" ?<-- (Shutdown("Cause I want to"))

(**
Sending now sending any message to the actor will result in an exception 

    System.InvalidOperationException: Actor (actor://main-pc/calculator/addition) could not handle message, State: Shutdown
*)


(**
#Changing the behaviour of actors

You can change the behaviour of actors at runtime. This achieved through mutually recursive functions
*)

let rec schizoPing = 
    (fun (actor:Actor) ->
        let log = actor.Log
        let rec ping() = 
            async {
                let! msg = actor.Receive()
                log.Info(sprintf "(%A): %A ping" actor msg, None)
                return! pong()
            }
        and pong() =
            async {
                let! msg = actor.Receive()
                log.Info(sprintf "(%A): %A pong" actor msg, None)
                return! ping()
            }
        ping()
    )
        

let schizo = Actor.create (ActorOptions.Create("schizo")) schizoPing 

schizo <-- "Hello"

(**

Sending two messages to the 'schizo' actor results in

    (schizo): "Hello" ping

followed by

    (schizo): "Hello" pong

#Linking Actors

Linking an actor to another means that this actor will become a sibling of the other actor. This means that we can create relationships among actors
*)

let child i = 
    Actor.create (ActorOptions.Create(sprintf "a/child_%d" i)) 
         (fun actor ->
             let log = actor.Log 
             let rec loop() =
                async { 
                   let! msg = actor.Receive()
                   log.Info(sprintf "%A recieved %A" actor msg, None) 
                   return! loop()
                }
             loop()
         )

let parent, children = 
    let children = (List.init 5 child)
    Actor.createLinked (ActorOptions.Create "a/parent") children
            (fun actor -> 
                let rec loop() =
                  async { 
                      let! msg = actor.Receive()
                      actor.Options.Children <-* msg
                      return! loop()
                  }
                loop()    
            ), children 

parent <-- "Forward this to your children"

(**
This outputs

   a/child_1 recieved "Forward this to your children"
   a/child_3 recieved "Forward this to your children"
   a/child_2 recieved "Forward this to your children"
   a/child_4 recieved "Forward this to your children"
   a/child_0 recieved "Forward this to your children"

We can also unlink actors
*)

Actor.unlink [children.Head] parent

parent <-- "Forward this to your children"

(**
This outputs

    a/child_1 recieved "Forward this to your children"
    a/child_3 recieved "Forward this to your children"
    a/child_2 recieved "Forward this to your children"
    a/child_4 recieved "Forward this to your children"

#State in Actors

State in actors is managed by passing an extra parameter around the loops. For example,
*)

let incrementer =
    Actor.create ActorOptions.Default (fun (actor:Actor) -> 
        let log = actor.Log
        let rec loopWithState (currentCount:int) = 
            async {
                let! a = actor.Receive()
                log.Debug(sprintf "Incremented count by %d" a, None) 
                return! loopWithState (currentCount + a)
            }
        loopWithState 0
    )

incrementer <-- 1
incrementer <-- 2

(**
The question now remains is how do we retrieve the state contained within the actor
*)