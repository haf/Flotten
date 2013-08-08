namespace FSharp.Actor

module Async = 
    
    open FSharp.Actor    
    open System.Threading.Tasks

    type ResultCell<'a>() =
        let source = new TaskCompletionSource<'a>()
    
        member x.RegisterResult result = source.SetResult(result)
    
        member x.AsyncWaitResult =
            Async.FromContinuations(fun (cont,_,_) -> 
                let apply = fun (task:Task<_>) -> cont (task.Result)
                source.Task.ContinueWith(apply) |> ignore)
            
    
        member x.GetWaitHandle(timeout:int) =
            async { let waithandle = source.Task.Wait(timeout)
                    return waithandle }
    
        member x.GrabResult() = source.Task.Result
    
        member x.TryWaitResultSynchronously(timeout:int) = 
            if source.Task.IsCompleted then 
                Some source.Task.Result
            else 
                if source.Task.Wait(timeout) then 
                    Some source.Task.Result
                else None

    type ReplyChannel<'Reply>(replyf : 'Reply -> unit) =
        interface IReplyChannel<'Reply> with
            member x.Reply(reply) = replyf(reply)

