using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using File = System.IO.File;

namespace HistoryImport;

internal static class HistoryImport
{
    private const int ThumbnailSize = 400;

    private static void Main()
    {
        // Specify source and target directories
        var sourceDirectory = @"C:\Users\YuriiKikalo\Downloads\Telegram Desktop\ChatExport_2023-05-26 (1)";
        var targetDirectory = @"C:\backup\downloads\temp\ConsoleApp14\New folder";

        Directory.CreateDirectory(targetDirectory);

        var jsonText = File.ReadAllText(Path.Combine(sourceDirectory, "result.json"));
        var jsonObject = JObject.Parse(jsonText);

        var xmlDocument = new XDocument();
        var rootElement = new XElement("messages");
        xmlDocument.Add(rootElement);

        foreach (var message in jsonObject["messages"])
        {
            var messageId = message["id"]?.ToString();
            var mediaPath = message["file"]?.ToString();
            var photoPath = message["photo"]?.ToString();
            var thumbPath = message["thumbnail"]?.ToString();


            var urls = message["text_entities"]?.ToObject<JArray>()?
                .Select(te => new[] { te["href"], te["text"] }.FirstOrDefault(u => Uri.IsWellFormedUriString(u?.ToString(), UriKind.Absolute))?.ToString())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();


            var hasUrl = urls != null && urls.Any();

            var imagePath = new[] { photoPath, thumbPath, RetrieveThumbnail(mediaPath) }
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

            var hasMedia = imagePath != null;

            if (hasMedia || hasUrl)
            {
                if (hasMedia)
                {
                    var newImagePath = Path.Combine(targetDirectory, $"{messageId}.jpg");
                    var sourceFileName = Path.Combine(sourceDirectory, imagePath!);

                    if (File.Exists(sourceFileName))
                    {
                        File.Delete(newImagePath);
                        DownScale(sourceFileName, newImagePath);
                    }
                }

                if (hasUrl)
                {
                    foreach (var url in urls!)
                    {
                        var urlMessageElement = new XElement("message");
                        urlMessageElement.SetAttributeValue("messageId", messageId);
                        urlMessageElement.SetAttributeValue("url", url);
                        urlMessageElement.SetAttributeValue("description", "");
                        rootElement.Add(urlMessageElement);
                    }
                }

            }
        }

        xmlDocument.Save(Path.Combine(targetDirectory, "url.xml"));
    }

    private static void DownScale(string sourceFileName, string newImagePath)
    {
        try
        {
            File.Copy(sourceFileName, newImagePath);
            using var newImage = Cv2.ImRead(newImagePath, ImreadModes.Color);
            var scale = Math.Min((double)ThumbnailSize / newImage.Width, (double)ThumbnailSize / newImage.Height);

            if (scale <= 1)
            {
                var newWidth = (int)(newImage.Width * scale);
                var newHeight = (int)(newImage.Height * scale);

                using var resizedImage = new Mat();
                Cv2.Resize(newImage, resizedImage, new Size(newWidth, newHeight), 0.0, 0.0, InterpolationFlags.Area);

                File.Delete(newImagePath);
                Cv2.ImWrite(newImagePath, resizedImage);
            }
        }
        catch (Exception e)
        {

            Console.WriteLine(e);
        }
    }

    private static string? RetrieveThumbnail(string? mediaPath)
    {
        return null;
    }
}
