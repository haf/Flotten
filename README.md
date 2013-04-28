# Flotten

A F# implementation of the algorithm described in [In Search of an
Understandable Consensus Algorithm][1]. Flotten is swedish for raft.

To have a look at the implementation, look at [Raft.fs][2] which is the
implementation file.

Moving parts:

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
