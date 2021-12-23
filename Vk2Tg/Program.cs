using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using VkNet;
using VkNet.AudioBypassService.Extensions;
using VkNet.Enums;
using VkNet.Enums.Filters;
using VkNet.Enums.SafetyEnums;
using VkNet.Model;
using VkNet.Model.Attachments;
using VkNet.Model.RequestParams;
using Audio = VkNet.Model.Attachments.Audio;
using Document = VkNet.Model.Attachments.Document;
using Message = Telegram.Bot.Types.Message;
using Poll = VkNet.Model.Attachments.Poll;
using Video = VkNet.Model.Attachments.Video;

namespace Vk2Tg
{
    class Program
    {
        private static int VkGroupId { get; set; }
        private static long ChatId { get; set; }
        private static ITelegramBotClient TgClient { get; set; }
        
        private static int _requestsCount = 0;
        private static int _requestsLimit = 5;

        private static ulong _postsAmount = 0;
        private static ulong _alreadyPosted = 0;
        
        static async Task Main(string[] args)
        {
            Console.WriteLine($"[{DateTime.Now}] Start posting...");

            string tgToken = Config.Settings.TgToken;
            VkGroupId = Config.Settings.VkGroupId;
            
            var vkClient = new VkApi(new ServiceCollection().AddAudioBypass());
            vkClient.Authorize(new ApiAuthParams()
            {
                ApplicationId = Config.Settings.VkAppId,
                AccessToken = Config.Settings.VkToken,
                Settings = Settings.All
            });
            
            TgClient = new TelegramBotClient(tgToken);
            
            var chat = TgClient.GetChatAsync(new ChatId(Config.Settings.TgTargetChatName)).Result;
            ChatId = chat.Id;
            
            ulong offset = 0;
            
            var vkResponse = vkClient.Wall.Get(new WallGetParams()
            {
                OwnerId = VkGroupId,
                Count = 100,
                Offset = offset,
                Filter = WallFilter.All
            });

            offset += 100;
            
            _postsAmount = vkResponse.TotalCount;
            Console.WriteLine($"Total amount of posts: {_postsAmount}");

            List<Post> vkPosts = new List<Post>();
            vkPosts.AddRange(vkResponse.WallPosts);

            // If you don't want to start from the first post, you can use this variable
            int limitId = 0; 
            
            for (int i = 0; i < (int) (_postsAmount / 100); i++)
            {
                // Если привышает лимит - зканчиваем и удаляем те, что выше этого
                if (limitId != 0 && vkPosts.Any(x => x.Id < 9326))
                {
                    vkPosts = vkPosts.Where(x => x.Id > limitId).ToList();
                    break;
                }
                
                Thread.Sleep(500);
                
                vkResponse =  vkClient.Wall.Get(new WallGetParams()
                {
                    OwnerId = VkGroupId,
                    Count = 100,
                    Offset = offset,
                    Filter = WallFilter.All
                });
                vkPosts.AddRange(vkResponse.WallPosts);
            
                Console.WriteLine("Amount: {0} | Offset now: {1}", vkResponse.WallPosts.Count, offset);
                offset += 100;
            }

            var posts = vkPosts.OrderBy(x => x.Date).ToArray();

            foreach (var post in posts)
            {
                Console.WriteLine($"Progress: {_alreadyPosted}/{_postsAmount}");
                
                var postData = GetPostData(vkClient, post);
                var text = CreateMessage(postData);
                
                Message replyMessage;
                int replyMessageId = 0;
                
                _alreadyPosted++;
                
                if (postData.HasAttachments)
                {
                    var album = new List<IAlbumInputMedia>();
                    album.AddRange(postData.PostPhotos);
                    
                    string caption;
                    
                    // If media description is too big, it will be sent as another message
                    if (text.Length > 1024)
                    {
                        caption = "";

                        if (text.Length > 4096)
                        {
                            int limit = text.Length / 4096;
                            bool isFirst = true;
                            
                            for (int i = 0; i >= limit; i++)
                            {
                                int startIndex = i * 4096;
                                int length = 4096;

                                if (i == limit)
                                    length = text.Length - startIndex + 1;
                                
                                var shortText = text.Substring(i * 4096, length);

                                if (isFirst)
                                {
                                    isFirst = false;
                                    replyMessage = (await CreateTgPost(postData, shortText)).First();
                                    replyMessageId = replyMessage.MessageId;
                                }
                                
                                await CreateTgPost(postData, shortText, replyMessageId);
                            }
                        }
                        else
                        {
                            replyMessage = (await CreateTgPost(postData, text)).First();
                            replyMessageId = replyMessage.MessageId;    
                        }
                    }
                    else
                        caption = text;

                    if (album.Count == 0)
                    {
                        await CreateTgPost(postData, text);
                        continue;
                    }
                    
                    switch (album[^1])
                    {
                        case InputMediaAudio audio:
                            audio.Caption = caption;
                            audio.ParseMode = ParseMode.Html;
                
                            album.RemoveAt(album.Count - 1);
                            album.Add(audio);
                            break;
                        
                        case InputMediaPhoto photo:
                            photo.Caption = caption;
                            photo.ParseMode = ParseMode.Html;
                
                            album.RemoveAt(album.Count - 1);
                            album.Add(photo);
                            break;
                    }
                    
                    await CreateTgPost(postData, text, replyMessageId, album);
                    continue;
                }
                
                if (text.Length > 4096)
                {
                    int limit = text.Length / 4096;
                    bool isFirst = true;
                    
                    for (int i = 0; i >= limit; i++)
                    {
                        int startIndex = i * 4096;
                        int length = 4096;

                        if (i == limit)
                            length = text.Length - startIndex + 1;
                        
                        var shortText = text.Substring(i * 4096, length);

                        if (isFirst)
                        {
                            isFirst = false;
                            replyMessage =  (await CreateTgPost(postData, shortText)).First();
                            replyMessageId = replyMessage.MessageId;
                        }
                        
                        await CreateTgPost(postData, shortText, replyMessageId);
                    }
                }
                else
                    await CreateTgPost(postData, text);
            }

            Console.WriteLine("Done posting!");
        }

        static async Task<Message[]> CreateTgPost(PostData postData, string text = null, int replyMessageId = 0, List<IAlbumInputMedia> album = null)
        {
            Message[] messages; 
            while (true)
            {
                try
                {
                    Console.WriteLine($"{_requestsLimit - _requestsCount} requests before a recover");
                    
                    if (album != null)
                    {
                        messages = await TgClient.SendMediaGroupAsync(ChatId, album, false, replyMessageId);
                        _requestsCount++;
                    }
                    else
                    {
                        var message = await TgClient.SendTextMessageAsync(ChatId, text, ParseMode.Html);
                        _requestsCount++;
                        messages = new[] { message };
                    }
                }
                catch (ApiRequestException e)
                {
                    if (e.ErrorCode == 400)
                    {
                        var count = album?.Count ?? 0;
                        Console.WriteLine("{0}: Telegram exception (Post Url: {1} | Album size: {2}): {3}", 
                            DateTime.Now, postData.Link, count, e.Message);
                        return Array.Empty<Message>();
                    }
                    
                    Console.WriteLine("{0}: Telegram handled exception: {1}", DateTime.Now, e.Message);
                    Thread.Sleep(e.Parameters.RetryAfter * 1000);
                    messages = Array.Empty<Message>();
                }

                if (_requestsCount != _requestsLimit)
                    return messages;
                
                
                // Recovering every 5 posts to avoid telegram too many requests exception
                Console.WriteLine("Waiting for recover...");
                Thread.Sleep(40_000);
                _requestsCount = 0;

                return messages;
            }
        }

        static PostData GetPostData(VkApi vkClient, Post post)
        {
            var postData = new PostData
            {
                PostId = post.Id,
                FromId = post.FromId,
                Date = post.Date,
                Text = post.Text,
                PostAudios = new List<string>(),
                PostPhotos = new List<IAlbumInputMedia>(),
                PostVideos = new List<string>(),
                PostGifs = new List<string>(),
                Link = $"https://vk.com/wall-131682518_{post.Id}"
            };

            if (postData.FromId.HasValue && postData.FromId != VkGroupId)
            {
                Thread.Sleep(500);
                var user = vkClient.Users.Get(new List<long> {postData.FromId.Value},
                    ProfileFields.FirstName | ProfileFields.LastName, NameCase.Nom).First();
                postData.AuthorName = $"{user.FirstName} {user.LastName}";
            }
            
            if (post.Attachments == null)
                return postData;
            
            foreach (var attachment in post.Attachments)
            {
                HttpWebRequest webRequest;
                WebResponse webResponse;
                Stream stream;
                
                switch (attachment.Instance)
                {
                    case Document document:
                        postData.HasAttachments = true;
                        
                        if (document.Type != DocumentTypeEnum.Gif) 
                            break;
                        
                        if (document.Uri == null)
                            break;
                        
                        postData.PostGifs.Add(document.Uri);
                        break;
                    
                    case Photo photo:
                        postData.HasAttachments = true;

                        var size = photo.Sizes.OrderBy(x => x.Height).Last();
                        
                        webRequest = (HttpWebRequest) WebRequest.Create(size.Url);
                        webRequest.AllowWriteStreamBuffering = true;
                        webRequest.Timeout = 30000;
                        try
                        {
                            webResponse = webRequest.GetResponse();
                            stream = webResponse.GetResponseStream();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Got a web exception in photos: {0}", e.Message);
                            postData.HasAttachments = false;
                            break;
                        }
                        var image = new InputMediaPhoto(new InputMedia(stream, $"{photo.Id}.jpg"));
                        
                        postData.PostPhotos.Add(image);
                        break;
                    
                    // Maybe will add audio downloading later
                    case Audio audio:
                        if (audio == null)
                            break;
                        
                        string track = "";

                        if (audio.Url == null)
                        {
                            track = $"{audio.Artist} - {audio.Title}";
                            postData.PostAudios.Add(track);
                            break;
                        }
                        
                        string audioUrl = Regex.Replace(
                            audio.Url.ToString(),
                            @"/[a-zA-Z\d]{6,}(/.*?[a-zA-Z\d]+?)/index.m3u8()",
                            @"$1$2.mp3"
                        );
                        
                        track = $"<a href=\"{audioUrl}\">🎵 {audio.Artist} - {audio.Title}</a>";
                        postData.PostAudios.Add(track);
                        break;
                    
                    case Video video:
                        string url; 
                        
                        if (video.Player != null)
                            url = video.Player.ToString();
                        else if (video.Files != null)
                            url = video.Files.Mp4_1080.ToString();
                        else
                            url = $"https://vk.com/video{video.OwnerId}_{video.Id}";
    
                        postData.PostVideos.Add(url);
                        break;
                    
                    case VkNet.Model.Attachments.Poll poll:
                        postData.HasPoll = true;
                        postData.Poll = new Poll {Subject = poll.Question};
                        var results = new Dictionary<string, int>();
                        
                        foreach (var answer in poll.Answers)
                            results.Add(answer.Text, answer.Votes ?? 0);

                        postData.Poll.Results = results;
                        break;
                }
            }

            return postData;
        }
        
        static string CreateMessage(PostData postData)
        {
            string res = $"<i>{postData.Date}</i>\n"; // $"Post ID: {post.Id}\nDate: {post.Date}\n";
    
            if (!string.IsNullOrEmpty(postData.AuthorName))
                res += $"<b>{postData.AuthorName}</b>\n";

            if (!string.IsNullOrEmpty(postData.Text))
                res += $"\n{postData.Text}\n";

            if (postData.HasPoll)
            {
                res += $"\n<b>Опрос</b>\n{postData.Poll.Subject}\n";
                foreach (var result in postData.Poll.Results)
                    res += $"{result.Key} | {result.Value}\n";
            }
            
            int count;

            if (postData.PostVideos.Any())
            {
                postData.HasAttachments = true;

                count = 1;
                foreach (var video in postData.PostVideos)
                {
                    res += $"Видео {count}: {video}\n";
                    count++;
                }
            }

            if (postData.PostAudios.Any())
            {
                postData.HasAttachments = true;
                
                foreach (var audio in postData.PostAudios)
                    res += $"{audio}\n";
            }

            if (postData.PostGifs.Any())
            {
                postData.HasAttachments = true;
                
                count = 1;
                foreach (var gif in postData.PostGifs)
                {
                    res += $"<a href=\"{gif}\">Гифка {count}</a>\n";
                    count++;
                }
            }

            res += $"\nСсылка: {postData.Link}";
            return res;
        }
    }

    class PostData
    {
        public long? PostId { get; set; }
        public long? FromId { get; set; }
        // Если FromId != 0
        public string AuthorName { get; set; }
        public DateTime? Date { get; set; }
        public string Text { get; set; }
        public List<IAlbumInputMedia> PostPhotos { get; set; }
        public List<string> PostAudios { get; set; }
        public List<string> PostVideos { get; set; }
        public List<string> PostGifs { get; set; }
        public bool HasPoll { get; set; }
        public Poll Poll { get; set; }
        public bool HasAttachments { get; set; }
        // Ссылка на пост в ВК
        public string Link { get; set; }
    }

    class Poll
    {
        public string Subject { get; set; }
        public Dictionary<string, int> Results { get; set; }
    }
}