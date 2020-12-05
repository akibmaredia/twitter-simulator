#r "nuget: Akka.FSharp"
#r "nuget: Akka.Remote"

#load @"./MessageTypes.fsx"

open Akka.Actor
open Akka.FSharp
open Akka.Remote
open Akka.Configuration

open System
open System.Collections.Generic
open System.Text.RegularExpressions

open Microsoft.FSharp.Core

open MessageTypes

type BootServer = {
    BootMessage: string;
}

type ShutdownServer = {
    ShutdownMessage: string;
}

type Tweet = {
    Id: int;
    Content: string;
    PostedBy: int;
}

type User = {
    Id: int;
    Handle: string;
    FirstName: string;
    LastName: string;
    Tweets: List<int>;
    Followers: HashSet<int>;
    FollowingTo: HashSet<int>;
    ActorRef: IActorRef;
}

type TweetRef = { 
    TweetId: int;
    NextTweet: TweetRef option; 
}

// Remote Configuration
let configuration = 
    ConfigurationFactory.ParseString(
        @"akka {
            actor.provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
            remote.helios.tcp {
                port = 5555
                hostname = localhost
            }
        }")

let system = ActorSystem.Create("Twitter", configuration)

type Server() =
    inherit Actor()
    // User Id -> User Instance mapping
    let users = new Dictionary<int, User>()

    // User Id -> UserStatus (online / offline) mapping
    let userStatus = new Dictionary<int, bool>()

    // Tweet Id -> Tweet Instance mapping
    let tweets = new Dictionary<int, Tweet>()

    // User Handle -> User Id mapping
    let handles = new Dictionary<string, int>()

    // Hashtag -> List<Tweet Id> mapping, for searching tweets having a specific hashtag
    let hashtags = new Dictionary<string, List<int>>()

    // User Id -> List<Tweet Id> mapping, for searching tweets where user is mentioned
    let mentions = new Dictionary<int, List<int>>()

    // Regex patterns
    let mentionPattern = @"@\w+"
    let hashtagPattern = @"#\w+"

    let findAllMatches (text: string, regex: string, sep: string) = 
        let ans = new HashSet<string>()
        let matches = Regex.Matches(text, regex)
        for m in matches do
            ans.Add(m.Value) |> ignore
        ans

    override x.OnReceive (message: obj) =   
        match message with 
        | :? BootServer as bootInfo -> 
            printfn "%s" bootInfo.BootMessage
        | :? RegisterUserRequest as request ->
            try 
                let user: User = {
                    Id = (request.Handle.[4..] |> int);
                    Handle = request.Handle;
                    FirstName = request.FirstName;
                    LastName = request.LastName;
                    Tweets = new List<int>();
                    Followers = new HashSet<int>();
                    FollowingTo = new HashSet<int>();
                    ActorRef = request.ActorRef;
                }
                
                users.Add((user.Id, user))
                users.[user.Id].FollowingTo.Add(user.Id) |> ignore
                users.[user.Id].Followers.Add(user.Id) |> ignore

                handles.Add((user.Handle, user.Id))

                userStatus.Add((user.Id, true))
                
                let response: RegisterUserResponse = {
                    Id = user.Id;
                    Handle = user.Handle;
                    FirstName = user.FirstName;
                    LastName = user.LastName;
                    Success = true;
                }
                x.Sender.Tell response
            with
                :? System.ArgumentException -> 
                    let response: RegisterUserResponse = {
                        Id = -1;
                        Handle = request.Handle;
                        FirstName = "";
                        LastName = "";
                        Success = false;
                    }
                    x.Sender.Tell response
        | :? FollowUserRequest as request -> 
            users.[request.FollowerId].FollowingTo.Add(request.FolloweeId) |> ignore
            users.[request.FolloweeId].Followers.Add(request.FollowerId) |> ignore
            let response: FollowUserResponse = { Success = true; }
            x.Sender.Tell response
        | :? UnfollowUserRequest as request ->
            users.[request.FollowerId].FollowingTo.Remove(request.FolloweeId) |> ignore
            users.[request.FolloweeId].Followers.Remove(request.FollowerId) |> ignore
            let response: UnfollowUserResponse = { Success = true; }
            x.Sender.Tell response
        | :? PostTweetRequest as request -> 
            let tweet: Tweet = {
                Id = tweets.Count + 1;
                Content = request.Content;
                PostedBy = request.UserId;
            }
            tweets.Add((tweet.Id, tweet))

            users.[request.UserId].Tweets.Add(tweet.Id) |> ignore

            let tweetMentions = findAllMatches(request.Content, mentionPattern, "@")
            for mention in tweetMentions do
                if handles.ContainsKey mention then
                    let userId = handles.[mention]
                    
                    if userStatus.[userId] then
                        let tweet: TweetData = { 
                            Id = tweet.Id; 
                            Content = request.Content; 
                            PostedBy = users.[request.UserId].Handle;
                            PostedById = request.UserId; 
                        }
                        let updateFeedMsg: UpdateFeedResponse = { Tweet = tweet; }
                        users.[userId].ActorRef.Tell updateFeedMsg
                    
                    if mentions.ContainsKey userId then
                        mentions.[userId].Add(tweet.Id)
                    else 
                        let tweetIdList = new List<int>()
                        tweetIdList.Add(tweet.Id)
                        mentions.Add((userId, tweetIdList))

            let tweetHashtags = findAllMatches(request.Content, hashtagPattern, "#")
            for tag in tweetHashtags do
                if hashtags.ContainsKey tag then
                    hashtags.[tag].Add(tweet.Id)
                else 
                    let tweetIdList = new List<int>()
                    tweetIdList.Add(tweet.Id)
                    hashtags.Add((tag, tweetIdList))

            let followers = users.[request.UserId].Followers
            for follower in followers do 
                if userStatus.[follower] then
                    let tweet: TweetData = { 
                        Id = tweet.Id; 
                        Content = request.Content; 
                        PostedBy = users.[request.UserId].Handle; 
                        PostedById = request.UserId; 
                    }
                    let updateFeedMsg: UpdateFeedResponse = { Tweet = tweet; }
                    users.[follower].ActorRef.Tell updateFeedMsg

            let tweetSuccess = tweets.ContainsKey(tweet.Id)
            let response: PostTweetResponse = {
                UserId = request.UserId;
                TweetId = tweet.Id;
                Content = request.Content;
                Success = tweetSuccess;
            }
            x.Sender.Tell response
        | :? RetweetRequest as request -> 
            // Send tweet update to this user's follower
            let followers = users.[request.UserId].Followers
            for follower in followers do 
                if userStatus.[follower] then
                    let tweet: TweetData = { 
                        Id = request.TweetId; 
                        Content = tweets.[request.TweetId].Content;
                        PostedBy = users.[request.OriginalUserId].Handle; 
                        PostedById = request.OriginalUserId;
                    }
                    let updateFeedMsg: UpdateFeedResponse = { Tweet = tweet; }
                    users.[follower].ActorRef.Tell updateFeedMsg

            let response: RetweetResponse = { Success = true; }
            x.Sender.Tell response
        | :? UpdateUserStatusRequest as request -> 
            userStatus.[request.UserId] <- request.IsOnline
        | :? PrintInfo as request -> 
            let user = users.[request.Id]
            printfn "%d | %s | %s | %s | %d | %d" user.Id user.Handle user.FirstName user.LastName user.Followers.Count user.FollowingTo.Count
        | _ -> ()

let server = system.ActorOf(Props(typedefof<Server>), "Server")

let (task:Async<ShutdownServer>) = (server <? { BootMessage = "Server is running!"; })

let response = Async.RunSynchronously (task)
printfn "%A" response

server.Tell(PoisonPill.Instance);
