/// This module implements a timeout service
module Timeouts

open Actors
open FSharpx.Collections.Experimental
open NodaTime

/// helper function to write a string entry to the debug log.
let private debug  = System.Diagnostics.Debug.WriteLine

/// get the current instant
let private now () = Instant.FromDateTimeUtc(System.DateTime.UtcNow)


/// Internal state for the timeout service
type internal TSState =
  { upcoming : BinomialHeap<WorkItem> }
  static member Empty pid = { upcoming = BinomialHeap.empty(false (* ascending instants *)) }

/// Work Items for the timeout service
and [<CustomEquality; CustomComparison>] WorkItem =
  { actor   : ActorRef
  ; time    : Instant
  ; message : obj }
  override x.Equals(yobj) =
    match yobj with
    | :? WorkItem as y -> (x.time = y.time && x.actor.pid = y.actor.pid)
    | _ -> false
  override x.GetHashCode() = hash x.time
  interface System.IComparable with
    member x.CompareTo yobj =
      match yobj with
      | :? WorkItem as y -> compare x.time y.time // compare only on when should be sent
      | _ -> invalidArg "yobj" "cannot compare values of different types"

/// Messages for TimeoutService
type TSMsg =
  | Halt
  | Schedule of WorkItem

/// TimeoutService that sends messages to actors wanting timeouts
let startTS pid =
  let epsilon = 1 // milliseconds to wait for any message
  Actor.Start <|
    fun inbox ->
      let myself = { pid = pid; send = (fun m -> inbox.Post(m :?> TSMsg)) }

      /// in loop mode we're receiving scheduled timeouts
      let rec loop state = async {
        let! m = inbox.TryReceive epsilon
        match m with
        | Some(Schedule wi) -> return! loop { state with upcoming = state.upcoming |> BinomialHeap.insert wi }
        | Some(Halt)        -> return ()
        | None              -> return! chewOn state }

      /// in chewing mode we're just popping things off the binomial heap
      /// and executing the sending of the timeout
      and chewOn state = async {
        match state.upcoming.TryUncons() with
        | Some ({ actor = { pid = pid; send = send }; time = time; message = msg }, upcoming')
          when time <= now () ->
          debug <| sprintf "timeout for pid %i, sending %A to it." pid msg
          msg |> send
          return! chewOn { state with upcoming = upcoming' }
        | _    -> return! loop state
        }

      loop <| TSState.Empty(pid)
