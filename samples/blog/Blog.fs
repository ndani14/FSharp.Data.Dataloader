module Tests
open System
open FSharp.Data.Dataloader


// === An API ===

type PostId = int
type PostContent = string
type Enviroment = {
    PostIds : PostId list
    PostContents : Map<PostId, PostContent>
}

let env = {
    PostIds = [1;2]
    PostContents = Map.ofList [1, "Test1"; 2, "Test2"]
}

// The original HAXL library relies HEAVYILY on GADTS for request types
// This presents some problems for F#, as we don't have that great of a way of 
// expressing that.

type BlogRequest =
    | FetchPosts
    | FetchPostContent of PostId
    interface Request with
        member x.Identifier =
            match x with
            | FetchPosts -> "FetchPosts"
            | FetchPostContent id -> "FetchContent id = " + id.ToString()
        

type BlogDataSource() = 
    interface DataSource<BlogRequest> with
        member x.Name = "Blog"
        member x.FetchFn (blocked: BlockedFetch<BlogRequest> list) = 
            // This is where you could do batching, concurrency, asynchronicity, etc
            blocked
            |> List.iter(fun b ->
                match b.Request with
                | FetchPosts -> 
                    let ids = env.PostIds
                    b.Status := FetchSuccess(box ids)
                | FetchPostContent id ->
                    let content = Map.tryFind id env.PostContents
                    b.Status := FetchSuccess(box content))
            |> SyncFetch

[<EntryPoint>]
let main args = 
    printfn "Starting Fetch!"
    let source = BlogDataSource()
    let fetchPostIds = Fetch.dataFetch<PostId list, BlogRequest> source (FetchPosts)
    let fetchPostContent id = Fetch.dataFetch<PostContent option, BlogRequest> source (FetchPostContent id)
    let contents =
        fetchPostIds
        |> Fetch.bind(Fetch.mapSeq fetchPostContent)
    let res =  Fetch.runFetch contents
    printfn "Results: %A" res
    0