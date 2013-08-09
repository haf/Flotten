module Raft2

open System
open Log
open NodaTime


module Persistence =
  let persist pstate =
    async { return () }

open Persistence

module Actors =

  /// A reference id for the actor
  type ActorId = uint32

  type Actor =
    abstract Id   : ActorId
    abstract Post : obj -> Async<unit>

  /// Messaging infrastructure
  type MessageContext =
    abstract MessageId : Guid
    abstract Source    : Actor

  type Inbox<'a> =
    abstract Receive : unit -> Async<'a * MessageContext>

  let find actorId : Actor =
    { new Actor with
        member x.Id     = 0u
        member x.Post o = async { return () } }

  let (<!-) actor message =
    async {
      do! (actor : Actor).Post message }

  let (<!!) actors message =
    async {
      for a in (actors : Actor list) do
        do! a <!- message }


open Actors

/// Logical clock for knowing what term is current:
/// Terms are used to identify stale information in Raft.
type Term    = uint32

/// Logical clock for knowing the time that Schedule requests were sent and Timeouts received
type Clock   = bigint

/// State machine command
type Command = string
type CommandResult = string

/// Schedule a timeout to occur
type Schedule(message : obj, time : Instant) =
  member x.Message = message
  member x.Time    = time

/// Timeout occurred
type Timeout(data : obj) =
  member x.Data = data

/// Invoked by candidates to gather votes (§5.2).
type RequestVote(term : Term, candidateId : ActorId) =
  member x.Term = term
  member x.CandidateId = candidateId

type RequestVoteReply(term : Term, voteGranted : bool) =
  member x.Term = term
  member x.VoteGranted = voteGranted

/// Invoked by leader periodically to prevent other servers from starting new elections (§5.2).
type Heartbeat(term : Term, leaderId : ActorId) =
  member x.Term = term
  member x.LeaderId = leaderId

type HeartbeatReply(term : Term) =
  member x.Term = term

/// Invoked by leader to replicate log entries (§5.3); extension of Heartbeat RPC.
type AppendEntries(term : Term, leaderId : ActorId,
                   prevLogIndex : Log.Index, prevLogTerm : Term,
                   entries : Log.LogEntry list, commitIndex : Log.Index) =
  member x.Heartbeat    = Heartbeat(term, leaderId)
  member x.PrevLogIndex = prevLogIndex
  member x.PrevLogTerm  = prevLogTerm
  member x.Entries      = entries
  member x.CommitIndex  = commitIndex

type AppendEntriesReply(term : Term, success : bool) =
  inherit HeartbeatReply(term)
  member x.Success          = success

/// Invoked by clients to modify the replicated state (§7.1).
type ClientRequest(seqNo : uint32, command : Command) =
  member x.SeqNo   = seqNo
  member x.Command = command

type ClientRequestReply(success : bool, response : CommandResult, leaderHint : ActorId) =
  member x.Success    = success
  member x.Response   = response
  member x.LeaderHint = leaderHint

// publish:

/// Published by servers upon learning of a newer term or candidate/leader.
type StepDown(term) =
  member x.Term = term

/// Data transfer object for elections
type Election =
  { term : Term }

/// All sorts of RPCs that Raft needs to be able to handle
type RPCs =
  | RequestVote of RequestVote
  | Heartbeat of Heartbeat
  | AppendEntries of AppendEntries
  | ClientRequest of ClientRequest

type Replies =
  | RequestVoteRepl of RequestVoteReply
  | HeartbeatRepl of HeartbeatReply
  | AppendEntriesRepl of AppendEntriesReply
  | ClientRequestRepl of ClientRequestReply

type Accepted =
  | RPC of RPCs
  | Reply of Replies
  | ElectionTimeout of Election * Clock

type PersistentState =
  { timeoutTerm : Clock
  ; currentTerm : Term
  ; votedFor    : ActorId Option }
  static member Empty =
    { currentTerm = 0u; votedFor = None; timeoutTerm = 0I }

/// Candidates and leaders keep the following volatile state for each other server:
type PeerState =
  { voteResponded  : bool
  ; voteGranted    : bool
  ; nextIndex      : Log.Index
  ; lastAgreeIndex : Log.Index }
  static member Empty =
    { voteResponded  = false
    ; voteGranted    = false
    ; nextIndex      = 0u
    ; lastAgreeIndex = 0u }

/// Configuration that each node starts with
type Config =
  { id    : ActorId
  ; peers : ActorId list }

/// Highly volatile state of outstanding RPC requests
type RPCState =
  { votes  : Map<Guid, RequestVoteReply>
  ; config : Config }
  static member Create config =
    { votes  = Map.empty
    ; config = config }

/// State kept by the raft actor/node
type State = PersistentState * RPCState * Option<PeerState>

module RaftProtocol =

  let respond (messageContext : MessageContext) p_state reply =
    async {
      /// Each server persists the persistent state before responding to RPCs:
      do! persist p_state
      do! messageContext.Source <!- reply }

  /// transitions the state to the next state (increments the local time)
  let resetElection state = 
    let p, a, b = state
    { p with timeoutTerm = p.timeoutTerm + 1I }, a, b

open RaftProtocol

let spawn (inbox : Inbox<Accepted>) config =

  let rec initialise () =
    async {
      let state = PersistentState.Empty, (RPCState.Create config), None
      return! follower state }

  and follower (state : State) =
    let p_state, rpc_state, _ = state
    async {
      let! msg, context = inbox.Receive()
      match msg with
      | RPC rpc ->
        match rpc with
        | RequestVote rv ->
          if rv.Term < p_state.currentTerm then
            do! RequestVoteReply(p_state.currentTerm, false) 
              |> respond context p_state
            return! follower state
          elif rv.Term > p_state.currentTerm then
            do! RequestVoteReply(rv.Term, true)
              |> respond context p_state
            return! follower state
          else
            let reply = RequestVoteReply(rv.Term, p_state.votedFor.IsNone || rv.CandidateId = p_state.votedFor.Value)
            let p_state', _, _ as state' = resetElection state
            do! reply |> respond context p_state'
            return! follower state'
        | Heartbeat hb -> return ()
        | AppendEntries ae -> return ()
        | ClientRequest cr -> return ()
      | ElectionTimeout(election, sentTime)
        when election.term = p_state.currentTerm
        &&   sentTime      = p_state.timeoutTerm ->
        // since election timeout expired, become candidate
        return! initialise_candidate(resetElection(state))
      | ElectionTimeout(election, sentTime) ->
        // since sentTime != timeoutTerm or the term != currentTerm; has had no RPC nor vote granted by follower
        return! follower state
      | Reply t ->
        
        return! follower state
      }

  and initialise_candidate state =
    async {
      return! candidate state
      }

  and candidate state =
    async {
      return! leader state
      }

  and leader state =
    async {
      return! follower state
      }

  initialise ()