﻿namespace FsRaft 

[<AutoOpen>]
module Heartbeat =
    open System
    open FSharpx
    open Persistence

    type Heartbeat<'T> (leaderId, ep : Endpoint, send : Endpoint -> RaftProtocol -> Async<RaftProtocol>, add : Endpoint -> RaftProtocol -> unit, initial : RaftState<'T>, log) =
        let peerId, _, _ = ep
        //do debug "new heartbeat: %A" (ep)
        let rpc state =
            async {
                let v = Lenses.configPeer ep |> Lens.get state
                let v = v.Value //TODO
                assert (v.NextIndex <= state.Log.NextIndex)
                let! reply = 
                    send ep <|
                        AppendEntriesRpc
                            { Term = state.Term.Current 
                              LeaderId = leaderId
                              PrevLogTermIndex = Log.termIndex state.Log (v.NextIndex - 1) //TODO this can go below 0!!
                              Entries = Log.query state.Log (Range (v.NextIndex - 1, v.NextIndex + 4)) |> Seq.toList
                              LeaderCommit = state.CommitIndex } 
                return add ep reply }
        
        let agent = FSharp.Control.AutoCancelAgent.Start (fun inbox ->

            let rec loop state = async {
                let! msg = inbox.TryReceive RaftConstants.heartbeat
                match msg with
                | Some s ->
                    do! rpc s
                    return! loop s
                | None ->
                    do! rpc state
                    return! loop state }
            loop initial)

        do agent.Error.Add raise

        member __.State state = 
           agent.Post state
           state

        interface IDisposable with 
            member __.Dispose () =
                log (Debug (sprintf "raft :: %s leader: heartbeat disposing" (short peerId)))
                dispose agent


    type internal Protocol<'T> =
        | State of RaftState<'T>
        | Dispose of AsyncReplyChannel<unit>


    type HeartbeatSuper<'T> (ep, send, add, initial : RaftState<'T>, log) =
        let id,_,_ = ep
        let updatePeers state current =
            let peers =
                Lenses.configPeers |> Lens.get state
                |> Map.filter (fun k _ -> k <> ep) // don't send a message to self

            let removed = Map.difference current peers
            removed |> Map.values |> List.iter dispose
            
            let current' = Map.difference current removed
            
            let added = 
                Map.difference peers current'
                |> Map.map (fun k _ -> 
                    new Heartbeat<'T>(ep, k, send, add, state, log)) 
            
            Map.merge added current'

        let agent = FSharp.Control.AutoCancelAgent.Start (fun inbox ->

            // optimisation to avoid a full IStructuralEquitable comparison
            let changed (s1 : RaftState<'T>) (s2 : RaftState<'T>) =
                s1.CommitIndex <> s2.CommitIndex
                || s1.Log.NextIndex <> s2.Log.NextIndex
                || s1.Term <> s2.Term
                || s1.Config <> s2.Config

            let rec loop state (peers : Map<Endpoint, Heartbeat<'T>>) = 
                async {
                    let! msg = inbox.Receive() 
                    match msg with
                    | State s when changed s state ->
                        let peers = updatePeers s peers
                        // distribute update state to all peers
                        peers
                        |> Map.iter (fun _ v -> v.State s |> ignore)
                        return! loop s peers
                    | Dispose rc ->
                        peers |> Map.values |> List.iter dispose
                        rc.Reply ()
                    | _ ->
                        return! loop state peers }
            loop initial Map.empty<Endpoint, Heartbeat<'T>>)

        member __.State state = 
           agent.Post (State state)
           state

        interface IDisposable with 
            member __.Dispose () =
                log (Debug (sprintf "raft :: %s leader: heartbeat supervisor disposing" (short id)))
                agent.PostAndReply (fun rc ->  Dispose rc)
                dispose agent
