using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;


namespace TelegramBot;

internal static class TelegramBot
{
    private static string? _token;
    private static ITelegramBotClient? _botClient;
    private static CancellationTokenSource _cts;
    internal static int LastMessageId = 0;
    private static string _directoryName = null!;
    public static string CacheDir = null!;

    public static async Task Main(string[] args)
    {
        _cts = await Configuration(args);

        if (_botClient == null)
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
        Environment.Exit(0);
    }

    /// <summary> Configures the bot with the given token and directory name. </summary>
    /// <param name="args">The token and cache directory name.</param>
    /// <returns>A CancellationTokenSource for future operations if the bot can join groups and read all group messages, otherwise null.</returns>
    private static async Task<CancellationTokenSource> Configuration(IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            Console.WriteLine("Please provide two arguments: the token and the directory name.");
            Environment.Exit(1);
        }

        _token = args[0];
        _directoryName = args[1];


        CacheDir = Path.Combine(Directory.GetCurrentDirectory(), _directoryName);

        Console.WriteLine("\nCurrent cache directory: " + Path.GetFullPath(CacheDir));

        _botClient = new TelegramBotClient(_token);

        CancellationTokenSource cts = new();

        var me = await _botClient.GetMeAsync(cts.Token);
        Console.WriteLine($"Start listening for {me.Username} ({me.Id})");
        Console.WriteLine($"User: {(me.IsPremium == true ? "premium" : "")} {me.FirstName} {me.LastName}");

        Console.WriteLine($"{(me.CanJoinGroups is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} join groups.");
        Console.WriteLine($"{(me.CanReadAllGroupMessages is true ? $"✅ Bot {me.Username} can" : $"❎ Bot {me.Username} cannot")} read all group messages.");

        if (me.CanJoinGroups is not true || me.CanReadAllGroupMessages is not true)
        {
            Environment.Exit(1);
        }
        else
        {
            return cts;
        }

        return null;
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

            if (await HandleTextCommands(message, cancellationToken))
            {
                return;
            }

            var messageOperations = new MessageOperations(_botClient, message, cancellationToken);
            await messageOperations.PerformOperations();
        }
        catch (ApiRequestException e1)
        {
            Console.WriteLine($"ApiRequestException with code {e1.ErrorCode}");
            Console.WriteLine(FormatException(e1));
        }
        
        catch (Exception e)
        {
            Console.WriteLine(FormatException(e));
        }
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

    private static async Task<bool> HandleTextCommands(Message message, CancellationToken cancellationToken)
    {
        var ret = true;
        var textCaption = (message.Text + message.Caption).Trim();
        try
        {
            if (textCaption.StartsWith("BD delete last", StringComparison.OrdinalIgnoreCase))
            {
                await _botClient!.DeleteMessageAsync(message.Chat.Id, LastMessageId, cancellationToken);
            }
            else if (textCaption.StartsWith("BD delete", StringComparison.OrdinalIgnoreCase))
            {
                var words = textCaption.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length >= 3)
                {
                    if (int.TryParse(words[2], out var messageId))
                    {
                        await _botClient!.DeleteMessageAsync(message.Chat.Id, messageId, cancellationToken);
                    }
                }
            }
            else
            {
                ret = false;
            }
        }
        catch
        {
            ret = false;
        }
        return ret;
    }

    internal static string FormatException(Exception e)
    {
        Console.WriteLine('\n');
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