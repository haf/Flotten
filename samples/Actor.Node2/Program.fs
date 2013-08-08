// Learn more about F# at http://fsharp.net
// See the 'F# Tutorial' project for more help.

open System
open FSharp.Actor
open FSharp.Actor.ZeroMq
open FsCoreSerializer

do
  ActorSystem.configure(
        ActorSystemConfiguration.Create(
                transports = [ZeroMQ.transport "tcp://127.0.0.1:6667" "tcp://127.0.0.1:6666" [] (new FsCoreSerializer())]
                ))

let pingPong = 
    ActorSystem.actorOf("ping-pong", 
       (fun (actor:Actor) ->
            let log = actor.Log
            let rec loop() = 
                async {
                    let! msg = actor.Receive()
                    log.Debug(sprintf "Msg: %s" msg, None)
                    //(msg.Sender |> string) ?<-- "pong"
                    return! loop()
                }
            loop()
        ))

[<EntryPoint>]
let main argv =
    Console.WriteLine("node-2 started");
    Console.ReadLine() |> ignore
   
    pingPong <-- "Hello"

    let mutable ended = false
    while not <| ended do
        "ping-pong" ?<-- "Ping"
        let input = Console.ReadLine()
        ended <- input = "exit"

    Console.ReadLine() |> ignore

    0
