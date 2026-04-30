using System.Globalization;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
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
    private readonly object backgroundTaskSync = new();
    private readonly object pendingReplySync = new();
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly LmStudioClient lmStudioClient = new();
    private readonly ExaSearchClient exaSearchClient = new();
    private readonly ConfigWindow configWindow;
    private readonly DraftPopupWindow draftPopupWindow;
    private readonly CancellationTokenSource disposalTokenSource = new();
    private readonly List<Task> backgroundTasks = [];
    private readonly List<PendingReply> pendingReplies = [];
    private readonly Dictionary<string, int> pendingReplyCountsByChannel = new(StringComparer.Ordinal);
    private string? lastActiveChannelId;
    private string? lastProcessedFingerprint;

    // Stored as UTC ticks so Interlocked can provide thread-safe access.
    private long lastReplyAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private long lastProcessedFingerprintAtTicks = DateTimeOffset.MinValue.UtcTicks;

    private sealed record ChatHistoryEntry(string ChannelId, LmStudioClient.ChatTurn Turn);
    private sealed record PendingReply(Guid Id, ChatChannelDefinition Channel, string ReplyText, DateTimeOffset CreatedAtUtc);

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(PluginInterface);
        this.WindowSystem = new WindowSystem("XivAiChat");
        this.configWindow = new ConfigWindow(this);
        this.draftPopupWindow = new DraftPopupWindow(this);

        this.WindowSystem.AddWindow(this.configWindow);
        this.WindowSystem.AddWindow(this.draftPopupWindow);

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

    public bool HasPendingReplies
    {
        get
        {
            lock (this.pendingReplySync)
            {
                return this.pendingReplies.Count > 0;
            }
        }
    }

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
        this.WaitForBackgroundTasks();
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
            case "lang":
            case "language":
                this.HandleLanguageCommand(rest);
                break;
            case "en":
            case "english":
                this.ApplyLanguagePreset(BuiltInPromptPresets.EnglishName);
                break;
            case "zh":
            case "tc":
            case "chinese":
                this.ApplyLanguagePreset(BuiltInPromptPresets.TraditionalChineseName);
                break;
            case "ja":
            case "jp":
            case "japanese":
                this.ApplyLanguagePreset(BuiltInPromptPresets.JapaneseName);
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
            case "after":
                this.HandleIntegerUpdate(
                    rest,
                    1,
                    20,
                    value => this.Configuration.ReplyAfterMessageCount = value,
                    "ReplyAfterMessageCount");
                break;
            case "test":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    ChatGui.PrintError("Usage: /xivaichat test <message>", Tag);
                    return;
                }

                this.StartBackgroundTask(token => this.RunManualPromptAsync(rest, token));
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

    public void ToggleConfigUi()
    {
        this.configWindow.IsOpen = !this.configWindow.IsOpen;
    }

    public void OpenMainUi()
    {
        this.configWindow.IsOpen = true;
    }

    public void OpenDraftPopup()
    {
        this.draftPopupWindow.IsOpen = true;
    }

    public void ReadSituation()
    {
        ChatChannelDefinition? channel;
        lock (this.historySync)
        {
            channel = !string.IsNullOrWhiteSpace(this.lastActiveChannelId) &&
                      ChatChannelRegistry.TryGetById(this.lastActiveChannelId, out var foundChannel)
                ? foundChannel
                : null;
        }

        if (channel is null)
        {
            this.LastDecision = "Read Situation failed: no active watched channel history is available yet.";
            ChatGui.PrintError("Read Situation failed: no active watched channel history is available yet.", Tag);
            return;
        }

        var snapshot = this.GetHistorySnapshot(channel.Id);
        if (snapshot.Count == 0)
        {
            this.LastDecision = $"Read Situation failed: {channel.Label} has no stored chat history yet.";
            ChatGui.PrintError($"Read Situation failed: {channel.Label} has no stored chat history yet.", Tag);
            return;
        }

        this.LastDecision = $"Read Situation requested for {channel.Label}.";
        this.OpenDraftPopup();
        this.StartBackgroundTask(token => this.ProcessIncomingTurnAsync(channel, token));
    }

    public void SwitchLanguagePreset(string presetName)
    {
        this.Configuration.SetActivePrompt(presetName);
        this.Configuration.Save();
        this.LastDecision = $"Language preset switched to {this.Configuration.ActivePromptPreset}.";
    }

    public void RunManualPrompt(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ChatGui.PrintError("Enter a test message first.", Tag);
            return;
        }

        this.StartBackgroundTask(token => this.RunManualPromptAsync(message, token));
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
        var approval = this.Configuration.RequireApprovalBeforeReply ? "manual" : "auto";
        var mention = this.Configuration.RequireMention ? "required" : "not required";
        var alias = string.IsNullOrWhiteSpace(this.Configuration.TriggerAlias) ? "(none)" : this.Configuration.TriggerAlias;
        var channels = this.Configuration.WatchedChannelIds.Count == 0
            ? "(none)"
            : this.Configuration.GetWatchedChannelSummary();

        return $"Enabled={this.Configuration.Enabled} | Mode={mode} | Approval={approval} | ReplyAfter={this.Configuration.ReplyAfterMessageCount} | Channels={channels} | Mention={mention} | Alias={alias} | Provider={this.Configuration.Provider} | Model={this.Configuration.Model}";
    }

    public IReadOnlyList<PendingReplyViewModel> GetPendingReplies()
    {
        lock (this.pendingReplySync)
        {
            return this.pendingReplies
                .Select(reply => new PendingReplyViewModel(
                    reply.Id,
                    reply.Channel.Label,
                    reply.Channel.Id,
                    reply.ReplyText,
                    reply.CreatedAtUtc))
                .ToArray();
        }
    }

    public void ApprovePendingReply(Guid id)
    {
        PendingReply? pendingReply = null;
        lock (this.pendingReplySync)
        {
            var index = this.pendingReplies.FindIndex(reply => reply.Id == id);
            if (index < 0)
            {
                return;
            }

            pendingReply = this.pendingReplies[index];
            this.pendingReplies.RemoveAt(index);
        }

        if (pendingReply is null)
        {
            return;
        }

        this.UpdateDraftPopupVisibility();
        this.StartBackgroundTask(_ => this.PublishReplyAsync(pendingReply.Channel, pendingReply.ReplyText, CancellationToken.None));
    }

    public void DismissPendingReply(Guid id)
    {
        lock (this.pendingReplySync)
        {
            this.pendingReplies.RemoveAll(reply => reply.Id == id);
        }

        this.UpdateDraftPopupVisibility();
        this.LastDecision = "Pending AI draft was dismissed.";
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        this.HandleIncomingChat("ChatMessage", message.LogKind, message.Sender.TextValue.Trim(), message.Message.TextValue.Trim());
    }

    private void OnChatMessageUnhandled(IChatMessage message)
    {
        this.HandleIncomingChat("ChatMessageUnhandled", message.LogKind, message.Sender.TextValue.Trim(), message.Message.TextValue.Trim());
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

        var fingerprint = $"{type}|{senderText}|{messageText}";
        if (this.lastProcessedFingerprint == fingerprint &&
            DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref this.lastProcessedFingerprintAtTicks), TimeSpan.Zero) < TimeSpan.FromSeconds(5))
        {
            this.LastDecision = "Ignored: duplicate event for the same message.";
            return;
        }

        this.lastProcessedFingerprint = fingerprint;
        Interlocked.Exchange(ref this.lastProcessedFingerprintAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        if (this.IsOwnMessage(senderText))
        {
            this.AddObservedTurn(channel.Id, senderText, messageText);
            this.LastDecision = "Ignored: message looks like it came from your own character.";
            return;
        }

        this.AddObservedTurn(channel.Id, senderText, messageText);

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

        var replyAfterCount = Math.Max(1, this.Configuration.ReplyAfterMessageCount);
        var acceptedCount = this.IncrementPendingReplyCount(channel.Id);
        if (acceptedCount < replyAfterCount)
        {
            this.LastDecision = $"Accepted: stored {acceptedCount}/{replyAfterCount} messages for {channel.Label} before generating a reply.";
            return;
        }

        this.ResetPendingReplyCount(channel.Id);
        this.LastDecision = $"Accepted: sending {channel.Label} context to the selected provider.";

        this.StartBackgroundTask(token => this.ProcessIncomingTurnAsync(channel, token));
    }

    private async Task ProcessIncomingTurnAsync(ChatChannelDefinition channel, CancellationToken cancellationToken)
    {
        var acquiredGate = false;
        if (!await this.requestGate.WaitAsync(0, cancellationToken))
        {
            this.LastDecision = "Ignored: another request is already running.";
            Log.Debug("Skipping chat reply because another AI request is already running.");
            return;
        }

        acquiredGate = true;

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

            if (this.Configuration.RequireApprovalBeforeReply)
            {
                this.EnqueuePendingReply(channel, sanitizedReply);
                this.LastDecision = $"Draft ready for {channel.Label}. Use the floating draft window or the config window to approve it.";
                return;
            }

            if (this.Configuration.ReplyDelayMilliseconds > 0)
            {
                this.LastDecision = $"Provider replied. Waiting {this.Configuration.ReplyDelayMilliseconds} ms before posting.";
                await Task.Delay(this.Configuration.ReplyDelayMilliseconds, cancellationToken);
            }

            await this.PublishReplyAsync(channel, sanitizedReply, cancellationToken);
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
            if (acquiredGate)
            {
                try
                {
                    this.requestGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Plugin disposal can win the race after cancellation.
                }
            }
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

    private void HandleLanguageCommand(string rest)
    {
        var presetName = rest.Trim().ToLowerInvariant() switch
        {
            "en" or "english" => BuiltInPromptPresets.EnglishName,
            "zh" or "tc" or "traditional chinese" or "chinese" => BuiltInPromptPresets.TraditionalChineseName,
            "ja" or "jp" or "japanese" => BuiltInPromptPresets.JapaneseName,
            _ => null,
        };

        if (presetName is null)
        {
            ChatGui.PrintError("Usage: /xivaichat lang en|zh|ja", Tag);
            return;
        }

        this.ApplyLanguagePreset(presetName);
    }

    private void ApplyLanguagePreset(string presetName)
    {
        this.SwitchLanguagePreset(presetName);
        ChatGui.Print($"Language preset set to {this.Configuration.ActivePromptPreset}.", Tag);
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
        ChatGui.Print("Commands: status | on | off | send on|off | slot <1-8> | endpoint <url> | model <name> | prompt <text> | lang en|zh|ja | en | zh | ja | alias <word|clear> | mention on|off | cooldown <0-600> | after <1-20> | test <message>. Use the in-game window to pick multiple channels.", Tag);
    }

    private void PrintStatus()
    {
        ChatGui.Print($"{this.GetStatusSummary()} | Endpoint={this.Configuration.Endpoint}", Tag);
    }

    private void DrawUi()
    {
        this.draftPopupWindow.IsOpen = this.Configuration.ShowDraftPopup || this.draftPopupWindow.IsOpen;
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

    private void AddObservedTurn(string channelId, string senderText, string messageText)
    {
        var turn = new LmStudioClient.ChatTurn("user", senderText, messageText);
        this.AddTurn(channelId, turn);
        lock (this.historySync)
        {
            this.lastActiveChannelId = channelId;
        }
    }

    private bool IsOwnMessage(string sender)
    {
        if (!PlayerState.IsLoaded || string.IsNullOrWhiteSpace(PlayerState.CharacterName))
        {
            return false;
        }

        return StartsWithName(sender, PlayerState.CharacterName);
    }

    private bool IsTriggered(string message)
    {
        if (!string.IsNullOrWhiteSpace(this.Configuration.TriggerAlias) &&
            ContainsDelimitedToken(message, this.Configuration.TriggerAlias))
        {
            return true;
        }

        if (PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(PlayerState.CharacterName))
        {
            return message.Contains(PlayerState.CharacterName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void StartBackgroundTask(Func<CancellationToken, Task> work)
    {
        Task? task = null;
        task = Task.Run(
            async () =>
            {
                try
                {
                    await work(this.disposalTokenSource.Token);
                }
                finally
                {
                    lock (this.backgroundTaskSync)
                    {
                        if (task is not null)
                        {
                            this.backgroundTasks.Remove(task);
                        }
                    }
                }
            },
            CancellationToken.None);

        lock (this.backgroundTaskSync)
        {
            this.backgroundTasks.Add(task);
        }
    }

    private void WaitForBackgroundTasks()
    {
        Task[] snapshot;
        lock (this.backgroundTaskSync)
        {
            snapshot = this.backgroundTasks.ToArray();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(snapshot, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Expected during plugin unload.
        }
    }

    private int IncrementPendingReplyCount(string channelId)
    {
        lock (this.historySync)
        {
            this.pendingReplyCountsByChannel.TryGetValue(channelId, out var currentCount);
            currentCount++;
            this.pendingReplyCountsByChannel[channelId] = currentCount;
            return currentCount;
        }
    }

    private void ResetPendingReplyCount(string channelId)
    {
        lock (this.historySync)
        {
            this.pendingReplyCountsByChannel[channelId] = 0;
        }
    }

    private static bool StartsWithName(string text, string name)
    {
        if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsTokenBoundary(text, name.Length);
    }

    private static bool ContainsDelimitedToken(string text, string token)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var index = text.IndexOf(token, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (IsTokenBoundary(text, index - 1) &&
                IsTokenBoundary(text, index + token.Length))
            {
                return true;
            }

            searchStart = index + token.Length;
        }

        return false;
    }

    private static bool IsTokenBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
        {
            return true;
        }

        return !char.IsLetterOrDigit(text[index]);
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

    private void EnqueuePendingReply(ChatChannelDefinition channel, string replyText)
    {
        lock (this.pendingReplySync)
        {
            this.pendingReplies.Add(new PendingReply(Guid.NewGuid(), channel, replyText, DateTimeOffset.UtcNow));
        }

        this.UpdateDraftPopupVisibility(openWindow: true);
    }

    private void UpdateDraftPopupVisibility(bool openWindow = false)
    {
        if (openWindow || this.Configuration.ShowDraftPopup)
        {
            this.draftPopupWindow.IsOpen = true;
        }
    }

    private async Task PublishReplyAsync(ChatChannelDefinition channel, string sanitizedReply, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public sealed record PendingReplyViewModel(
        Guid Id,
        string ChannelLabel,
        string ChannelId,
        string ReplyText,
        DateTimeOffset CreatedAtUtc);
}
