using System.Configuration;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using OpenCvSharp.XFeatures2D;
using SimMetrics.Net.Metric;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Tesseract;
using File = System.IO.File;
using Size = OpenCvSharp.Size;

namespace TelegramBot;

public class MessageOperations
{
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly Message _message;
    private readonly CancellationToken _cancellationToken;
    private const int SomeThreshold = 60;
    private readonly string _channelDir;
    private readonly string _xmlFilePath;
    private readonly long _chatId;
    private readonly int _messageId;
    private readonly string? _mediaGroup;
    private bool _answered;
    private const double TextSimilarityThreshold = 0.7;
    private const double OcrConfidenceThreshold = 0.6;
    private const int MinimalTextSizeToDrop = 60;
    private const string OcrModelFolder = @"./tessdata";
    private const int ThumbnailSize = 400;
    private const int TempChatId = 109671846;
    private const string MessageText = "It looks like similar content has already been posted here ↑";
    private static readonly List<string> ExcludedHosts = new() { @"store.steampowered.com" };


    public MessageOperations(ITelegramBotClient telegramBotClient, Message message, CancellationToken cancellationToken)
    {
        _telegramBotClient = telegramBotClient;
        _message = message;
        _cancellationToken = cancellationToken;

        _chatId = _message.Chat.Id;

        _channelDir = GetChannelInfo();

        _messageId = _message.MessageId;
        _mediaGroup = _message.MediaGroupId;

        _xmlFilePath = EnsureBdExists();
    }


    internal async Task PerformOperations()
    {
        await ProcessPhoto();
        var urls = ElaborateAllMessageUrl();
        var videos = ProcessVideo();
        var gif = ProcessAnimation();
        await Task.WhenAll(urls, videos, gif);
    }

    private async Task ProcessAnimation()
    {
        await ProcessEmbeddedImage(_message.Animation?.Thumbnail);
    }

    private async Task ProcessVideo()
    {
        await ProcessEmbeddedImage(_message.Video?.Thumbnail);
    }

    private async Task ProcessPhoto()
    {
        await ProcessEmbeddedImage(_message.Photo?.MaxBy(x => x.FileSize));
    }

    private async Task ProcessEmbeddedImage(FileBase? photo)
    {
        if (photo?.FileId != null)
        {
            var file = await _telegramBotClient.GetFileAsync(photo.FileId, _cancellationToken);

            if (file.FilePath == null)
            {
                Console.WriteLine(
                    $@"Telegram message is not well-formed. Image {file.FileId} in the message {_message.MessageId} does not exists.");
                return;
            }

            var photoTempPath = Path.Combine(TelegramBot.CacheDir, _chatId.ToString(), Guid.NewGuid().ToString());
            await using (var fileStream = new FileStream(photoTempPath, FileMode.Create))
            {
                await _telegramBotClient.DownloadFileAsync(file.FilePath, fileStream, _cancellationToken);
            }

            await ProcessStoredImage(photoTempPath);
        }
    }

    /// <summary> Sends a text message to a chat using the bot client. </summary>
    /// <param name="originalMessageId">The ID of the message that will be pointed as original content.</param>
    /// <param name="count"></param>
    /// <param name="matchDemo"></param>
    /// <param name="title"></param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task SendInfo2Chat(int originalMessageId, int count = 0, Mat? matchDemo = null, string title = "")
    {
        lock (_message)
        {
            if (_answered)
            {
                return;
            }
            _answered = true;
        }

        try
        {
            Message mess;
            if (matchDemo != null)
            {
                var buffer = MatToBytes(matchDemo);
                using var ms = new MemoryStream(buffer);

                mess = await _telegramBotClient.SendDocumentAsync(
                    chatId: _chatId,
                    document: new InputFileStream(ms, "match.jpg"),
                    caption: $"{MessageText}\nImage similarity: {100 * (1 - Math.Pow(2, -1.0 * count / SomeThreshold)):0}%",
                    replyToMessageId: originalMessageId,
                    cancellationToken: _cancellationToken);
            }
            else
            {
                var ellipsis =  string.Join(' ', title.Trim().Split( ' ', StringSplitOptions.RemoveEmptyEntries).Take(20)) + "...";
                var textOutput = string.IsNullOrWhiteSpace(title.Trim()) ? MessageText : $"{MessageText}\nText:\n{ellipsis}";

                mess = await _telegramBotClient.SendTextMessageAsync(
                    chatId: _chatId,
                    text: textOutput,
                    replyToMessageId: originalMessageId,
                    cancellationToken: _cancellationToken);
            }

            TelegramBot.LastMessageId = mess.MessageId;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    /// <summary>  Elaborates all message URLs by parsing their text, fixing the unformed URLs, and processing each URL further. </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ElaborateAllMessageUrl()
    {
        var allEntityValues = Enumerable.Empty<string>();
        if (_message.Entities != null)
        {
            allEntityValues = ParseUrlInMessage(_message.Entities, _message.EntityValues!);
        }

        if (_message.CaptionEntities != null)
        {
            allEntityValues = allEntityValues.Concat(ParseUrlInMessage(_message.CaptionEntities, _message.CaptionEntityValues!));
        }

        await Task
            .WhenAll(allEntityValues
                .Select(FixUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(s => ProcessSingleUrl(s!))
                .ToList());
    }

    /// <summary> Processes a single URL, either by sending an existing message with original content or by processing the new content from the URL. </summary>
    /// <param name="url">The URL to process.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task ProcessSingleUrl(string url)
    {
        var originalMessageId = await GetExistingMessageIdFromBd(u =>
        {
            try
            {
                var uriString = u.Attribute("url")?.Value;
                if (Uri.IsWellFormedUriString(url, UriKind.Absolute) && Uri.IsWellFormedUriString(uriString, UriKind.Absolute))
                {
                    return Uri.Compare(
                               new Uri(uriString),
                               new Uri(url),
                               UriComponents.Host | UriComponents.Path,
                               UriFormat.SafeUnescaped,
                               StringComparison.OrdinalIgnoreCase)
                           == 0;
                }

                return false;
            }
            catch (Exception e)
            {
                Console.WriteLine(TelegramBot.FormatException(e));
                return false;
            }
        });

        if (originalMessageId > 0)
        {
            await SendInfo2Chat(originalMessageId);
        }
        else
        {
            await ProcessSingleUrlContent(url);
        }
    }

    /// <summary> Gets the existing entity message identifier from the database and checks if this message still exists. </summary>
    /// <param name="entityComparisonRule">The xml records comparison predicate.</param>
    /// <returns>The existing entity message identifier.</returns>
    private async Task<int> GetExistingMessageIdFromBd(Func<XElement, bool> entityComparisonRule)
    {

        var originalMessageId = 0;
        XDocument doc;
        lock (_telegramBotClient)
        {
            doc = XDocument.Load(_xmlFilePath);
        }

        var foundSimilarMessages = doc
                .Descendants("message")
                .Where(u => entityComparisonRule(u) && u.Attribute("messageId")?.Value != null)
                .Select(u => int.Parse(u.Attribute("messageId")?.Value!)).ToList();
     
        if (foundSimilarMessages.Any())
        {
            originalMessageId = foundSimilarMessages.Min();
            return await IsMessageExistsOnline(originalMessageId) ? originalMessageId : 0;
        }

        return 0;
    }

    /// <summary> Processes a URL downloaded content by determining the content type and processing the URL accordingly. </summary>
    /// <param name="url">The URL to process.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ProcessSingleUrlContent(string url)
    {
        var contentType = await GetMime(url);
        if (contentType != null)
        {
            if (contentType.StartsWith("image/"))
            {
                await ProcessImageUrl(url);
            }

            else if (contentType.StartsWith("text/"))
            {
                var (title, imageUrl) = await GetHtmlPreview(url);
                if (title != null)
                {
                    await ProcessTitle(title, url);
                }
                if (imageUrl != null)
                {
                    if (!ExcludedHosts.Any(url.Contains))
                    {
                        await ProcessImageUrl(imageUrl);
                    }
                }
            }
            else
            {
                await AddUrl2Bd(url, string.Empty);
            }
        }
    }

    /// <summary> Adds a URL to the database if it does not already exist with its message ID and title. </summary>
    /// <param name="url">The URL to add.</param>
    /// <param name="description">The description of the URL.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task AddUrl2Bd(string? url, string? description)
    {
        var url1 = string.IsNullOrWhiteSpace(url) ? "\\uri" : url;

        var originalMessageId = await GetExistingMessageIdFromBd(u =>
            string.Equals(u.Attribute("url")?.Value, url1, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Attribute("description")?.Value, description, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Attribute("messageId")?.Value, _messageId.ToString(), StringComparison.OrdinalIgnoreCase));

        if (originalMessageId <= 0)
        {
            lock (_telegramBotClient)
            {
                var doc = XDocument.Load(_xmlFilePath);
                var urlElement = new XElement("message",
                    new XAttribute("url", url ?? string.Empty),
                    new XAttribute("messageId", _messageId),
                    new XAttribute("description", description ?? string.Empty)
                );
                doc.Root?.Add(urlElement);

                doc.Save(_xmlFilePath);
            }
        }
    }

    /// <summary> Checks if a message exists in a chat. </summary>
    /// <param name="originalMessageId">The original message id.</param>
    /// <returns> A Boolean value indicating whether the message exists. </returns>
    private async Task<bool> IsMessageExistsOnline(int originalMessageId)
    {
        if (string.IsNullOrWhiteSpace(_xmlFilePath))
        {
            return false;
        }

        var isMessageExists = false;
        try
        {
            var tempMessageId = await _telegramBotClient.CopyMessageAsync(TempChatId, _chatId, originalMessageId, cancellationToken: _cancellationToken);
            await _telegramBotClient.DeleteMessageAsync(TempChatId, tempMessageId.Id, cancellationToken: _cancellationToken);
            isMessageExists = true;
        }
        catch (ApiRequestException e)
        {
            if (e.ErrorCode == 400)
            {
                Console.WriteLine(@$"Message https://t.me/c/{_chatId.ToString().Remove(0, "-100".Length)}/{originalMessageId} is not exist and cannot be replied.");
                Console.WriteLine($"Error {e.Message}");
                RemoveFromDb(originalMessageId);
            }
            else
            {
                Console.WriteLine(TelegramBot.FormatException(e));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(TelegramBot.FormatException(e));
        }

        return isMessageExists;
    }

    /// <summary> Removes a message from the database and deletes the associated image file. </summary>
    /// <param name="originalMessageId">The ID of the message to be removed.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private void RemoveFromDb(int originalMessageId)
    {
        lock (_telegramBotClient)
        {
            var doc = XDocument.Load(_xmlFilePath);

            var elementToRemove = doc.Descendants("message").FirstOrDefault(urlElement => urlElement.Attribute("messageId")?.Value == originalMessageId.ToString());
            if (elementToRemove != null)
            {
                elementToRemove.Remove();
                doc.Save(_xmlFilePath);
            }
        }
        File.Delete(Path.Combine(_channelDir, $"{originalMessageId}.jpg"));
    }

    /// <summary> Downloads an image from the given URL and processes it's similarity checks. </summary>
    /// <param name="url">The URL of the image to download.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task ProcessImageUrl(string url)
    {
        var imagePath = await DownloadFileAsync(url, Path.Combine(_channelDir, Guid.NewGuid().ToString()));
        var result = await ProcessStoredImage(imagePath, url);

        if (result is false)
        {
            await AddUrl2Bd(url, null);
        }
    }

    /// <summary> Processes the title of the given Url and either sends a message about duplicate or adds a new one to the database. </summary>
    /// <param name="title">The title of the _message.</param>
    /// <param name="url">The URL of the _message.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    private async Task ProcessTitle(string title, string url)
    {
        var originalMessageId = await GetExistingMessageIdFromBd(u =>
            new Levenstein()
                .GetSimilarity(
                    (u.Attribute("description")?.Value ?? "").ToLower(),
                    title.ToLower())
            > TextSimilarityThreshold);

        if (originalMessageId > 0)
        {
            await SendInfo2Chat(originalMessageId, title: title);
        }
        else
        {
            await AddUrl2Bd(url, title);
        }
    }

    /// <summary> Processes a stored image, checks for duplicates and sends a message to the chat if a duplicate is found. </summary>
    /// <param name="filePath">The path of the stored image.</param>
    /// <param name="url"></param>
    /// <returns>A Boolean indicating whether a duplicate was found.</returns>
    private async Task<bool?> ProcessStoredImage(string? filePath, string url = "")
    {
        bool? ret;
        if (filePath == null)
        {
            return null;
        }

        using (var newImage = Cv2.ImRead(filePath, ImreadModes.Color))
        {
            if (!await ProcessTextScreen(url, newImage))
            {
                using var resizedImage = Downscale(newImage);

                var (originalMessageId, count, matchDemo) = await GetOriginalMessageIdAndSimilarity(resizedImage);

                if (originalMessageId != 0)
                {
                    //var originalMessageLink = $"https://t.me/c/{chatId}/{originalMessageId}";
                    await SendInfo2Chat(originalMessageId, count, matchDemo);
                    ret = true;
                }
                else
                {
                    var fileName = $"{_messageId}.jpg";
                    if (File.Exists(Path.Combine(_channelDir, fileName)))
                    {
                        fileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now.Ticks}.jpg";
                    }

                    Cv2.ImWrite(Path.Combine(_channelDir, fileName), resizedImage);
                    ret = false;
                }

                matchDemo?.Release();
                resizedImage.Release();
                newImage.Release();
            }
            else
            {
                ret = true;
            }
        }

        File.Delete(filePath);

        return ret;
    }

    private async Task<bool> ProcessTextScreen(string url, Mat newImage)
    {
        using var newImage1 = new Mat();

        Cv2.CvtColor(newImage, newImage1, ColorConversionCodes.BGR2GRAY);

        using var bmp = newImage1.ToBitmap();
        using var img = PixConverter.ToPix(bmp);
        
        using var engineRus = new TesseractEngine(OcrModelFolder, "rus", EngineMode.Default);
        using var engineEng = new TesseractEngine(OcrModelFolder, "eng", EngineMode.Default);

        var (confidenceRus, lengthRus, textRus) = OcrImage(engineRus, img);
        var (confidenceEng, lengthEng, textEng) = OcrImage(engineEng, img);

        var confidence = float.Max(confidenceEng, confidenceRus);
        var length = int.Max(lengthEng, lengthRus);
        if (confidence > OcrConfidenceThreshold && length > MinimalTextSizeToDrop)
        {
            Console.WriteLine($"https://t.me/c/{_chatId.ToString().Remove(0, "-100".Length)}/{_messageId} picture highly likely a text and will not be treated as picture");
            var title = confidenceRus > confidenceEng ? textRus : textEng;
            var cleanedText = Regex.Replace(title, @"\s+", " ");
            await ProcessTitle(cleanedText, url);

            return true;
        }

        return false;
    }

    private (float meanConfidence, int length, string text) OcrImage(TesseractEngine engine, Pix pix)
    {
        using var page = engine.Process(pix);
        var text = page.GetText();
        text = Regex.Replace(text, @"\s+", " ");
        text = Regex.Replace(text.Trim(), @"(\w+)-\s+(\w+)", "$1$2");
        var meanConfidence = page.GetMeanConfidence();
        var length = text.Length;
        return (meanConfidence, length, text);
    }

    /// <summary> Gets the duplicate message ID from the given message, channel directory, and new image descriptors. </summary>
    /// <returns>The duplicate message ID, or 0 if no duplicate message ID was found.</returns>
    private async Task<(int originalMessageId, int count, Mat? oldImage)> GetOriginalMessageIdAndSimilarity(Mat newImage)
    {
        var newImageDescriptors = GetImageDescriptors(newImage, out var newKeyPoints);
        var count = 0;
        Mat oldImage = null!;
        List<DMatch> matches = null!;

        KeyPoint[] oldKeyPoints = null!;
        var similarImageFile = Directory
            .EnumerateFiles(_channelDir, "*.jpg")
            .OrderByDescending(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(f =>
            {
                oldImage = Cv2.ImRead(f, ImreadModes.Color);
                var oldImageDescriptors = GetImageDescriptors(oldImage, out oldKeyPoints);

                matches = CompareImages(oldImageDescriptors, newImageDescriptors);
                var isMatched = matches.Count > SomeThreshold;
                if (isMatched)
                {
                    count = matches.Count;
                }
                return isMatched;
            });

        if (similarImageFile != null)
        {
            var imgMatches = ImgMatches(matches, newImage, oldKeyPoints, oldImage, newKeyPoints);
            similarImageFile = NormalizeFileName(similarImageFile);

            int.TryParse(Path.GetFileNameWithoutExtension(similarImageFile), out var originalMessageId);

            try
            {
                originalMessageId = await IsMessageExistsOnline(originalMessageId) ? originalMessageId : 0;
            }
            catch (Exception)
            {
                originalMessageId = 0;
            }

            return (originalMessageId, count, imgMatches);
        }


        return (0, 0, null!);
    }

    /// <summary>
    /// Draws matches between two images and returns the result as a Mat.
    /// </summary>
    /// <param name="matches">The matches between the two images.</param>
    /// <param name="newImage">The new image.</param>
    /// <param name="oldKeyPoints">The key-points of the old image.</param>
    /// <param name="oldImage">The old image.</param>
    /// <param name="newKeyPoints">The key-points of the new image.</param>
    /// <returns>The result of the matches drawn between the two images.</returns>
    private static Mat ImgMatches(IEnumerable<DMatch> matches, Mat newImage, IEnumerable<KeyPoint> oldKeyPoints, Mat oldImage, IEnumerable<KeyPoint> newKeyPoints)
    {
        var imgMatches = new Mat();

        Cv2.DrawMatches(oldImage, oldKeyPoints, newImage, newKeyPoints, matches.Take(10), imgMatches,
            matchColor: Scalar.Cyan,
            singlePointColor: Scalar.Red,
            flags: DrawMatchesFlags.NotDrawSinglePoints
        );

        // foreach (var match in matches.Take(20))
        // {
        //     Cv2.Circle(imgMatches, (Point)new Point2f(oldKeyPoints[match.QueryIdx].Pt.X, oldKeyPoints[match.QueryIdx].Pt.Y), 4, Scalar.Red, 1);
        //     Cv2.Circle(imgMatches, (Point)new Point2f(newKeyPoints[match.TrainIdx].Pt.X + oldImage.Width, newKeyPoints[match.TrainIdx].Pt.Y), 4, Scalar.Red, 1);
        //     Cv2.Line(imgMatches, (Point)oldKeyPoints[match.QueryIdx].Pt, (Point)new Point2f(newKeyPoints[match.TrainIdx].Pt.X + oldImage.Width, newKeyPoints[match.TrainIdx].Pt.Y), Scalar.RandomColor(), 1);
        // }
        return imgMatches;
    }

    /// <summary> Retrieves the title, description, and image URL from an HTML page. </summary>
    /// <param name="url">The URL of the HTML page.</param>
    /// <returns>A tuple containing the title, description, and image URL.</returns>
    private async Task<(string? title, string? imageUrl)> GetHtmlPreview(string? url)
    {
        var (title, imageUrl) = await FilterWhiteListUrls(url);

        if (imageUrl != null || title != null)
        {
            return (title, imageUrl);
        }

        var web = new HtmlWeb { UserAgent = "curl/8.0.1" };
        var doc = await web.LoadFromWebAsync(url, _cancellationToken);

        var titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']") ??
                        doc.DocumentNode.SelectSingleNode("//head/title");

        // var descriptionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']") ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']");

        var imageUrlNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

        title = titleNode?.Name == "meta" ? titleNode.GetAttributeValue("content", "") : titleNode?.InnerText;
        title = title != null ? WebUtility.HtmlDecode(title) : null;

        if (title?.Length < MinimalTextSizeToDrop)
        {
            title = null;
        }
        // var description = descriptionNode?.GetAttributeValue("content", "");
        imageUrl = imageUrlNode?.GetAttributeValue("content", "");
        imageUrl = imageUrl != null ? WebUtility.HtmlDecode(imageUrl) : null;

        return (title, imageUrl);
    }

    private async Task<(string? title, string? imageUrl)> FilterWhiteListUrls(string? url)
    {
        if (url != null && url.Contains("t.me"))
        {
            var web = new HtmlWeb { UserAgent = "curl/8.0.1" };
            var doc = await web.LoadFromWebAsync(url, _cancellationToken);

            // var titleNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']") ??
                            // doc.DocumentNode.SelectSingleNode("//head/title");

            var descriptionNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']") ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']");

            // var imageUrlNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");

            var title = descriptionNode?.Name == "meta" ? descriptionNode.GetAttributeValue("content", "") : descriptionNode?.InnerText;
            title = title != null ? WebUtility.HtmlDecode(title) : null;

            if (title?.Length < MinimalTextSizeToDrop)
            {
                title = null;
            }

            return (title, null);
        }


        // if (url != null && url.Contains("twitter.com"))
        // {
        //     var match = Regex.Match(url, @"status/(\d+)");
        //     if (match.Success)
        //     {
        //         var tweetId = match.Groups[1].Value;
        //
        //         using var client = new HttpClient();
        //         client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ConfigurationManager.AppSettings.Get("token")}");
        //         var endpoint = $"https://api.twitter.com/1.1/statuses/show/{tweetId}.json";
        //
        //         var response = await client.GetAsync(endpoint, _cancellationToken);
        //
        //         if (response.IsSuccessStatusCode)
        //         {
        //             var responseBody = await response.Content.ReadAsStringAsync(_cancellationToken);
        //             var tweetData = JsonSerializer.Deserialize<Tweet>(responseBody);
        //
        //             if (tweetData != null)
        //             {
        //                 var title = tweetData.Text;
        //                 var imageUrl = tweetData.Entities?.Media?.FirstOrDefault(u => u?.MediaUrlHttps != null)?.MediaUrlHttps;
        //                 if (imageUrl != null)
        //                 {
        //                     await ProcessImageUrl(imageUrl);
        //                     return (title, url);
        //                 }
        //             }
        //         }
        //     }
        // }

        return (null, null);
    }

    /// <summary> Gets the MIME type of a given URL. </summary>
    /// <param name="url">The URL to get the MIME type of.</param>
    /// <returns>The MIME type of the given URL.</returns>
    private static async Task<string?> GetMime(string? url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/110.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"110\", \"Not A(Brand\";v=\"24\", \"Google Chrome\";v=\"110\"");
        //"Chromium";v="110", "Not A(Brand";v="24", "Google Chrome";v="110"
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await client.SendAsync(request);

        string? contentType = null;
        if (response.IsSuccessStatusCode)
        {
            contentType = response.Content.Headers.ContentType?.MediaType;
        }

        return contentType;
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

        var asoluteUri = uri?.AbsoluteUri;


        if (asoluteUri != null && asoluteUri.Contains("twitter.com"))
        {
            if (asoluteUri.Contains("://twitter.com"))
            {
                asoluteUri = asoluteUri.Replace("twitter.com", "fxtwitter.com");
            }
            
            asoluteUri = new Uri(asoluteUri).GetLeftPart(UriPartial.Path);
        }

        return asoluteUri;
    }

    /// <summary> Normalizes a file name by removing the any trailing underscores and distinction postfixes. </summary>
    /// <param name="path">The path of the file to normalize.</param>
    /// <returns>The normalized file name.</returns>
    private static string NormalizeFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Split('_')[0];
        var boldPath = Path.GetDirectoryName(path);
        return Path.Combine(boldPath!, $"{name}.jpg");
    }

    /// <summary> Ensures that the Urls.xml file exists in the specified channel directory. </summary>
    /// <returns>The path of the Urls.xml file.</returns>
    private string EnsureBdExists()
    {
        var xmlFilePath = Path.Combine(_channelDir, "Urls.xml");
        if (!File.Exists(xmlFilePath))
        {
            lock (_telegramBotClient)
            {
                new XDocument(new XElement("messages")).Save(xmlFilePath);
            }
        }

        return xmlFilePath;
    }

    /// <summary> Down-scales an image to a given size. </summary>
    /// <param name="image">The image to be down-scaled.</param>
    /// <returns>The down-scaled image.</returns>
    public static Mat Downscale(Mat image)
    {
        var scale = Math.Min((double)ThumbnailSize / image.Width, (double)ThumbnailSize / image.Height);

        var newWidth = (int)(image.Width * scale);
        var newHeight = (int)(image.Height * scale);

        var resizedImage = new Mat();
        Cv2.Resize(image, resizedImage, new Size(newWidth, newHeight), 0.0, 0.0, InterpolationFlags.Area);

        return resizedImage;
    }

    /// <summary> Detects and computes the descriptors of an image using the SURF algorithm. </summary>
    /// <param name="image">The image to be processed.</param>
    /// <param name="keyPoints"></param>
    /// <returns>The descriptors of the image.</returns>
    private static Mat GetImageDescriptors(Mat image, out KeyPoint[] keyPoints)
    {
        using var grayImage = new Mat();
        Cv2.CvtColor(image, grayImage, ColorConversionCodes.BGR2GRAY);

        using var image1 = new Mat();
        using var image2 = new Mat();
        Cv2.GaussianBlur(grayImage, image1, new Size(3, 3), 0);
        Cv2.EqualizeHist(image1, image2);

        var detector = SURF.Create(200);
        Mat descriptors = new();
        detector.DetectAndCompute(image, null, out keyPoints, descriptors);

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
        var bfMatcher = new BFMatcher(NormTypes.L2);
        var matches = bfMatcher.KnnMatch(descriptors1, descriptors2, 3);

        return matches
            .Where(match => match.Length > 1 && match[0].Distance < 0.7f * match[1].Distance)
            .OrderBy(m => m[0].Distance.CompareTo(m[1].Distance))
            .Select(match => match[0])
            .ToList();
    }

    /// <summary> Converts a Mat image to a byte array. </summary>
    /// <param name="image">The Mat image to convert.</param>
    /// <returns>A byte array representing the Mat image.</returns>
    private byte[] MatToBytes(Mat image)
    {
        Cv2.ImEncode(".jpg", image, out var buffer);
        return buffer;
    }

    /// <summary> Creates a directory for the current group and returns the directory path. </summary>
    /// <returns> The directory path for the current group. </returns>
    public string GetChannelInfo()
    {
        var channelDir = Path.Combine(TelegramBot.CacheDir, _chatId.ToString());
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
}