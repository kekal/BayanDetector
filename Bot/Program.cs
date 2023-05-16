using System.Net;
using System.Xml.Linq;
using HtmlAgilityPack;
using OpenCvSharp;
using OpenCvSharp.Flann;
using OpenCvSharp.XFeatures2D;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using File = System.IO.File;


namespace TelegramBot;

internal static class TelegramBot
{
    private static string? _cacheDir;
    private const int SomeThreshold = 80;
    private static string? _token;
    private static string? _directoryName;
    private static ITelegramBotClient? _botClient;
    private const int ThumbnailSize = 400;
    private static CancellationTokenSource? _cts;
    private const int TempChatId = 109671846;
    private const string MessageText = "It looks like similar content has already been posted here ↑";


    public static async Task Main(string[] args)
    {
        _cts = await Configuration(args);

        if (_cts == null || _botClient == null)
        {
            return;
        }

        ReceiverOptions receiverOptions = new() { AllowedUpdates = Array.Empty<UpdateType>() };

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandlePollingErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _cts.Token
        );

        Console.ReadLine();
        _cts.Cancel();
    }

    private static async Task<CancellationTokenSource?> Configuration(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            Console.WriteLine("Please provide two arguments: the token and the directory name.");
            return null;
        }

        _token = args[0];
        _directoryName = args[1];


        _cacheDir = Path.Combine(Directory.GetCurrentDirectory(), _directoryName);
        _botClient = new TelegramBotClient(_token);

        CancellationTokenSource cts = new();

        var me = await _botClient.GetMeAsync(cts.Token);

        Console.WriteLine($"Start listening for {me.Username} ({me.Id})");
        Console.WriteLine($"User: {(me.IsPremium == true ? "premium" : "")} {me.FirstName} {me.LastName}");

        Console.WriteLine($"{(me.CanJoinGroups is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} join groups.");
        Console.WriteLine($"{(me.CanReadAllGroupMessages is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} read all group messages.");

        return me.CanJoinGroups is not true || me.CanReadAllGroupMessages is not true ? null : cts;
    }

    private static async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message == null || _botClient == null)
        {
            return;
        }

        var channelDir = GetChannelInfo(message);

        if (channelDir != null)
        {
            await ElaborateAllMessageUrl(message, channelDir, cancellationToken);

            await ProcessPhoto(message, channelDir, cancellationToken);
        }
    }

    private static string? GetChannelInfo(Message message)
    {
        if (_cacheDir == null)
        {
            return null;
        }

        var channelDir = Path.Combine(_cacheDir, $"{message.Chat.Id}");
        try
        {
            Directory.CreateDirectory(channelDir);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Cannot create directory for current group: {e.Message}");
            return channelDir;
        }

        return channelDir;

    }

    private static async Task ProcessPhoto(Message message, string channelDir, CancellationToken cancellationToken)
    {
        var photo = message.Photo?.OrderBy(x => x.FileSize).Skip(1).FirstOrDefault();
        if (photo?.FileId != null && _botClient != null)
        {
            var file = await _botClient.GetFileAsync(photo.FileId, cancellationToken);

            if (file.FilePath == null)
            {
                Console.WriteLine($@"Telegram message is not well-formed. Image {file.FileId} in the message {message.MessageId} does not exists.");
                return;
            }

            var photoTempPath = Path.Combine(_cacheDir!, $"{message.Chat.Id}", Guid.NewGuid().ToString());
            await using (var fileStream = new FileStream(photoTempPath, FileMode.Create))
            {
                await _botClient.DownloadFileAsync(file.FilePath, fileStream, cancellationToken);
            }

            await ProcessStoredImage(photoTempPath, message, channelDir, cancellationToken);
        }
    }

    private static async Task SendInfo2Chat(int originalMessageId, long chatId, CancellationToken cancellationToken)
    {
        if (_botClient != null)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: MessageText,
                    replyToMessageId: originalMessageId,
                    cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private static async Task ElaborateAllMessageUrl(Message message, string channelDir,
        CancellationToken cancellationToken)
    {
        var allEntityValues = Enumerable.Empty<string>();
        if (message.Entities != null)
        {
            allEntityValues = ParseUrlInMessage(message.Entities, message.EntityValues!);
        }
        if (message.CaptionEntities != null)
        {
            allEntityValues = allEntityValues.Concat(ParseUrlInMessage(message.CaptionEntities, message.CaptionEntityValues!));
        }

        await Task
            .WhenAll(allEntityValues
                .Select(FixUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(url => ProcessSingleUrl(url, message, channelDir, cancellationToken))
                .ToList());
    }

    private static IEnumerable<string> ParseUrlInMessage(MessageEntity[] entities, IEnumerable<string> entityValues)
    {
        return entities.Select(entity => entity.Type switch
        {
            MessageEntityType.Url => entityValues.ElementAt(Array.IndexOf(entities, entity)),
            MessageEntityType.TextLink => entity.Url,
            _ => null
        }).Where(res => !string.IsNullOrWhiteSpace(res)).Distinct()!;
    }

    private static async Task ProcessSingleUrl(string? url, Message message, string channelDir, CancellationToken cancellationToken)
    {
        var originalMessageId = await GetExistingEntityMessageIdFromBd(channelDir, message, u => Uri.Compare(new Uri(u.Attribute("url")?.Value ?? @"\\uri"), new Uri(url!), UriComponents.Host | UriComponents.Path, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0);

        if (originalMessageId > 0)
        {
            await SendInfo2Chat(originalMessageId, message.Chat.Id, cancellationToken);
        }
        else
        {
            await ProcessSingleUrlContent(url, message, channelDir, cancellationToken);
        }
    }

    private static async Task ProcessSingleUrlContent(string? url, Message message, string channelDir, CancellationToken cancellationToken)
    {
        var contentType = await GetMime(url);
        if (contentType != null)
        {
            if (contentType.StartsWith("image/"))
            {
                await ProcessImageUrl(url, message, channelDir, cancellationToken);
            }

            else if (contentType.StartsWith("text/"))
            {
                var webPreview = await GetHtmlPreview(url);
                await ProcessTitle(webPreview.Title, url, message, channelDir, cancellationToken);
                await ProcessImageUrl(webPreview.ImageUrl, message, channelDir, cancellationToken);
            }
            else
            {
                await AddUrl2Bd(url, string.Empty,  message, channelDir);
            }
        }
    }

    private static async Task AddUrl2Bd(string? url, string? description, Message message, string channelDir)
    {
        var originalMessageId = await GetExistingEntityMessageIdFromBd(channelDir, message,
            u =>
                   string.Equals(u.Attribute("url")?.Value, url, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Attribute("description")?.Value, description, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Attribute("messageId")?.Value, message.MessageId.ToString(), StringComparison.OrdinalIgnoreCase));

        if (originalMessageId <= 0)
        {
            var xmlFilePath = EnsureBdExists(channelDir);

            var doc = XDocument.Load(xmlFilePath);
            var urlElement = new XElement("message",
                new XAttribute("url", url ?? string.Empty),
                new XAttribute("messageId", message.MessageId),
                new XAttribute("description", description ?? string.Empty)
            );
            doc.Root?.Add(urlElement);

            doc.Save(xmlFilePath);
        }
    }

    private static async Task<int> GetExistingEntityMessageIdFromBd(string channelDir, Message message, Func<XElement, bool> entityComparisonRule)
    {
        var xmlFilePath = EnsureBdExists(channelDir);

        var originalMessageId = 0;
        var foundSimilarMessages = XDocument.Load(xmlFilePath)
            .Descendants("message")
            .Where(u => entityComparisonRule(u) && u.Attribute("messageId")?.Value != null)
            .Select(u => int.Parse(u.Attribute("messageId")?.Value!)).ToList();

        if (foundSimilarMessages.Any())
        {
            originalMessageId = foundSimilarMessages.Min();
        }

        var chatId = message.Chat.Id;
        return await IsMessageExists(originalMessageId, chatId, channelDir) ? originalMessageId : 0;

        
    }

    private static async Task<bool> IsMessageExists(int originalMessageId, long chatId, string channelDir)
    {
        var xmlFilePath = EnsureBdExists(channelDir);
        if (string.IsNullOrWhiteSpace(xmlFilePath))
        {
            return false;
        }

        var isMessageExists = false;
        try
        {
            var tempMessageId = await _botClient!.CopyMessageAsync(TempChatId, chatId, originalMessageId);
            await _botClient!.DeleteMessageAsync(TempChatId, tempMessageId.Id);
            isMessageExists = true;
        }
        catch (ApiRequestException e)
        {
            if (e.ErrorCode != 400)
            {
                return false;
            }
        }
        if (!isMessageExists)
        {
            await RemoveFromDb(originalMessageId, channelDir, xmlFilePath);
        }
        return isMessageExists;
    }

    private static async Task RemoveFromDb(int originalMessageId, string channelDir, string xmlFilePath)
    {
        var doc = XDocument.Load(xmlFilePath);
        var elementToRemove = doc.Descendants("message").FirstOrDefault(urlElement => urlElement.Attribute("messageId")?.Value == originalMessageId.ToString());

        if (elementToRemove != null)
        {
            elementToRemove.Remove();
            await Task.Run(() =>
            {
                doc.Save(xmlFilePath);
                
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        File.Delete(Path.Combine(channelDir, $"{originalMessageId}.jpg"));
    }

    private static string EnsureBdExists(string channelDir)
    {
        var xmlFilePath = Path.Combine(channelDir, "Urls.xml");
        if (!File.Exists(xmlFilePath))
        {
            new XDocument(new XElement("message")).Save(xmlFilePath);
        }

        return xmlFilePath;
    }

    private static async Task ProcessImageUrl(string? url, Message message, string channelDir, CancellationToken cancellationToken)
    {
        var imagePath = await DownloadFileAsync(url, Path.Combine(channelDir, Guid.NewGuid().ToString()));
        var result = await ProcessStoredImage(imagePath, message, channelDir, cancellationToken);

        if (result is false)
        {
            await AddUrl2Bd(url, null, message, channelDir);
        }
    }

    private static async Task ProcessTitle(string? title, string? url, Message message, string channelDir, CancellationToken cancellationToken)
    {
        var originalMessageId = await GetExistingEntityMessageIdFromBd(channelDir, message, u => string.Equals(u.Attribute("description")?.Value, title, StringComparison.OrdinalIgnoreCase));

        if (originalMessageId > 0)
        {
            await SendInfo2Chat(originalMessageId, message.Chat.Id, cancellationToken);
        }
        else
        {
            await AddUrl2Bd(url, title, message, channelDir);
        }
    }

    private static async Task<bool?> ProcessStoredImage(string? filePath, Message message, string channelDir, CancellationToken cancellationToken)
    {
        bool? ret;
        if (filePath == null)
        {
            return null;
        }

        using (var newImage = Cv2.ImRead(filePath, ImreadModes.Color))
        {
            using var resizedImage = Downscale(newImage);
            var newImageDescriptors = GetImageDescriptors(resizedImage);

            var similarImageFile = Directory
                .EnumerateFiles(channelDir, "*.jpg")
                .OrderBy(Path.GetFileNameWithoutExtension)
                .FirstOrDefault(f =>
                {
                    var oldImage = Cv2.ImRead(f, ImreadModes.Color);
                    var oldImageDescriptors = GetImageDescriptors(oldImage);

                    var matches = CompareImages(oldImageDescriptors, newImageDescriptors);
                    return matches.Count > SomeThreshold;
                });

            similarImageFile = NormalizeFileName(similarImageFile);

            int.TryParse(Path.GetFileNameWithoutExtension(similarImageFile), out var originalMessageId);

            try
            {
                originalMessageId = await IsMessageExists(originalMessageId, message.Chat.Id, channelDir) ? originalMessageId : 0;
            }
            catch (Exception)
            {
                originalMessageId = 0;
            }


            if (originalMessageId != 0)
            {
                //var originalMessageLink = $"https://t.me/c/{chatId}/{originalMessageId}";
                await SendInfo2Chat(originalMessageId, message.Chat.Id, cancellationToken);
                ret = true;
            }
            else
            {
                var fileName = $"{message.MessageId}.jpg";
                if (File.Exists(Path.Combine(channelDir, fileName)))
                {
                    fileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}.jpg";
                }

                Cv2.ImWrite(Path.Combine(channelDir, fileName), resizedImage);
                ret = false;
            }

            resizedImage.Release();

            newImage.Release();
        }

        File.Delete(filePath);
        
        return ret;
    }

    private static string? NormalizeFileName(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var name = Path.GetFileNameWithoutExtension(path).Split('_')[0];
            var boldPath = Path.GetDirectoryName(path);
            return Path.Combine(boldPath!, $"{name}.jpg");
        }

        return path;
    }

    private static Mat Downscale(Mat image)
    {
        var scale = Math.Min((double)ThumbnailSize / image.Width, (double)ThumbnailSize / image.Height);

        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);

        var resizedImage = new Mat();
        Cv2.Resize(image, resizedImage, new Size(newWidth, newHeight));

        return resizedImage;
    }

    private static async Task<(string? Title, string? Description, string? ImageUrl)> GetHtmlPreview(string? urlEntity)
    {
        var web = new HtmlWeb();
        var doc = await web.LoadFromWebAsync(urlEntity);

        var titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']") ?? doc.DocumentNode.SelectSingleNode("//head/title");

        var descriptionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']") ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']");

        var imageUrlNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

        var title = titleNode?.Name == "meta" ? titleNode.GetAttributeValue("content", "") : titleNode?.InnerText;
        title = title != null ? WebUtility.HtmlDecode(title) : null;
        var description = descriptionNode?.GetAttributeValue("content", "");
        var imageUrl = imageUrlNode?.GetAttributeValue("content", "");

        return (title, description, imageUrl);
    }
    
    private static async Task<string?> GetMime(string? urlEntity)
    {
        using var client = new HttpClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");

        var request = new HttpRequestMessage(HttpMethod.Head, urlEntity);
        var response = await client.SendAsync(request);

        string? contentType = null;
        if (response.IsSuccessStatusCode)
        {
            contentType = response.Content.Headers.ContentType?.MediaType;
        }

        return contentType;
    }

    private static string? FixUrl(string? url)
    {
        Uri? uri = null;
        if (url != null && !Uri.TryCreate(url, UriKind.Absolute, out uri))
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            {
                url = "http://" + url;
            }

            uri = new Uri(url);
        }

        return uri?.AbsoluteUri;
    }

    private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}", _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }

    private static Mat GetImageDescriptors(Mat image)
    {
        var detector = SURF.Create(400);
        Mat descriptors = new();
        detector.DetectAndCompute(image, null, out _, descriptors);

        return descriptors;
    }

    private static List<DMatch> CompareImages(Mat descriptors1, Mat descriptors2)
    {
        var bfMatcher = new FlannBasedMatcher(new KDTreeIndexParams(2), new SearchParams());
        var matches = bfMatcher.KnnMatch(descriptors1, descriptors2, 2);

        return matches
            .Where(match => match.Length > 1 && match[0].Distance < 0.75f * match[1].Distance)
            .Select(match => match[0]).ToList();
    }

    private static async Task<string?> DownloadFileAsync(string? fileUrl, string filePath)
    {
        if (fileUrl != null)
        {
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(fileUrl);
                await using (var fileStream = new FileStream(filePath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                return Path.GetFullPath(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download image: {ex.Message}");
            }

        }

        return null;
    }
}