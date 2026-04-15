using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace XivAiChat;

internal static class GameChatSender
{
    public static bool SendMessage(ChatChannelDefinition channel, string message)
    {
        if (channel is null || string.IsNullOrWhiteSpace(channel.CommandPrefix) || string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var command = $"{channel.CommandPrefix} {message}".Trim();
        return ExecuteCommand(command);
    }

    private static unsafe bool ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        using var utf8Command = new Utf8String(command);
        utf8Command.SanitizeString(
            AllowedEntities.Unknown9 |
            AllowedEntities.Payloads |
            AllowedEntities.OtherCharacters |
            AllowedEntities.SpecialCharacters |
            AllowedEntities.Numbers |
            AllowedEntities.LowercaseLetters |
            AllowedEntities.UppercaseLetters);

        if (utf8Command.Length is <= 0 or > 500)
        {
            return false;
        }

        UIModule.Instance()->ProcessChatBoxEntry(&utf8Command);
        return true;
    }
}
