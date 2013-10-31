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

  /// A message id type
  type MessageId = Guid

  type Actor =
    abstract Id   : ActorId
    abstract Post : obj -> Async<unit>

  /// Messaging infrastructure
  type MessageContext =
    abstract MessageId : MessageId
    abstract Source    : Actor
    abstract ReplyTo   : MessageId option

  type SendContext =
    abstract MessageId : MessageId

  type Inbox<'a> =
    abstract Receive : unit -> Async<'a * MessageContext>

  let uuid () : MessageId = System.Guid.NewGuid()

  let find actorId : Actor =
    { new Actor with
        member x.Id     = 0u
        member x.Post o = async { return () } }

  let post actor messageId message =
    async { do! (actor : Actor).Post message }

  let (<!-) actor message =
    async {
      let id = uuid ()
      do! post actor id message
      return { new SendContext with member x.MessageId = id } }

  let (<!!) actors message =
    async {
      return! actors |> List.map (fun a -> a <!- message) |> Async.Parallel }

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
type RequestVoteRequest(term : Term, candidateId : ActorId) =
  member x.Term = term
  member x.CandidateId = candidateId

type RequestVoteReply(term : Term, voteGranted : bool) =
  member x.Term = term
  member x.VoteGranted = voteGranted

/// Invoked by leader periodically to prevent other servers from starting new elections (§5.2).
type HeartbeatRequest(term : Term, leaderId : ActorId) =
  member x.Term = term
  member x.LeaderId = leaderId

type HeartbeatReply(term : Term) =
  member x.Term = term

/// Invoked by leader to replicate log entries (§5.3); extension of Heartbeat RPC.
type AppendEntriesRequest(term : Term, leaderId : ActorId,
                          prevLogIndex : Log.Index, prevLogTerm : Term,
                          entries : Log.LogEntry list, commitIndex : Log.Index) =
  member x.Heartbeat    = HeartbeatRequest(term, leaderId)
  member x.PrevLogIndex = prevLogIndex
  member x.PrevLogTerm  = prevLogTerm
  member x.Entries      = entries
  member x.CommitIndex  = commitIndex

type AppendEntriesReply(term : Term, success : bool) =
  inherit HeartbeatReply(term)
  member x.Success = success

/// Invoked by clients to modify the replicated state (§7.1).
type ClientRequestRequest(seqNo : uint32, command : Command) =
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
  | RequestVote of RequestVoteRequest
  | Heartbeat of HeartbeatRequest
  | AppendEntries of AppendEntriesRequest
  | ClientRequest of ClientRequestRequest

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
  Map<ActorId, PeerStateData>
and PeerStateData =
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
  ; peers : Actor list }

/// Highly volatile state of outstanding RPC requests; RequestId (MessageId)
/// to Reply.
type RPCState =
  { replies  : Map<MessageId, bool>
  ; voteReqs : Set<MessageId> }
  static member Empty =
    { replies  = Map.empty
    ; voteReqs = Set.empty }

/// State kept by the raft actor/node
type State = PersistentState * RPCState * Option<PeerState>

module Logging =
  let log = printfn

module RaftProtocol =
  open Logging

  type StateOption =
    | Follower
    | Candidate
    | Leader

  type States =
    { follower  : State -> Async<unit>
    ; candidate : State -> Async<unit>
    ; leader    : State -> Async<unit>
    ; current   : StateOption }
  with
    member x.ContinueWithSame state = async {
      match x.current with
      | Follower -> return! x.follower state
      | Candidate -> return! x.candidate state
      | Leader    -> return! x.leader state }
    static member AsFollower(f, b_c, l) =
      { follower = f; candidate = b_c; leader = l; current = Follower }
    static member AsCandidate(f, c, l) =
      { follower = f; candidate = c; leader = l; current = Candidate }
    static member AsLeader(f, c, l) =
      { follower = f; candidate = c; leader = l; current = Leader }

  let respond (messageContext : MessageContext) p_state reply =
    async {
      /// Each server persists the persistent state before responding to RPCs:
      do! persist p_state
      let! ctx = messageContext.Source <!- reply
      return ctx }

  /// persists persistent state, publishes the message and adds the outstanding
  /// RPCs to the returned value of type RPCState.
  let pub_rpc peers p_state rpc_state message =
    let add_rpc acc (ctx : SendContext) =
      { acc with replies = acc.replies.Add(ctx.MessageId, false) } // TODO: always adds to votes
    async {
      do! persist p_state
      let! send_contexts = peers <!! message
      return send_contexts |> List.ofArray |> List.fold add_rpc rpc_state }

  /// Transitions the state to the next state (increments the local time)
  let reset_election state =
    let p, a, b = state
    { p with timeoutTerm = p.timeoutTerm + 1I }, a, b

  /// Sets the next term as the state
  let next_term state =
    let p, a, b = state
    { p with currentTerm = p.currentTerm + 1u }, a, b

  /// Sets the votedFor to self and initialises the Peer State.
  let vote_self selfId state =
    let p, rpc, shared = state
    { p with votedFor = Some(selfId) }, rpc, Some(Map.empty)

  /// The message received doesn't fit the state the server/actor is in
  let invalid_in_state state_name msg =
    log "state %s doesn't accept %A" state_name msg

  /// The message wasn't current anymore, discarding.
  let stale_msg state_name msg =
    log "state %s received stale message %A" state_name msg

  let handle_heartbeat_msg (hb : HeartbeatRequest) state context
    (states : States) = async {
    let p_state, rpc_state, _ = state
    if hb.Term < p_state.currentTerm then
      let reply = HeartbeatReply(p_state.currentTerm)
      let! _ = reply |> respond context p_state
      return! states.ContinueWithSame state // same state, same election
    elif hb.Term > p_state.currentTerm then
      let state' = next_term state
      return! states.follower (reset_election state)
    else
      // if someone send a HB at current term
      return! states.follower (reset_election state) }

open Logging
open RaftProtocol

let spawn (inbox : Inbox<Accepted>) config =

  let rec initialise () = async {
    let state = PersistentState.Empty, RPCState.Empty, None
    return! follower state }

  and follower (state : State) =
    let p_state, rpc_state, _ = state
    let states = States.AsFollower(follower, become_candidate, leader)
    async {
      let! msg, context = inbox.Receive()
      match msg with
      | RPC rpc ->
        match rpc with
        | RequestVote rv ->
          if rv.Term < p_state.currentTerm then
            let! _ = RequestVoteReply(p_state.currentTerm, false) 
                     |> respond context p_state
            return! follower state
          elif rv.Term > p_state.currentTerm then
            let! _ = RequestVoteReply(rv.Term, true)
                     |> respond context p_state
            return! follower state
          else
            let reply = RequestVoteReply(rv.Term, p_state.votedFor.IsNone || rv.CandidateId = p_state.votedFor.Value)
            let p_state', _, _ as state' = reset_election state
            let! _ = reply |> respond context p_state'
            return! follower state'
        | Heartbeat hb ->
          return! handle_heartbeat_msg hb state context states
        | AppendEntries ae ->
          // Todo
          return! follower (reset_election state)
        | ClientRequest cr ->
          cr |> invalid_in_state "follower"
          return! follower state
      | ElectionTimeout(election, sentTime)
        when election.term = p_state.currentTerm
        &&   sentTime      = p_state.timeoutTerm ->
        // since election timeout expired, become candidate
        return! become_candidate (reset_election state)
      | ElectionTimeout(election, sentTime) ->
        // since sentTime != timeoutTerm or the term != currentTerm; has had no RPC nor vote granted by follower
        return! follower state
      | Reply t ->
        // ignore replies in this state
        return! follower state
      }

  and become_candidate state =
    async {
      let p_state', rpc_state, peer_state' = next_term state |> reset_election |> vote_self config.id
      let msg_req_vote = RequestVoteRequest(p_state'.currentTerm, config.id)
      let! rpc_state' = msg_req_vote |> pub_rpc config.peers p_state' rpc_state
      return! candidate (p_state', rpc_state', peer_state')
      }

  and candidate state =
    let p_state, rpc_state, _ = state
    let states = States.AsCandidate(follower, candidate, leader)
    async {
      let! msg, context = inbox.Receive()
      match msg with
      | RPC rpc ->
        match rpc with
        | RequestVote rv ->
          return ()
        | Heartbeat hb ->
          return! handle_heartbeat_msg hb state context states
        | AppendEntries ae ->
          return ()
        | ClientRequest cr ->
          return ()
      | ElectionTimeout(election, sentTime) ->
        return ()
      | Reply t ->
        let rpc_id = context.ReplyTo.Value // always has value on reply
        match t with
        | RequestVoteRepl r ->
//          if rpc_state.voteReqs.Contains rpc_id then
//            
          return ()
        | HeartbeatRepl h ->
          return ()
        | AppendEntriesRepl a ->
          return ()
        | ClientRequestRepl cr ->
          // ignore client requests when in candidate state
          return! candidate state
      }

  and leader state =
    async {
      let! msg, context = inbox.Receive()
      match msg with
      | RPC rpc ->
        match rpc with
        | RequestVote rv ->
          return ()
        | Heartbeat hb ->
          return ()
        | AppendEntries ae ->
          return ()
        | ClientRequest cr ->
          return ()
      | ElectionTimeout(election, sentTime) ->
        return ()
      | Reply t ->
        return ()
      }

  initialise ()
