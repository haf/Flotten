# Flotten

A F# implementation of the algorithm described in [In Search of an
Understandable Consensus Algorithm][1].

To have a look at the implementation, look at [Raft.fs][2] which is the
implementation file.

Moving parts:

 * Each RaftActor, in Raft.fs
 * A single TimeoutService. Preferrably hosted on the same node as the
   RaftActor is.
 * The voting which is done in parallel with [Actors.yieldHarvest][3].

 [1]: https://ramcloud.stanford.edu/wiki/download/attachments/11370504/raft.pdf
 [2]: https://github.com/haf/Flotten/blob/master/Flotten/Raft.fs
 [3]: https://github.com/haf/Flotten/blob/master/Flotten/Actors.fs#L44
