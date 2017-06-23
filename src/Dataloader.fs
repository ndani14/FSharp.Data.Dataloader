module FSharp.Data.Dataloader

open System
open System.Reflection
open System.Collections.Concurrent
/// Represents an instruction to the system on how to fetch a value of type 'a

/// Type encapsulating the delayed resolution of a Result
type Fetch<'a> = { unFetch : DataCache ref -> RequestStore ref -> Result<'a> }

/// Type representing the result of a data fetch
/// Done represents a completed fetch with value of type 'a
and Result<'a> =
    | Done of 'a
    | Blocked of BlockedRequest list * Fetch<'a>
    | FailedWith of exn

/// Type representing the status of a blocked request
and FetchStatus<'a> =
    | NotFetched
    | FetchSuccess of 'a
    | FetchError of exn

/// Result type of a fetch
/// If the result type is asynchronous then all results will be batched
and PerformFetch =
    | SyncFetch of unit
    | AsyncFetch of Async<unit>

/// Represents an untyped Request, used to power the caching side
and Request = 
    abstract Identifier : string

/// Untyped version of Datasource, used primarily for heterogenous caches
and DataSource = 
    abstract Name : string
    abstract FetchFn : (BlockedRequest list -> PerformFetch)

/// A source of data that will be fetched from, with Request type 'r
and DataSource<'r when 'r :> Request> =
    /// Applied for every blocked request in a given round
    abstract FetchFn : BlockedFetch<'r> list -> PerformFetch
    /// Used to uniquely identify the datasource
    abstract Name : string

/// Metadata for a blocked request
and BlockedFetch<'r> = {
    Request: 'r
    Status: FetchStatus<obj> ref
}

/// Untyped version of BlockedFetch, used primarily for our cache
and BlockedRequest = {
    Request: obj
    Status: FetchStatus<obj> ref
}

/// When a reques
and DataCache = ConcurrentDictionary<string, FetchStatus<obj> ref>

/// When a request is issued by the client via a 'dataFetch',
/// It is placed in the RequestStore. When we are ready to fetch a batch of requests,
/// 'performFetch' is called
and RequestStore = ConcurrentDictionary<string, DataSource * BlockedRequest list>



[<RequireQualifiedAccess>]
module internal DataCache =
    let empty (): DataCache = new ConcurrentDictionary<string, FetchStatus<obj> ref>()
    let add (r: Request) (status: FetchStatus<obj> ref) (c: DataCache): DataCache =
        c.AddOrUpdate(r.Identifier, status, (fun _ _ -> status)) |> ignore
        c
    
    let get (r: Request) (c: DataCache) =
        match c.TryGetValue(r.Identifier) with
        | true, status -> 
            Some status
        | false, _ -> 
            None

[<RequireQualifiedAccess>]
module internal RequestStore =
    let empty (): RequestStore = new ConcurrentDictionary<string, DataSource * BlockedRequest list>()
    let addRequest (r: BlockedRequest) (source: DataSource) (store: RequestStore) =
        store.AddOrUpdate(source.Name, (source, [r]), (fun _ (_, requests) -> (source, r::requests))) |> ignore
        store
    
    let resolve (fn: DataSource -> BlockedRequest list -> PerformFetch) (store: RequestStore) =
        let asyncs = 
            store
            |> Seq.fold(fun acc (KeyValue(_, (s, b))) -> 
                match fn s b with
                | SyncFetch _ -> acc
                | AsyncFetch a -> a::acc) [] 
        asyncs |> Async.Parallel |> Async.RunSynchronously |> ignore

[<RequireQualifiedAccess>]
module Fetch =
    let lift a = { unFetch = fun cache req -> Done(a)}
    /// Applies a mapping function f to the inner value of a
    let rec map f a =
        let unFetch = fun cacheRef storeRef ->
            match a.unFetch cacheRef storeRef with
            | Done x -> Done(f x)
            | Blocked (br, c) -> Blocked(br, (map f c))
            | FailedWith exn -> FailedWith exn
        { unFetch = unFetch }

    /// Applies some wrapped function f to the inner value of a
    let rec applyTo (a: Fetch<'a>) (f: Fetch<('a -> 'b)>) =
        let unFetch = fun cacheRef storeRef ->
            match a.unFetch cacheRef storeRef, f.unFetch cacheRef storeRef with
            | Done a', Done f' -> Done(f' a')
            | Done a', Blocked(br, f') -> Blocked(br, applyTo { unFetch = fun cacheRef storeRef -> Done a' } f')
            | Blocked(br, a'), Done(f') -> Blocked(br, map f' a')
            | Blocked(br1, a'), Blocked(br2, f') -> Blocked(br1@br2, applyTo  a' f')
            | FailedWith exn, _ -> FailedWith exn
            | _, FailedWith exn -> FailedWith exn
        { unFetch = unFetch }
    
    /// Applies some binding function f to the inner value of a
    let rec bind f a =
        let unFetch = fun cacheRef storeRef ->
            match a.unFetch cacheRef storeRef with
            | Done x -> (f x).unFetch cacheRef storeRef
            | Blocked(br, x) -> Blocked(br, bind f x)
            | FailedWith exn -> FailedWith exn
        { unFetch = unFetch }
    
    /// Applies a bind function to a sequence of values, production a fetch of a sequence
    let mapSeq (f: 'a -> Fetch<'b>) (a: seq<'a>) =
        let cons (x: 'a) ys = (f x) |> map(fun v -> Seq.append [v]) |> applyTo ys
        Seq.foldBack cons a (lift Seq.empty)
    
    
    
    /// Transforms a request into a fetch operation
    let dataFetch<'a, 'r when 'r :> Request> (d: DataSource<'r>) (a: Request): Fetch<'a> =
        let cont (statusRef: FetchStatus<obj> ref) = 
            let unFetch cacheRef storeRef = 
                let status = !statusRef 
                match status with
                | FetchSuccess s -> Done(s :?> 'a)
                | _ -> FailedWith (Failure "Expected Complete Fetch!")
            { unFetch = unFetch }
        let unFetch cacheRef storeRef =
            let cache = !cacheRef
            // Do a lookup in the cache to see if we need to return 
            match DataCache.get a cache with
            | Some statusRef ->
                match !statusRef with
                | FetchSuccess (v:obj) ->
                    // We've seen the request before, and it is completed, so return the value
                    Done(v :?> 'a)
                | NotFetched ->
                    // We've seen the request before, but it is blocked, but we dont add the request to the RequestStore
                    Blocked([], cont statusRef)
                | FetchError ex ->
                    // There was an error, so add the failure as our result
                    FailedWith ex
            | None -> 
                // We haven't seen the request before, so add it to the request store so it will be fetched in the next round
                let statusRef = ref NotFetched
                let blockedReq = {Request = box a; Status = statusRef}
                // Update the cache and store references
                let datasource = 
                    { new DataSource with 
                        member x.Name = d.Name
                        member x.FetchFn = List.map(fun b -> { BlockedFetch.Request = b.Request :?> 'r; Status = b.Status}) >> d.FetchFn
                    }
                DataCache.add a statusRef cache |> ignore
                RequestStore.addRequest blockedReq datasource !storeRef |> ignore
                Blocked([blockedReq], cont statusRef)
        { unFetch = unFetch }
    
    /// Issues a batch of fetches to the request store. After 
    /// 'performFetchs' is complete, all of the BlockedRequests status refs are full
    let performFetches (store: RequestStore) =
        RequestStore.resolve(fun source blocked -> source.FetchFn blocked) store



    /// Executes a fetch using fn to resolve the blocked requests
    /// Fn should fill in the reference value of the BlockedRequest
    let runFetch fetch =
        let storeRef = ref (RequestStore.empty ()) 
        let cacheRef = ref (DataCache.empty ())
        let rec helper f =
            match fetch.unFetch cacheRef storeRef with
            | Done a -> a
            | Blocked(br, cont) ->
                performFetches (!storeRef)
                // Clear out the request cache
                storeRef := RequestStore.empty()
                helper cont
            | FailedWith ex -> raise ex
        helper fetch

