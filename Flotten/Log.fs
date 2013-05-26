module Log

/// A logical clock, could possibly be replaced with a Interval Tree Clock
type Clock = bigint

type Term = bigint

type Command  = string

type LogInstance =
  { log         : LogEntry list
  ; commitIndex : Index }

and LogEntry =
  /// Each log entry has an integer index identifying its position in the log.
  { position : Index
  /// Each log entry stores a state machine command along with the term number when the entry was received by the leader 
  ; command  : Command
  /// The term numbers in log entries are used to detect inconsistencies between logs and to ensure the Raft safety property
  /// as described in Section 5.4.
  ; term     : Term }
/// Each log entry has an integer index identifying its position in the log.
and Index = System.UInt32
  
type LogDelta = LogEntry list
  
/// Get the log entry at the given index
let at (index : Index) (instance : LogInstance) = 
  { position = index
  ; command  = "noop"
  ; term     = 1I } |> Some

let setAuthorative (delta : LogDelta) (instance : LogInstance) =
  ()

let append (cmd : Command) (t : Term) (l : LogInstance) =
  { l with log = { position = l.log.Length |> uint32
                 ; command = cmd
                 ; term    = t
                 } :: l.log
           ; commitIndex = l.commitIndex + 1u }