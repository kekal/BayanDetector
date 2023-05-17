using System.Net;
using System.Text;
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
using Size = OpenCvSharp.Size;


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

    /// <summary> Configures the bot with the given token and directory name. </summary>
    /// <param name="args">The token and cache directory name.</param>
    /// <returns>A CancellationTokenSource for future operations if the bot can join groups and read all group messages, otherwise null.</returns>
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

        Console.WriteLine("\nCurrent cache directory: " + Path.GetFullPath(_cacheDir));

        _botClient = new TelegramBotClient(_token);

        CancellationTokenSource cts = new();

        var me = await _botClient.GetMeAsync(cts.Token);
        Console.WriteLine($"Start listening for {me.Username} ({me.Id})");
        Console.WriteLine($"User: {(me.IsPremium == true ? "premium" : "")} {me.FirstName} {me.LastName}");

        Console.WriteLine($"{(me.CanJoinGroups is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} join groups.");
        Console.WriteLine($"{(me.CanReadAllGroupMessages is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} read all group messages.");

        return me.CanJoinGroups is not true || me.CanReadAllGroupMessages is not true ? null : cts;
    }

    /// <summary> Handles an update from Telegram, elaborating all message URLs and processing photos. </summary>
    /// <param name="_">The Telegram Bot Client. Not used.</param>
    /// <param name="update">The update event from Telegram.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    private static async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        try
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
        catch (Exception e)
        {
            Console.WriteLine(FormatException(e));
        }
    }

    /// <summary> Processes a photo from a Telegram message and stores it in the specified directory. </summary>
    /// <param name="message">The Telegram message containing the photo.</param>
    /// <param name="channelDir">The directory to store the photo.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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

    /// <summary> Sends a text message to a chat using the bot client. </summary>
    /// <param name="originalMessageId">The ID of the message that will be pointed as original content.</param>
    /// <param name="chatId">The ID of the chat.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="count"></param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async Task SendInfo2Chat(int originalMessageId, long chatId, CancellationToken cancellationToken, int count = 0)
    {
        if (_botClient != null)
        {
            try
            {
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: count <= 0 ? MessageText : $"{MessageText}\nImage similarity: {count}/{SomeThreshold}",
                    replyToMessageId: originalMessageId,
                    cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    /// <summary>  Elaborates all message URLs by parsing their text, fixing the unformed URLs, and processing each URL further. </summary>
    /// <param name="message">The message to be processed.</param>
    /// <param name="channelDir">The directory dedicated to the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async Task ElaborateAllMessageUrl(Message message, string channelDir, CancellationToken cancellationToken)
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

    /// <summary> Processes a single URL, either by sending an existing message with original content or by processing the new content from the URL. </summary>
    /// <param name="url">The URL to process.</param>
    /// <param name="message">The message containing the URL.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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

    /// <summary> Processes a URL downloaded content by determining the content type and processing the URL accordingly. </summary>
    /// <param name="url">The URL to process.</param>
    /// <param name="message">The message associated with the URL.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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
                await AddUrl2Bd(url, string.Empty, message, channelDir);
            }
        }
    }

    /// <summary> Adds a URL to the database if it does not already exist with its message ID and title. </summary>
    /// <param name="url">The URL to add.</param>
    /// <param name="description">The description of the URL.</param>
    /// <param name="message">The message containing the URL.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static async Task AddUrl2Bd(string? url, string? description, Message message, string channelDir)
    {
        var originalMessageId = await GetExistingEntityMessageIdFromBd(channelDir, message, u =>
                   string.Equals(u.Attribute("url")?.Value, url, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Attribute("description")?.Value, description, StringComparison.OrdinalIgnoreCase)
                || string.Equals(u.Attribute("messageId")?.Value, message.MessageId.ToString(), StringComparison.OrdinalIgnoreCase));

        if (originalMessageId <= 0)
        {
            var xmlFilePath = EnsureBdExists(channelDir);
            lock (_botClient!)
            {
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
    }

    /// <summary> Gets the existing entity message identifier from the database and checks if this message still exists. </summary>
    /// <param name="channelDir">The channel directory.</param>
    /// <param name="message">The message.</param>
    /// <param name="entityComparisonRule">The xml records comparison predicate.</param>
    /// <returns>The existing entity message identifier.</returns>
    private static async Task<int> GetExistingEntityMessageIdFromBd(string channelDir, Message message, Func<XElement, bool> entityComparisonRule)
    {
        var xmlFilePath = EnsureBdExists(channelDir);

        var originalMessageId = 0;
        XDocument xDocument;
        lock (_botClient!)
        {
            xDocument = XDocument.Load(xmlFilePath);
        }

        var foundSimilarMessages = xDocument
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

    /// <summary> Checks if a message exists in a chat. </summary>
    /// <param name="originalMessageId">The original message id.</param>
    /// <param name="chatId">The chat id.</param>
    /// <param name="channelDir">The channel directory.</param>
    /// <returns> A Boolean value indicating whether the message exists. </returns>
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
            RemoveFromDb(originalMessageId, channelDir, xmlFilePath);
        }
        return isMessageExists;
    }

    /// <summary> Removes a message from the database and deletes the associated image file. </summary>
    /// <param name="originalMessageId">The ID of the message to be removed.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="xmlFilePath">The path of the XML file.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static void RemoveFromDb(int originalMessageId, string channelDir, string xmlFilePath)
    {
        lock (_botClient!)
        {
            var doc = XDocument.Load(xmlFilePath);

            var elementToRemove = doc.Descendants("message").FirstOrDefault(urlElement => urlElement.Attribute("messageId")?.Value == originalMessageId.ToString());
            if (elementToRemove != null)
            {
                elementToRemove.Remove();
                doc.Save(xmlFilePath);
            }
        }
        File.Delete(Path.Combine(channelDir, $"{originalMessageId}.jpg"));
    }

    /// <summary> Downloads an image from the given URL and processes it's similarity checks. </summary>
    /// <param name="url">The URL of the image to download.</param>
    /// <param name="message">The message associated with the image.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private static async Task ProcessImageUrl(string? url, Message message, string channelDir, CancellationToken cancellationToken)
    {
        var imagePath = await DownloadFileAsync(url, Path.Combine(channelDir, Guid.NewGuid().ToString()));
        var result = await ProcessStoredImage(imagePath, message, channelDir, cancellationToken);

        if (result is false)
        {
            await AddUrl2Bd(url, null, message, channelDir);
        }
    }

    /// <summary> Processes the title of the given Url and either sends a message about duplicate or adds a new one to the database. </summary>
    /// <param name="title">The title of the message.</param>
    /// <param name="url">The URL of the message.</param>
    /// <param name="message">The message object.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary> Processes a stored image, checks for duplicates and sends a message to the chat if a duplicate is found. </summary>
    /// <param name="filePath">The path of the stored image.</param>
    /// <param name="message">The message object.</param>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A Boolean indicating whether a duplicate was found.</returns>
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

            var (originalMessageId,count) = await GetDuplicateMessageId(message, channelDir, GetImageDescriptors(resizedImage));

            if (originalMessageId != 0)
            {
                //var originalMessageLink = $"https://t.me/c/{chatId}/{originalMessageId}";
                await SendInfo2Chat(originalMessageId, message.Chat.Id, cancellationToken, count);
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

    /// <summary> Gets the duplicate message ID from the given message, channel directory, and new image descriptors. </summary>
    /// <param name="message">The message to check the originality.</param>
    /// <param name="channelDir">The channel directory to search for the duplicate images.</param>
    /// <param name="newImageDescriptors">The given image descriptors to compare against.</param>
    /// <param name="count"></param>
    /// <returns>The duplicate message ID, or 0 if no duplicate message ID was found.</returns>
    private static async Task<(int originalMessageId, int count)> GetDuplicateMessageId(Message message, string channelDir, Mat newImageDescriptors)
    {
        var count = 0;
        var similarImageFile = Directory
            .EnumerateFiles(channelDir, "*.jpg")
            .OrderBy(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(f =>
            {
                var oldImage = Cv2.ImRead(f, ImreadModes.Color);
                var oldImageDescriptors = GetImageDescriptors(oldImage);

                var matches = CompareImages(oldImageDescriptors, newImageDescriptors);
                var isMatched = matches.Count > SomeThreshold;
                if (isMatched)
                {
                    count = matches.Count;
                }
                return isMatched;
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

        return (originalMessageId, count);
    }

    /// <summary> Retrieves the title, description, and image URL from an HTML page. </summary>
    /// <param name="url">The URL of the HTML page.</param>
    /// <returns>A tuple containing the title, description, and image URL.</returns>
    private static async Task<(string? Title, string? Description, string? ImageUrl)> GetHtmlPreview(string? url)
    {
        var web = new HtmlWeb();
        var doc = await web.LoadFromWebAsync(url);

        var titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']") ?? doc.DocumentNode.SelectSingleNode("//head/title");

        var descriptionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']") ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']");

        var imageUrlNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

        var title = titleNode?.Name == "meta" ? titleNode.GetAttributeValue("content", "") : titleNode?.InnerText;
        title = title != null ? WebUtility.HtmlDecode(title) : null;
        var description = descriptionNode?.GetAttributeValue("content", "");
        var imageUrl = imageUrlNode?.GetAttributeValue("content", "");

        return (title, description, imageUrl);
    }

    /// <summary> Gets the MIME type of a given URL. </summary>
    /// <param name="url">The URL to get the MIME type of.</param>
    /// <returns>The MIME type of the given URL.</returns>
    private static async Task<string?> GetMime(string? url)
    {
        using var client = new HttpClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");

        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await client.SendAsync(request);

        string? contentType = null;
        if (response.IsSuccessStatusCode)
        {
            contentType = response.Content.Headers.ContentType?.MediaType;
        }

        return contentType;
    }

    /// <summary> Handles errors that occur during polling. </summary>
    /// <param name="botClient">The Telegram Bot Client.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }

    /// <summary> Downloads a file from the given URL and saves it to the given file path. </summary>
    /// <param name="fileUrl">The URL of the file to download.</param>
    /// <param name="filePath">The path to save the file to.</param>
    /// <returns>The full path of the saved file, or null if the download failed.</returns>
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

    /// <summary> Parses the URL in a message text entities. </summary>
    /// <param name="entities">The list of entities.</param>
    /// <param name="entityValues">The list of entity values.</param>
    /// <returns>A list of unique URLs.</returns>
    private static IEnumerable<string> ParseUrlInMessage(MessageEntity[] entities, IEnumerable<string> entityValues)
    {
        return entities.Select(entity => entity.Type switch
        {
            MessageEntityType.Url => entityValues.ElementAt(Array.IndexOf(entities, entity)),
            MessageEntityType.TextLink => entity.Url,
            _ => null
        }).Where(res => !string.IsNullOrWhiteSpace(res)).Distinct()!;
    }

    /// <summary> Fixes a non-well shaped URL. </summary>
    /// <param name="url">The URL to fix.</param>
    /// <returns>The fixed URL.</returns>
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

    /// <summary> Normalizes a file name by removing the any trailing underscores and distinction postfixes. </summary>
    /// <param name="path">The path of the file to normalize.</param>
    /// <returns>The normalized file name.</returns>
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

    /// <summary> Creates a directory for the current group and returns the directory path. </summary>
    /// <param name="message">The message object.</param>
    /// <returns> The directory path for the current group. </returns>
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

    /// <summary> Ensures that the Urls.xml file exists in the specified channel directory. </summary>
    /// <param name="channelDir">The directory of the channel.</param>
    /// <returns>The path of the Urls.xml file.</returns>
    private static string EnsureBdExists(string channelDir)
    {
        var xmlFilePath = Path.Combine(channelDir, "Urls.xml");
        if (!File.Exists(xmlFilePath))
        {
            Console.WriteLine(41);
            lock (_botClient!)
            {
                Console.WriteLine(42);
                new XDocument(new XElement("message")).Save(xmlFilePath);
            }
            Console.WriteLine(43);
        }

        return xmlFilePath;
    }

    /// <summary> Down-scales an image to a given size. </summary>
    /// <param name="image">The image to be down-scaled.</param>
    /// <returns>The down-scaled image.</returns>
    private static Mat Downscale(Mat image)
    {
        var scale = Math.Min((double)ThumbnailSize / image.Width, (double)ThumbnailSize / image.Height);

        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);

        var resizedImage = new Mat();
        Cv2.Resize(image, resizedImage, new Size(newWidth, newHeight));

        return resizedImage;
    }

    /// <summary> Detects and computes the descriptors of an image using the SURF algorithm. </summary>
    /// <param name="image">The image to be processed.</param>
    /// <returns>The descriptors of the image.</returns>
    private static Mat GetImageDescriptors(Mat image)
    {
        var detector = SURF.Create(400);
        Mat descriptors = new();
        detector.DetectAndCompute(image, null, out _, descriptors);

        return descriptors;
    }

    /// <summary>
    /// Compares two images using FlannBasedMatcher and KDTreeIndexParams.
    /// </summary>
    /// <param name="descriptors1">The descriptors of the first image.</param>
    /// <param name="descriptors2">The descriptors of the second image.</param>
    /// <returns>A list of DMatch objects.</returns>
    private static List<DMatch> CompareImages(Mat descriptors1, Mat descriptors2)
    {
        var bfMatcher = new FlannBasedMatcher(new KDTreeIndexParams(2), new SearchParams());
        var matches = bfMatcher.KnnMatch(descriptors1, descriptors2, 2);

        return matches
            .Where(match => match.Length > 1 && match[0].Distance < 0.75f * match[1].Distance)
            .Select(match => match[0]).ToList();
    }

    private static string FormatException(Exception e)
    {
        StringBuilder sb = new();
        FormatExceptionInternal(e, sb, string.Empty);
        return sb.ToString();
    }


    private static void FormatExceptionInternal(Exception e, StringBuilder sb, string indent)
    {
        if (indent.Length > 0)
        {
            sb.Append($"{indent}Inner ");
        }

        sb.Append($"Exception Found:\n{indent}Type: {e.GetType().FullName}");
        sb.Append($"\n{indent}Message: {e.Message}");
        sb.Append($"\n{indent}Source: {e.Source}");
        sb.Append($"\n{indent}StackTrace: {e.StackTrace}");

        if (e.InnerException != null)
        {
            sb.Append("\n");
            FormatExceptionInternal(e.InnerException, sb, indent + "  ");
        }
    }
}