module Flotten

/// This module contains the Raft server implementation
module RaftServer =

  open NodaTime
  open Actors
  open Timeouts
  open Log

  /// helper function to write a string entry to the debug log.
  let private  debug  = System.Diagnostics.Debug.WriteLine
  /// get the current instant
  let private now () = Instant.FromDateTimeUtc(System.DateTime.UtcNow)
  /// Raft protocol options that each sever in the cluster starts with.
  type ProtoOpts =
    { electionTimeout : Duration }

  /// Possible messages to send to a Raft server.
  type RaftMessage =
    /// Sent to the actor when the timeout service has a timeout
    | ElectionTimeout of Clock
    /// The actor sends this message to itself from its outstanding
    /// RequestVote calls that happen in parallel
    | VoteConcluded of VoteResult
    // these are the only (four) messages in the algorithm
    | RequestVote of VoteRequest * VoteReply AsyncReplyChannel
    | AppendEntries of AppendRequest * AppendReply AsyncReplyChannel

  /// Request to do a vote
  and VoteRequest =
    /// candidate's term
    { term : Term
    /// candidate's Id (candidateId in paper)
    ; candidate    : ActorRef
    /// index of candidate's last log entry (§5.4)
    ; lastLogIndex : Log.Index
    /// term of candidate's last log entry (§5.4)
    ; lastLogTerm  : Term }
  /// The reply to the request to do a vote
  and VoteReply =
    /// currentTerm, for candidate to update itself
    { term : Term
    /// true means candidate received vote 
    ; voteGranted : bool }
  /// When a round of votes is completed, receive the vote result
  /// message.
  and VoteResult =
    { wasPassed : bool
    ; results   : VoteReply list }
  /// Request to append a LogDelta
  and AppendRequest =
    { term         : Term
    ; leaderId     : ActorRef
    ; prevLogIndex : Log.Index
    ; prevLogTerm  : Term
    ; entries      : LogDelta
    ; commitIndex  : Log.Index }
  and AppendReply =
    { term    : Term
    ; success : bool }

  /// Internal state for each Raft actor
  type internal MyState =
    /// Each server stores a current term number, which increases monotonically over time.
    { term      : Term
    /// The process identifier is used to uniquely identify the actor.
    ; pid       : Pid
    /// The cluster is a list of all the other Raft servers.
    ; cluster   : (Pid * Actor<RaftMessage>) list
    /// A list of all followers of myself
    ; followers : Follower list
    /// Who I voted for last
    ; votedFor  : ActorRef option
    /// TODO: update on send/receive (include in actor?)
    /// This is my own addition to the mix, a local clock that
    /// allows me to keep track of whether I should ignore a scheduled
    /// timeout (if it's stale the clock of its message will be less than
    /// this value).
    ; tsClock      : Clock
    /// The last log index written
    ; lastLogIndex : Log.Index
    /// The last log term seen/written
    ; lastLogTerm  : Term
    /// ￼A Sequence of log entries. The index into this sequence is the index of the log entry
    ; log          : LogInstance }

    /// The empty state
    static member Empty pid log = 
      { term         = 0I
      ; pid          = pid
      ; cluster      = []
      ; followers    = []
      ; votedFor     = None
      ; tsClock      = Clock.Zero
      ; lastLogIndex = Log.Index.MinValue
      ; lastLogTerm  = Term.Zero
      ; log          = log }

  and Follower =
    { actorRef : ActorRef
    ; nextIndex : Log.Index }

  /// Raft Actor implementing the Raft protocol
  let startRA pid (ts : MailboxProcessor<TSMsg>) (opts : ProtoOpts) log =

    /// Schedule message msg in time time to be sent to the actor.
    /// Async to allow a different external service to be used at some
    /// future point in time.
    let schedule actor time msg = async {
      do ts.Post <| Schedule { actor = actor; message = msg; time = time } }

    let logIsComplete lastLogTerm lastLogIndex =
      // TODO, the log is complete if ...
      true

    // TODO: need to persist state before sending any message
    // Assume reliable messaging; all messages sent will eventually be delivered
    Actor.Start <|
      fun inbox ->
        let myself = { pid = pid; send = (fun m -> inbox.Post(m :?> RaftMessage)) }
        let schedule = schedule myself

        let rec follower state = async {
          do! (ElectionTimeout state.tsClock) |> schedule (now () + opts.electionTimeout)

          let! msg = inbox.Receive ()
          match msg with
          /// To begin an election, a follower increments its current term and transitions to candidate state.
          /// Convert to candidate if election timeout elapses without either:
          /// * Receiving valid AppendEntries RPC, or
          /// * Granting vote to candidate.
          /// Incrementing the clock allows us to ignore old timeout messages (and reset election timeout)
          | ElectionTimeout timeoutClock when timeoutClock = state.tsClock ->
            return! candidate_start { state with term = state.term + 1I; tsClock = state.tsClock + 1I }

          | RequestVote ({ candidate = { pid = candidatePid }
                         ; term         = term
                         ; lastLogTerm  = lastLogTerm
                         ; lastLogIndex = lastLogIndex }, replyChan) ->
            match term with
            | _ when term < state.term -> return! follower state // no change in term
            | _ when term > state.term -> return! follower { state with MyState.term = term }
            | _ -> // when term = state.term 
              // If votedFor is null or candidateId, and candidate's log is at least as complete as local 
              // log (§5.2, §5.4), grant vote and reset election timeout.
              //
              // (Regarding timeouts:) Convert to candidate if election timeout elapses without either: 
              //   (SNIP) OR Granting vote to candidate 
              match state.votedFor with
              | None          ->
                replyChan.Reply { voteGranted = true; term = term }
                return! follower { state with tsClock = state.tsClock + 1I }
              | Some { pid = pid } ->
                if pid = candidatePid && logIsComplete lastLogTerm lastLogIndex then
                  replyChan.Reply { voteGranted = true; term = term } // TODO: correct term back?
                  return! follower { state with tsClock = state.tsClock + 1I } // TODO: update my term?
                else
                  replyChan.Reply { voteGranted = false; term = term } // TODO: correct term back?
                  return! follower state // TODO: update my term?

          // 1. Return if term < currentTerm (§5.1)
          | AppendEntries (req, _) when req.term < state.term ->
            return! follower state
          
          // 2. If term > currentTerm, currentTerm <- term (§5.1)
          | AppendEntries (req, replyChan) when req.term >= state.term ->
            
            // 3. If candidate (§5.2) or leader (§5.5), step down
            // 4. Reset election timeout (§5.2)
            // 5. Return failure if log doesn’t contain an entry at
            //    prevLogIndex whose term matches prevLogTerm (§5.3)
            let returnFailure () = replyChan.Reply { success = false; term = req.term }
            let state' = { state with tsClock = state.tsClock + 1I; term = req.term }

            match state.log |> Log.at req.prevLogIndex with
            | None ->
              returnFailure ()
              return! follower state'
            | Some entry when not (entry.term = req.prevLogTerm) ->
              returnFailure ()
              return! follower state'
            | Some entry ->
                // TODO: write code and possibly move to its own function shared
                // between the follower and candidate states
                //
                // 6. If existing entries conflict with new entries, delete all
                //    existing entries starting with first conflicting entry(§5.3)
                // 7. Append any new entries not already in the log 
                state.log |> Log.setAuthorative req.entries
                // 8. Apply newly committed entries to state machine (§5.3)
                // TODO
                replyChan.Reply { success = true; term = req.term }
                return! follower state'
          }

        and candidate_start state = async {

          let voteRequest chan =
            RequestVote (
              { term         = state.term
              ; candidate    = myself
              ; lastLogIndex = state.lastLogIndex
              ; lastLogTerm  = state.lastLogTerm }, chan)
          
          let quorum = System.Math.Ceiling( float state.cluster.Length / 2. ) |> int

          let emptyMap = [ (true, List.empty<VoteReply> ); (false, []) ] |> Map.ofList

          let groupByVote (grouping : Map<bool, _>) (msg : VoteReply) =
            let msgs = grouping.Item msg.voteGranted
            grouping.Remove msg.voteGranted
            |> (fun grouping -> grouping.Add (msg.voteGranted, msg :: msgs))

          let quorumForYesOrNo quorum grouping = // assume I send myself a req to vote
            let yays = grouping |> Map.find true
            let nays  = grouping |> Map.find false
            if (yays |> List.length) >= quorum then
              Some (true, yays)
            elif (nays |> List.length) >= quorum then 
              Some (false, nays)
            else
              None

          // intentionally two asyncs: first schedules a timeout, second runs election in background
          let startElection clock = async {
            do! (ElectionTimeout clock) |> schedule (now () + opts.electionTimeout)
            async {
              // reset election timeout
              // let myself vote
              let voteQualified = (pid, inbox) :: state.cluster
              let! votePassed, voteResults =
                yieldHarvest groupByVote Map.empty (quorumForYesOrNo quorum) voteQualified voteRequest
              inbox.Post <| VoteConcluded({ wasPassed = votePassed; results = voteResults }) }
            |> Async.Start }
          
          do! startElection state.tsClock
          return! candidate_running state }

        and candidate_running state = async {
        
          let forThisTerm currTerm (results : VoteReply list) = results |> List.fold (fun acc r -> acc && r.term = currTerm) true
          
          let! msg = inbox.Receive ()
          match msg with
          // While waiting for votes, a candidate may receive an AppendEntries RPC from another server claiming 
          // to be leader. If the leader’s term (included in its RPC) is at least
          // as large as the candidate’s current term, then the candidate recognizes the leader as legitimate and 
          // steps down, meaning that it returns to follower state.
          // 1. Return if term < currentTerm (§5.1)
          | AppendEntries (req, replyChan) when req.term < state.term ->
            return! candidate_running state

          | AppendEntries (req, replyChan) when req.term >= state.term ->
            // TODO: write state before next line
            replyChan.Reply { term = req.term; success = true }
            return! follower { state with tsClock = state.tsClock + 1I; term = req.term }

          | VoteConcluded { wasPassed = false; results = r }
              when r |> forThisTerm state.term ->
            return! follower { state with tsClock = state.tsClock + 1I }

          | VoteConcluded { wasPassed = true; results = r }
              when r |> forThisTerm state.term ->
            return! leader { state with tsClock = state.tsClock + 1I }

          // vote for myself now that I got opportunity
          | RequestVote (req, replyChan)
              when req.candidate = myself && req.term = state.term ->
            // TODO: write state before next line
            replyChan.Reply { term = req.term; voteGranted = true }
            return! candidate_running state

          | RequestVote (req, replyChan)
            when req.term > state.term ->
            // TODO: reply with true/false? In that case, also write state
            replyChan.Reply { term = req.term; voteGranted = true }
            return! follower { state with tsClock = state.tsClock + 1I; term = req.term; votedFor = Some req.candidate }
            
          // Election timeout elapses without election resolution: increment term, start new election 
          | ElectionTimeout clock when clock = state.tsClock ->
            return! candidate_start { state with tsClock = state.tsClock + 1I; term = state.term + 1I }
          }

        and leader state = async {
          return () }

        follower <| MyState.Empty pid log


(* optimisations:

* If desired, the protocol can be optimized to reduce the
  number of rejected AppendEntries RPCs. For example,
  when rejecting an AppendEntries request, the follower
  can include information about the term that contains the
  conflicting entry (term identifier and indexes of the first
  and last log entries for this term). With this information,
  the leader can decrement nextIndex to bypass all of the
  conflicting entries in that term; one AppendEntries RPC
  will be required for each term with conflicting entries,
  rather than one RPC per entry
*)