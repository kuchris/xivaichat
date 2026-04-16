using System.Globalization;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivAiChat;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/xivaichat";
    private const string Tag = "XIV AI Chat";
    private const int MaxReplyLength = 420;
    private const int MaxGameMessageBytes = 500;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    private readonly List<ChatHistoryEntry> recentTurns = [];
    private readonly object historySync = new();
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly LmStudioClient lmStudioClient = new();
    private readonly ExaSearchClient exaSearchClient = new();
    private readonly ConfigWindow configWindow;
    private readonly CancellationTokenSource disposalTokenSource = new();
    private string? lastProcessedFingerprint;

    // Stored as UTC ticks so Interlocked can provide thread-safe access.
    private long lastReplyAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private long lastProcessedFingerprintAtTicks = DateTimeOffset.MinValue.UtcTicks;

    private sealed record ChatHistoryEntry(string ChannelId, LmStudioClient.ChatTurn Turn);

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);
        this.WindowSystem = new WindowSystem("XivAiChat");
        this.configWindow = new ConfigWindow(this);

        this.WindowSystem.AddWindow(this.configWindow);

        CommandManager.AddHandler(
            CommandName,
            new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open the XIV AI Chat window, or use subcommands like status / test.",
            });

        ChatGui.ChatMessage += this.OnChatMessage;
        ChatGui.ChatMessageUnhandled += this.OnChatMessageUnhandled;
        PluginInterface.UiBuilder.Draw += this.DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;
        this.LastDecision = "Plugin loaded.";
        Log.Information("Loaded {PluginName}", PluginInterface.Manifest.Name);
    }

    public string Name => "XIV AI Chat";

    public Configuration Configuration { get; }

    public WindowSystem WindowSystem { get; }

    public string LastDecision { get; private set; } = "Starting";

    public string LastSeenSource { get; private set; } = "-";

    public string LastSeenType { get; private set; } = "-";

    public string LastSeenSender { get; private set; } = "-";

    public string LastSeenMessage { get; private set; } = "-";

    public void Dispose()
    {
        ChatGui.ChatMessage -= this.OnChatMessage;
        ChatGui.ChatMessageUnhandled -= this.OnChatMessageUnhandled;
        PluginInterface.UiBuilder.Draw -= this.DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        this.WindowSystem.RemoveAllWindows();
        this.configWindow.Dispose();
        this.disposalTokenSource.Cancel();
        this.disposalTokenSource.Dispose();
        this.requestGate.Dispose();
        this.lmStudioClient.Dispose();
        this.exaSearchClient.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            this.OpenMainUi();
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var action = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (action)
        {
            case "status":
                this.PrintStatus();
                break;
            case "on":
                this.Configuration.Enabled = true;
                this.Configuration.Save();
                ChatGui.Print("Auto-listening enabled.", Tag);
                break;
            case "off":
                this.Configuration.Enabled = false;
                this.Configuration.Save();
                ChatGui.Print("Auto-listening disabled.", Tag);
                break;
            case "send":
                this.HandleSendCommand(rest);
                break;
            case "slot":
                this.HandleSlotCommand(rest);
                break;
            case "endpoint":
                this.HandleTextUpdate(rest, value => this.Configuration.Endpoint = value, "Endpoint");
                break;
            case "model":
                this.HandleTextUpdate(rest, value => this.Configuration.Model = value, "Model");
                break;
            case "prompt":
                this.HandleTextUpdate(rest, value => this.Configuration.SystemPrompt = value, "Prompt");
                break;
            case "alias":
                this.HandleAliasCommand(rest);
                break;
            case "mention":
                this.HandleBooleanUpdate(rest, value => this.Configuration.RequireMention = value, "RequireMention");
                break;
            case "cooldown":
                this.HandleIntegerUpdate(
                    rest,
                    0,
                    600,
                    value => this.Configuration.CooldownSeconds = value,
                    "CooldownSeconds");
                break;
            case "test":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    ChatGui.PrintError("Usage: /xivaichat test <message>", Tag);
                    return;
                }

                _ = Task.Run(() => this.RunManualPromptAsync(rest, this.disposalTokenSource.Token));
                ChatGui.Print("Sent a manual test prompt to the selected provider.", Tag);
                break;
            case "help":
                this.PrintHelp();
                break;
            default:
                ChatGui.PrintError("Unknown subcommand. Use /xivaichat help.", Tag);
                break;
        }
    }

    public void SaveConfiguration()
    {
        this.Configuration.Save();
    }

    public void OpenConfigUi()
    {
        this.configWindow.IsOpen = true;
    }

    public void OpenMainUi()
    {
        this.configWindow.IsOpen = true;
    }

    public void RunManualPrompt(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ChatGui.PrintError("Enter a test message first.", Tag);
            return;
        }

        _ = Task.Run(() => this.RunManualPromptAsync(message, this.disposalTokenSource.Token));
    }

    public async Task<string?> TryDetectLmStudioModelAsync()
    {
        var model = await this.lmStudioClient.TryDetectLoadedLmStudioModelAsync(
            this.Configuration.Endpoint,
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(model))
        {
            this.LastDecision = "No loaded LM Studio model detected.";
            return null;
        }

        this.Configuration.Model = model;
        this.Configuration.Save();
        this.LastDecision = $"Detected loaded LM Studio model: {model}.";
        return model;
    }

    public string GetStatusSummary()
    {
        var mode = this.Configuration.SendReplies ? "Reply in source channel" : "Print locally";
        var mention = this.Configuration.RequireMention ? "required" : "not required";
        var alias = string.IsNullOrWhiteSpace(this.Configuration.TriggerAlias) ? "(none)" : this.Configuration.TriggerAlias;
        var channels = this.Configuration.WatchedChannelIds.Count == 0
            ? "(none)"
            : this.Configuration.GetWatchedChannelSummary();

        return $"Enabled={this.Configuration.Enabled} | Mode={mode} | Channels={channels} | Mention={mention} | Alias={alias} | Provider={this.Configuration.Provider} | Model={this.Configuration.Model}";
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        this.HandleIncomingChat("ChatMessage", type, sender.TextValue.Trim(), message.TextValue.Trim());
    }

    private void OnChatMessageUnhandled(XivChatType type, int timestamp, SeString sender, SeString message)
    {
        this.HandleIncomingChat("ChatMessageUnhandled", type, sender.TextValue.Trim(), message.TextValue.Trim());
    }

    private void HandleIncomingChat(string source, XivChatType type, string senderText, string messageText)
    {
        this.LastSeenSource = source;
        this.LastSeenType = type.ToString();
        this.LastSeenSender = string.IsNullOrWhiteSpace(senderText) ? "-" : senderText;
        this.LastSeenMessage = string.IsNullOrWhiteSpace(messageText) ? "-" : messageText;

        if (!this.Configuration.Enabled)
        {
            this.LastDecision = "Ignored: plugin is disabled.";
            return;
        }

        if (!ChatChannelRegistry.TryGetByType(type, out var channel) || channel is null)
        {
            this.LastDecision = $"Ignored: {type} is not a supported auto-reply channel.";
            return;
        }

        if (!this.Configuration.IsChannelEnabled(channel.Id))
        {
            this.LastDecision = $"Ignored: saw {channel.Label}, but it is not enabled.";
            return;
        }

        if (string.IsNullOrWhiteSpace(senderText) || string.IsNullOrWhiteSpace(messageText))
        {
            this.LastDecision = "Ignored: sender or message was empty.";
            return;
        }

        if (this.IsOwnMessage(senderText))
        {
            this.LastDecision = "Ignored: message looks like it came from your own character.";
            return;
        }

        if (this.Configuration.RequireMention && !this.IsTriggered(messageText))
        {
            this.LastDecision = "Ignored: mention/alias requirement not met.";
            return;
        }

        if (DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref this.lastReplyAtTicks), TimeSpan.Zero) < TimeSpan.FromSeconds(this.Configuration.CooldownSeconds))
        {
            this.LastDecision = $"Ignored: cooldown active for {this.Configuration.CooldownSeconds} seconds.";
            return;
        }

        var fingerprint = $"{type}|{senderText}|{messageText}";
        if (this.lastProcessedFingerprint == fingerprint &&
            DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref this.lastProcessedFingerprintAtTicks), TimeSpan.Zero) < TimeSpan.FromSeconds(5))
        {
            this.LastDecision = "Ignored: duplicate event for the same message.";
            return;
        }

        this.lastProcessedFingerprint = fingerprint;
        Interlocked.Exchange(ref this.lastProcessedFingerprintAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        var turn = new LmStudioClient.ChatTurn("user", senderText, messageText);
        this.AddTurn(channel.Id, turn);
        this.LastDecision = $"Accepted: sending {channel.Label} context to the selected provider.";

        _ = Task.Run(() => this.ProcessIncomingTurnAsync(channel, this.disposalTokenSource.Token));
    }

    private async Task ProcessIncomingTurnAsync(ChatChannelDefinition channel, CancellationToken cancellationToken)
    {
        if (!await this.requestGate.WaitAsync(0, cancellationToken))
        {
            this.LastDecision = "Ignored: another request is already running.";
            Log.Debug("Skipping chat reply because another AI request is already running.");
            return;
        }

        try
        {
            var snapshot = this.GetHistorySnapshot(channel.Id);

            string? searchContext = null;
            if (this.Configuration.UseExaSearch)
            {
                var lastUserMessage = snapshot.LastOrDefault(t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase));
                if (lastUserMessage is not null)
                {
                    searchContext = await this.exaSearchClient.SearchAsync(lastUserMessage.Content, cancellationToken);
                }
            }

            var reply = await this.lmStudioClient.CreateReplyAsync(this.Configuration, snapshot, cancellationToken, searchContext);
            var sanitizedReply = SanitizeReply(reply);
            if (string.IsNullOrWhiteSpace(sanitizedReply))
            {
                this.LastDecision = "Ignored: provider returned an empty reply.";
                return;
            }

            if (this.Configuration.ReplyDelayMilliseconds > 0)
            {
                this.LastDecision = $"Provider replied. Waiting {this.Configuration.ReplyDelayMilliseconds} ms before posting.";
                await Task.Delay(this.Configuration.ReplyDelayMilliseconds);
            }

            Interlocked.Exchange(ref this.lastReplyAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            this.AddTurn(channel.Id, new LmStudioClient.ChatTurn("assistant", "AI", sanitizedReply));

            await Framework.RunOnTick(() =>
            {
                if (this.Configuration.SendReplies)
                {
                    var outgoingReply = SanitizeReplyForGame(sanitizedReply);
                    var sent = !string.IsNullOrWhiteSpace(outgoingReply) &&
                               GameChatSender.SendMessage(channel, outgoingReply);

                    if (!sent)
                    {
                        this.LastDecision = $"Provider replied, but {channel.Label} dispatch failed. Printed locally instead.";
                        ChatGui.PrintError($"Failed to dispatch the {channel.Label} chat command. Printing the draft locally instead.", Tag);
                        ChatGui.Print(sanitizedReply, Tag);
                    }
                    else
                    {
                        this.LastDecision = $"Provider replied and the message was sent to {channel.Label}.";
                    }
                }
                else
                {
                    this.LastDecision = "Provider replied and the draft was printed locally.";
                    ChatGui.Print(sanitizedReply, Tag);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Plugin is being disposed; silently abort.
        }
        catch (Exception ex)
        {
            this.LastDecision = $"AI request failed: {ex.Message}";
            Log.Error(ex, "Failed to create AI reply.");
            await Framework.RunOnTick(() => ChatGui.PrintError($"AI request failed: {ex.Message}", Tag));
        }
        finally
        {
            this.requestGate.Release();
        }
    }

    private async Task RunManualPromptAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            var prompt = new LmStudioClient.ChatTurn("user", "Manual test", message);
            var history = this.GetHistorySnapshot();
            history.Add(prompt);

            string? searchContext = null;
            if (this.Configuration.UseExaSearch)
            {
                searchContext = await this.exaSearchClient.SearchAsync(message, cancellationToken);
            }

            var reply = await this.lmStudioClient.CreateReplyAsync(this.Configuration, history, cancellationToken, searchContext);
            await Framework.RunOnTick(() => ChatGui.Print(SanitizeReply(reply), Tag));
        }
        catch (OperationCanceledException)
        {
            // Plugin is being disposed; silently abort.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Manual AI test failed.");
            await Framework.RunOnTick(() => ChatGui.PrintError($"Manual test failed: {ex.Message}", Tag));
        }
    }

    private void HandleSendCommand(string rest)
    {
        if (!TryParseOnOff(rest, out var value))
        {
            ChatGui.PrintError("Usage: /xivaichat send on|off", Tag);
            return;
        }

        this.Configuration.SendReplies = value;
        this.Configuration.Save();
        ChatGui.Print($"SendReplies set to {value}.", Tag);
    }

    private void HandleSlotCommand(string rest)
    {
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot is < 1 or > 8)
        {
            ChatGui.PrintError("Usage: /xivaichat slot <1-8>", Tag);
            return;
        }

        this.Configuration.CwlsSlot = slot;
        this.Configuration.WatchedChannelIds = [$"cwl{slot}"];
        this.Configuration.Save();
        ChatGui.Print($"Watching only CWLS {slot}.", Tag);
    }

    private void HandleAliasCommand(string rest)
    {
        if (string.Equals(rest, "clear", StringComparison.OrdinalIgnoreCase))
        {
            this.Configuration.TriggerAlias = string.Empty;
            this.Configuration.Save();
            ChatGui.Print("Trigger alias cleared.", Tag);
            return;
        }

        this.HandleTextUpdate(rest, value => this.Configuration.TriggerAlias = value, "TriggerAlias");
    }

    private void HandleBooleanUpdate(string rest, Action<bool> setter, string label)
    {
        if (!TryParseOnOff(rest, out var value))
        {
            ChatGui.PrintError($"Usage: /xivaichat {label.ToLowerInvariant()} on|off", Tag);
            return;
        }

        setter(value);
        this.Configuration.Save();
        ChatGui.Print($"{label} set to {value}.", Tag);
    }

    private void HandleIntegerUpdate(string rest, int min, int max, Action<int> setter, string label)
    {
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            ChatGui.PrintError($"Usage: /xivaichat {label.ToLowerInvariant()} <{min}-{max}>", Tag);
            return;
        }

        setter(value);
        this.Configuration.Save();
        ChatGui.Print($"{label} set to {value}.", Tag);
    }

    private void HandleTextUpdate(string rest, Action<string> setter, string label)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            ChatGui.PrintError($"Usage: /xivaichat {label.ToLowerInvariant()} <value>", Tag);
            return;
        }

        setter(rest);
        this.Configuration.Save();
        ChatGui.Print($"{label} updated.", Tag);
    }

    private void PrintHelp()
    {
        ChatGui.Print("Commands: status | on | off | send on|off | slot <1-8> | endpoint <url> | model <name> | prompt <text> | alias <word|clear> | mention on|off | cooldown <0-600> | test <message>. Use the in-game window to pick multiple channels.", Tag);
    }

    private void PrintStatus()
    {
        ChatGui.Print($"{this.GetStatusSummary()} | Endpoint={this.Configuration.Endpoint}", Tag);
    }

    private void DrawUi()
    {
        this.WindowSystem.Draw();
    }

    private void AddTurn(string channelId, LmStudioClient.ChatTurn turn)
    {
        lock (this.historySync)
        {
            this.recentTurns.Add(new ChatHistoryEntry(channelId, turn));

            while (this.recentTurns.Count(entry => string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal)) >
                   Math.Max(1, this.Configuration.MaxHistoryMessages))
            {
                var removeIndex = this.recentTurns.FindIndex(
                    entry => string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal));
                if (removeIndex < 0)
                {
                    break;
                }

                this.recentTurns.RemoveAt(removeIndex);
            }
        }
    }

    private List<LmStudioClient.ChatTurn> GetHistorySnapshot(string? channelId = null)
    {
        lock (this.historySync)
        {
            return this.recentTurns
                .Where(entry => channelId is null || string.Equals(entry.ChannelId, channelId, StringComparison.Ordinal))
                .Select(entry => entry.Turn)
                .ToList();
        }
    }

    private bool IsOwnMessage(string sender)
    {
        if (!PlayerState.IsLoaded || string.IsNullOrWhiteSpace(PlayerState.CharacterName))
        {
            return false;
        }

        return sender.Contains(PlayerState.CharacterName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTriggered(string message)
    {
        if (!string.IsNullOrWhiteSpace(this.Configuration.TriggerAlias) &&
            message.Contains(this.Configuration.TriggerAlias, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(PlayerState.CharacterName))
        {
            return message.Contains(PlayerState.CharacterName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryParseOnOff(string value, out bool parsed)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on":
            case "true":
            case "1":
                parsed = true;
                return true;
            case "off":
            case "false":
            case "0":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }

    private string SanitizeReply(string reply)
    {
        var builder = new StringBuilder(reply.Length);
        foreach (var character in reply)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
            }
        }

        var singleLine = builder
            .ToString()
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        while (singleLine.Contains("  ", StringComparison.Ordinal))
        {
            singleLine = singleLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        singleLine = StripSpeakerPrefix(singleLine);
        singleLine = ExtractLikelyFinalReply(singleLine);

        return singleLine.Length <= MaxReplyLength
            ? singleLine
            : singleLine[..MaxReplyLength];
    }

    private string SanitizeReplyForGame(string reply)
    {
        var normalized = SanitizeReply(reply);

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsSurrogate(character))
            {
                continue;
            }

            builder.Append(character switch
            {
                '\u2019' or '\u2018' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2026' => '.',
                '\u2013' or '\u2014' => '-',
                _ => character,
            });
        }

        var cleaned = builder.ToString().Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (cleaned.StartsWith("/", StringComparison.Ordinal))
        {
            cleaned = cleaned.TrimStart('/');
        }

        return TrimToUtf8ByteCount(cleaned, MaxGameMessageBytes);
    }

    private string StripSpeakerPrefix(string text)
    {
        var prefixes = new List<string>
        {
            "AI:",
            "AI：",
            "Assistant:",
            "Assistant：",
            "Reply:",
            "Reply：",
            "Answer:",
            "Answer：",
        };

        if (PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(PlayerState.CharacterName))
        {
            prefixes.Add($"{PlayerState.CharacterName}:");
            prefixes.Add($"{PlayerState.CharacterName}：");
        }

        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text[prefix.Length..].Trim();
            }
        }

        return text;
    }

    private static string ExtractLikelyFinalReply(string text)
    {
        if (!ContainsReasoningMarker(text))
        {
            return text;
        }

        var candidates = text.Split(
            ['.', '!', '?', '\u3002', '\uFF01', '\uFF1F'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (var i = candidates.Length - 1; i >= 0; i--)
        {
            var candidate = candidates[i].Trim();
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !ContainsReasoningMarker(candidate) &&
                candidate.Length <= 80)
            {
                return candidate;
            }
        }

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i].Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && !ContainsReasoningMarker(candidate))
            {
                return candidate;
            }
        }

        return text;
    }

    private static bool ContainsReasoningMarker(string text)
    {
        var markers = new[]
        {
            "I need to",
            "Let me",
            "First,",
            "The user",
            "previous conversation",
            "I should",
            "I will",
            "user says",
            "我需要",
            "用戶說",
            "使用者說",
            "之前對話",
            "首先",
            "讓我",
            "我會",
            "這個用戶",
            "处理这个消息",
            "處理這個訊息",
        };

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimToUtf8ByteCount(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character);
            if (Encoding.UTF8.GetByteCount(builder.ToString()) > maxBytes)
            {
                builder.Length--;
                break;
            }
        }

        return builder.ToString().Trim();
    }
}
