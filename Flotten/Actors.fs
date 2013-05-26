
/// This module implements an actor framework (at the moment only type aliases, but
/// I would like to add network messaging to it
module Actors

/// an actor id, may be replaced with ICT Ids.
type Pid = int

/// a type alias for MailboxProcessor
type Actor<'a> = MailboxProcessor<'a>

type UntypedChannel = (obj -> unit)

/// a pid and a channel to post some object on
[<CustomEquality; CustomComparison>]
type ActorRef =
  { pid  : Pid
  ; send : UntypedChannel }
  override x.Equals yobj =
    match yobj with
    | :? ActorRef as y -> y.pid = x.pid
    | _ -> false
  override x.GetHashCode () = hash x.pid
  interface System.IComparable with
    member x.CompareTo yobj =
      match yobj with
      | :? ActorRef as y -> compare x.pid y.pid
      | _ -> invalidArg "yobj" "cannot compare values of different types"
  
type QuorumJoinMsg<'TReply, 'TRes> =
  | GetFinal of 'TRes AsyncReplyChannel
  | SetOne   of 'TReply

/// <summary>Perform a yield-harvest join - yield is like the fold over the aggregate state with the
/// latest reply: it aggregates whatever is needed for the group to reach a consensus.
/// </summary>
/// <param>yieldZero is simply the initial state.</param>
/// <param>fnHarvest does two things: verifies whether the current yield is enough by returning</param>
/// <param>true as the first value in the tuple, if it is, and secondly projects some value from
/// the aggregated state to be returned to the caller. This function is the reducer.</param>
/// <param>cluster is the actors to poll for results, the nodes mapped to.</param>
/// <param>facMsg is the corresponding Async.PostAndAsyncReply -> buildMessage -> ... -- buildMessage
/// parameter.</param>
let yieldHarvest
    (fnYield   : 'TState -> 'TReply -> 'TState)
    (yieldZero : 'TState)
    (fnHarvest : 'TState -> 'TRes option)
    (cluster   : (Pid * Actor<'TMsg>) list)
    (facMsg    : 'TReply AsyncReplyChannel -> 'TMsg) =
  let collector =
    Actor.Start <| fun inbox ->
      let rec start () = async {
        let! m = inbox.Receive ()
        match m with
        | GetFinal (chan : 'TRes AsyncReplyChannel) ->
          cluster
          |> List.map (fun (pid, actor) ->
            async {
              // TODO: timeout
              let! (voteResp : 'TReply) = actor.PostAndAsyncReply facMsg
              inbox.Post <| SetOne voteResp })
          |> Seq.iter Async.Start
          return! poll yieldZero chan // move to poll state.
        | _ -> failwith "wrong state to get SetOne message" }

      and poll results chan = async {
        let! m = inbox.Receive()
        match m with
        | SetOne (reply : 'TReply) ->
          let results' = reply |> fnYield results
          match fnHarvest results' with
          | Some result -> chan.Reply result
          | None        -> return! poll results' chan
        | _ -> failwith "wrong state to get GetFinal message" }
      start ()

  collector.PostAndAsyncReply <| fun (chan : 'TRes AsyncReplyChannel) -> GetFinal chan

