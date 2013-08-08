namespace FSharp.Actor

open System
open FSharp.Actor

type TrieBasedRegistry() =
    
    let actorTrie : Trie.trie<string, ActorRef> ref = ref Trie.empty

    let computeKeys (inp:ActorPath) = 
        let segs = inp.Actor.Split([|'/'|], StringSplitOptions.RemoveEmptyEntries)
        segs |> Array.map (fun x -> x.Replace(":", "")) |> List.ofArray
    
    let findAll address = 
        Trie.subtrie (computeKeys address) !actorTrie |> Trie.values 

    let find address = 
        match findAll address with
        | [] -> raise(ActorNotFound(sprintf "Could not find actor %A" address))
        | a -> a |> List.head

    let tryFind address = 
        match findAll address with
        | [] -> None
        | a -> a |> List.head |> Some

    let register (actor:ActorRef) = 
        actorTrie := Trie.add (computeKeys actor.Path) actor !actorTrie
    
    let unregister (actor:ActorRef) = 
        actorTrie := Trie.remove (computeKeys actor.Path) !actorTrie

    interface IRegister with
        member x.Register(ref) = register(ref)
        member x.Remove(ref) = unregister(ref)
        member x.Resolve(path) = find(path)
        member x.TryResolve(path) = tryFind(path)
        member x.ResolveAll(path) = findAll(path) |> Seq.ofList
        member x.Dispose() = 
                actorTrie := Trie.empty

