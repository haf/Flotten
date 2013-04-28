# Flotten

A F# implementation of the algorithm described in [In Search of an
Understandable Consensus Algorithm][1]. Flotten is swedish for raft. The paper
is authored by Diego Ongaro and John Ousterhout from Stanford University.

To have a look at the implementation, look at [Raft.fs][2] which is the
algorithm implementation file.

## Description of the Algorithm (paper abstract)

Raft is a consensus algorithm for managing a replicated log. It produces a result
equivalent to Paxos, and it is as efficient as Paxos, but its structure is
different from Paxos; this makes Raft more understandable than Paxos and also
provides a better foundation for building practical systems. In order to
enhance understandability, Raft separates the key elements of consensus, such as
leader election and log replication, and it enforces a stronger degree of
coherency to reduce the number of states that must be considered. Raft also
includes a new mechanism for changing the cluster membership, which uses
overlapping majorities to guarantee safety. Results from a user study
demonstrate that Raft is easier for students to learn than Paxos.

### Current Implementation Status

Very raw. When the TODOs have been scrubbed it can be considered pre-alpha. I
also plan to do some work to allow remote communication between actors as to
make the whole thing more realistic.

Large TODOs:

 * The Log
 * Complete AppendEntries for all states
 * Complete leader state and all that the leader state means.

### Moving parts:

 * Each RaftActor, in Raft.fs
 * A single TimeoutService. Preferrably hosted on the same node as the
   RaftActor is.
 * The voting which is done in parallel with [Actors.yieldHarvest][3].

## The MIT License

Copyright (c) 2013 Henrik Feldt

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

 [1]: https://ramcloud.stanford.edu/wiki/download/attachments/11370504/raft.pdf
 [2]: https://github.com/haf/Flotten/blob/master/Flotten/Raft.fs
 [3]: https://github.com/haf/Flotten/blob/master/Flotten/Actors.fs#L44
