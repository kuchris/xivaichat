using Dalamud.Game.Text;

namespace XivAiChat;

internal sealed record ChatChannelDefinition(string Id, string Label, string Group, XivChatType Type, string CommandPrefix);

internal static class ChatChannelRegistry
{
    private static readonly List<ChatChannelDefinition> Channels =
    [
        new("say", "Say", "General", XivChatType.Say, "/s"),
        new("party", "Party", "General", XivChatType.Party, "/p"),
        new("alliance", "Alliance", "General", XivChatType.Alliance, "/a"),
        new("freecompany", "Free Company", "General", XivChatType.FreeCompany, "/fc"),
        new("novicenetwork", "Novice Network", "General", XivChatType.NoviceNetwork, "/n"),
        new("yell", "Yell", "General", XivChatType.Yell, "/y"),
        new("shout", "Shout", "General", XivChatType.Shout, "/sh"),
        new("ls1", "Linkshell 1", "Linkshell", XivChatType.Ls1, "/l1"),
        new("ls2", "Linkshell 2", "Linkshell", XivChatType.Ls2, "/l2"),
        new("ls3", "Linkshell 3", "Linkshell", XivChatType.Ls3, "/l3"),
        new("ls4", "Linkshell 4", "Linkshell", XivChatType.Ls4, "/l4"),
        new("ls5", "Linkshell 5", "Linkshell", XivChatType.Ls5, "/l5"),
        new("ls6", "Linkshell 6", "Linkshell", XivChatType.Ls6, "/l6"),
        new("ls7", "Linkshell 7", "Linkshell", XivChatType.Ls7, "/l7"),
        new("ls8", "Linkshell 8", "Linkshell", XivChatType.Ls8, "/l8"),
        new("cwl1", "CWLS 1", "Cross-world Linkshell", XivChatType.CrossLinkShell1, "/cwl1"),
        new("cwl2", "CWLS 2", "Cross-world Linkshell", XivChatType.CrossLinkShell2, "/cwl2"),
        new("cwl3", "CWLS 3", "Cross-world Linkshell", XivChatType.CrossLinkShell3, "/cwl3"),
        new("cwl4", "CWLS 4", "Cross-world Linkshell", XivChatType.CrossLinkShell4, "/cwl4"),
        new("cwl5", "CWLS 5", "Cross-world Linkshell", XivChatType.CrossLinkShell5, "/cwl5"),
        new("cwl6", "CWLS 6", "Cross-world Linkshell", XivChatType.CrossLinkShell6, "/cwl6"),
        new("cwl7", "CWLS 7", "Cross-world Linkshell", XivChatType.CrossLinkShell7, "/cwl7"),
        new("cwl8", "CWLS 8", "Cross-world Linkshell", XivChatType.CrossLinkShell8, "/cwl8"),
    ];

    private static readonly IReadOnlyDictionary<string, ChatChannelDefinition> ById =
        Channels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<XivChatType, ChatChannelDefinition> ByType =
        Channels.ToDictionary(channel => channel.Type);

    public static IReadOnlyList<ChatChannelDefinition> All => Channels;

    public static IEnumerable<string> Groups =>
        Channels.Select(channel => channel.Group).Distinct(StringComparer.Ordinal);

    public static IEnumerable<ChatChannelDefinition> GetByGroup(string group)
    {
        return Channels.Where(channel => string.Equals(channel.Group, group, StringComparison.Ordinal));
    }

    public static bool TryGetById(string channelId, out ChatChannelDefinition? channel)
    {
        return ById.TryGetValue(channelId, out channel);
    }

    public static bool TryGetByType(XivChatType type, out ChatChannelDefinition? channel)
    {
        return ByType.TryGetValue(type, out channel);
    }
}
