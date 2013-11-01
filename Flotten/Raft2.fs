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
    abstract messag_id : MessageId
    abstract source    : Actor
    abstract reply_to  : MessageId option

  type SendContext =
    abstract message_id : MessageId

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
      return { new SendContext with member x.message_id = id } }

  let (<!!) actors message =
    async {
      return! actors |> List.map (fun a -> a <!- message) |> Async.Parallel }

open Actors

/// Logical clock for knowing what term is current:
/// terms are used to identify stale information in Raft.
type Term    = uint32

/// Logical clock for knowing the time that Schedule requests were sent and timeouts received
type Clock   = bigint

/// State machine command
type Command = string
type CommandResult = string

/// Schedule a timeout to occur
type Schedule(message : obj, time : Instant) =
  member x.message = message
  member x.time    = time

/// timeout occurred
type timeout(data : obj) =
  member x.data = data

/// Invoked by candidates to gather votes (§5.2).
type RequestVoteRequest(term : Term, candidate_id : ActorId) =
  member x.term = term
  member x.candidate_id = candidate_id

type RequestVoteReply(term : Term, vote_granted : bool) =
  member x.term = term
  member x.vote_granted = vote_granted

/// Invoked by leader periodically to prevent other servers from starting new elections (§5.2).
type HeartbeatRequest(term : Term, leader_id : ActorId) =
  member x.term = term
  member x.leader_id = leader_id

type HeartbeatReply(term : Term) =
  member x.term = term

/// Invoked by leader to replicate log entries (§5.3); extension of Heartbeat RPC.
type AppendEntriesRequest(term : Term, leader_id : ActorId,
                          prev_log_index : Log.Index, prev_log_term : Term,
                          entries : Log.LogEntry list, commit_index : Log.Index) =
  member x.heartbeat    = HeartbeatRequest(term, leader_id)
  member x.prev_log_index = prev_log_index
  member x.prev_log_term  = prev_log_term
  member x.entries        = entries
  member x.commit_index   = commit_index

type AppendEntriesReply(term : Term, success : bool) =
  inherit HeartbeatReply(term)
  member x.success = success

/// Invoked by clients to modify the replicated state (§7.1).
type ClientRequestRequest(seq_no : uint32, command : Command) =
  member x.seq_no  = seq_no
  member x.command = command

type ClientRequestReply(success : bool, response : CommandResult, leader_hint : ActorId) =
  member x.success     = success
  member x.response    = response
  member x.leader_hint = leader_hint

// publish:

/// Published by servers upon learning of a newer term or candidate/leader.
type StepDown(term) =
  member x.term = term

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
  { timeout_term : Clock
  ; current_term : Term
  ; voted_for    : ActorId Option }
  static member Empty =
    { current_term = 0u; voted_for = None; timeout_term = 0I }

/// Candidates and leaders keep the following volatile state for each other server:
type PeerState =
  Map<ActorId, PeerStateData>
and PeerStateData =
  { vote_responded   : bool
  ; vote_granted     : bool
  ; next_index       : Log.Index
  ; last_agree_index : Log.Index }
  static member Empty =
    { vote_responded   = false
    ; vote_granted     = false
    ; next_index       = 0u
    ; last_agree_index = 0u }

/// Configuration that each node starts with
type Config =
  { id    : ActorId
  ; peers : Actor list }

/// Highly volatile state of outstanding RPC requests; RequestId (messageId)
/// to Reply.
type RPCState =
  { replies  : Map<MessageId, bool>
  ; vote_reqs : Set<MessageId> }
  static member Empty =
    { replies  = Map.empty
    ; vote_reqs = Set.empty }

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

  let respond (message_context : MessageContext) p_state reply = async {
    /// Each server persists the persistent state before responding to RPCs:
    do! persist p_state
    let! ctx = message_context.source <!- reply
    return ctx }

  /// persists persistent state, publishes the message and adds the outstanding
  /// RPCs to the returned value of type RPCState.
  let pub_rpc peers p_state rpc_state message =
    let add_rpc acc (ctx : SendContext) =
      { acc with replies = acc.replies.Add(ctx.message_id, false) } // TODO: always adds to votes
    async {
      do! persist p_state
      let! send_contexts = peers <!! message
      return send_contexts |> List.ofArray |> List.fold add_rpc rpc_state }

  /// Transitions the state to the next state (increments the local time)
  let reset_election state =
    let p, a, b = state
    { p with timeout_term = p.timeout_term + 1I }, a, b

  /// Sets the next term as the state and prepares the PeerState for a new round of
  /// voting.
  let next_term state =
    let p, rpc, peer = state
    let peer' = peer |> Option.fold (fun s t -> t |> Map.map (fun k t -> PeerStateData.Empty)) Map.empty
    { p with current_term = p.current_term + 1u }, rpc, Some(peer')

  /// Sets the voted_for to self and initialises the Peer State.
  let vote_self self_id state =
    let p, rpc, peer = state
    { p with voted_for = Some self_id }, rpc, Some(Map.empty)

  /// The message received doesn't fit the state the server/actor is in
  let invalid_in_state state_name msg =
    log "state %s doesn't accept %A" state_name msg

  /// The message wasn't current anymore, discarding.
  let stale_msg state_name msg =
    log "state %s received stale message %A" state_name msg

  let handle_heartbeat_msg (hb : HeartbeatRequest) state context
    (states : States) = async {
    let p_state, rpc_state, _ = state
    if hb.term < p_state.current_term then
      let reply = HeartbeatReply(p_state.current_term)
      let! _ = reply |> respond context p_state
      return! states.ContinueWithSame state // same state, same election
    elif hb.term > p_state.current_term then
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
          if rv.term < p_state.current_term then
            let! _ = RequestVoteReply(p_state.current_term, false) 
                     |> respond context p_state
            return! follower state
          elif rv.term > p_state.current_term then
            let! _ = RequestVoteReply(rv.term, true)
                     |> respond context p_state
            return! follower state
          else
            let reply = RequestVoteReply(rv.term, p_state.voted_for.IsNone || rv.candidate_id = p_state.voted_for.Value)
            let p_state', _, _ as state' = reset_election state
            let! _ = reply |> respond context p_state'
            return! follower state'
        | Heartbeat hb ->
          return! handle_heartbeat_msg hb state context states
        | AppendEntries ae ->
          // TODO: AppendEntries in follower state
          return! follower (reset_election state)
        | ClientRequest cr ->
          cr |> invalid_in_state "follower"
          return! follower state
      | ElectionTimeout(election, senttime)
        when election.term = p_state.current_terma
        &&   senttime      = p_state.timeout_term ->
        // since election timeout expired, become candidate
        return! become_candidate (reset_election state)
      | ElectionTimeout(election, senttime) ->
        // since senttime != timeout_term or the term != current_term; has had no RPC nor vote granted by follower
        return! follower state
      | Reply t ->
        t |> invalid_in_state "follower"
        return! follower state
      }

  and become_candidate state = async {
    let p_state', rpc_state, peer_state' =
      next_term state |> reset_election |> vote_self config.id
    let msg_req_vote = RequestVoteRequest(p_state'.current_term, config.id)
    let! rpc_state' = msg_req_vote |> pub_rpc config.peers p_state' rpc_state
    return! candidate (p_state', rpc_state', peer_state')
    }

  and candidate state =
    let p_state, rpc_state, peer_state = state
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
      | ElectionTimeout(election, sent_time) when sent_time = p_state.timeout_term ->
        return! become_candidate state
      | ElectionTimeout(election, sent_time) when sent_time <> p_state.timeout_term ->
        // ignore, old timeout! (gt relation unknown? ignore then)
        return! candidate state
      | Reply t ->
        // TODO: list of RPCs update?
        // TODO: change how rpc_state works - avoid having to prune it every new term?
        let rpc_id = context.reply_to.Value // always has value on reply
        match t with
        | RequestVoteRepl r when r.term = p_state.current_term ->
          let lost_election maj r = false // TODO: count for real instead
          let won_election maj r = false  // TODO: count for real instead
          let n = config.peers.Length
          let majority = n / 2 + 1 // int division
          if lost_election majority r then
            return! follower (reset_election state) // TODO: rpc state
          elif won_election majority r then
            return! leader (reset_election state) // TODO: rpc state
          else
            // TODO: let rpc_state' = ...
            return! candidate state
        | RequestVoteRepl r when r.term > p_state.current_term ->
          // discover higher term; step down
          return! follower (reset_election state)
        | RequestVoteRepl r when r.term < p_state.current_term ->
          // ignore, because old term received
          return! candidate state
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
      | ElectionTimeout(election, senttime) ->
        return ()
      | Reply t ->
        return ()
      }

  initialise ()
