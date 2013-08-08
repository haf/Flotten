namespace FSharp.Actor.ZeroMq

open FSharp.Actor
open System.Threading

module ZeroMQ =

    open System
    open ZeroMQ
    open FsCoreSerializer
    open FSharp.Actor
    open System.Text

    let createContext() = 
        ZeroMQ.ZmqContext.Create()

    let publisher endpoint (serialiser:ISerializer) (event:IEvent<MessageEnvelope>) =
        async {
            try
                use ctx = createContext()
                use socket = ctx.CreateSocket(SocketType.PUB)
                socket.Bind(endpoint)
                while true do
                    let! message = event |> Async.AwaitEvent
                    let msg = ZmqMessage()
                    let header = Frame(Encoding.UTF8.GetBytes(String.Format("{0} ",[|message.Topic|]))) 
                    let payload = Frame(serialiser.Serialize(message))
                    socket.SendMessage(ZmqMessage([|header; payload|])) |> ignore
            with e -> 
                printfn "Pub error: %A" e               
        }

    let subscribe endpoint (topic:string list) (serialiser:ISerializer)  onReceived  = 
        async {
            try
                use ctx = createContext()
                use socket = ctx.CreateSocket(SocketType.SUB)

                if topic |> List.isEmpty
                then socket.SubscribeAll()
                else topic |> List.iter (Encoding.UTF8.GetBytes >> socket.Subscribe)
                socket.Connect(endpoint)

                while true do 
                    let msg = socket.ReceiveMessage()
                    if msg.FrameCount = 2
                    then
                        let bytes = (msg.[1].Buffer) 
                        let result = serialiser.Deserialize(bytes) :?> MessageEnvelope
                        onReceived(result)                        
            with e -> 
                printfn "Sub erro: %A" e
        }

    let transport publishEndpoint subscribeEndpoint subscriptions serialiser = 
        let publishEvent = new Event<_>()
        let receiveEvent = new Event<_>()
        let cts = new CancellationTokenSource()
        { new ITransport with
            member x.Post(msg:MessageEnvelope) = publishEvent.Trigger(msg)
            member x.Receive with get() = receiveEvent.Publish
            member x.Start() =
                 Async.Start(publisher publishEndpoint serialiser publishEvent.Publish, cts.Token)
                 Async.Start(subscribe subscribeEndpoint subscriptions serialiser receiveEvent.Trigger, cts.Token)
            member x.Dispose() = 
                cts.Cancel()
                
        }
