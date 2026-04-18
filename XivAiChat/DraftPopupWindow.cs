using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;

namespace XivAiChat;

public sealed class DraftPopupWindow : Window
{
    private readonly Plugin plugin;

    public DraftPopupWindow(Plugin plugin)
        : base("Reply Drafts###XivAiChatDraftPopup")
    {
        this.plugin = plugin;
        this.Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        this.Size = new Vector2(420f, 240f);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320f, 180f),
            MaximumSize = new Vector2(620f, 520f),
        };

        if (plugin.Configuration.DraftPopupPositionX >= 0f &&
            plugin.Configuration.DraftPopupPositionY >= 0f)
        {
            this.Position = new Vector2(
                plugin.Configuration.DraftPopupPositionX,
                plugin.Configuration.DraftPopupPositionY);
        }
        else
        {
            this.Position = new Vector2(24f, 420f);
        }

        this.PositionCondition = ImGuiCond.FirstUseEver;
        this.RespectCloseHotkey = false;
    }

    public override void Draw()
    {
        var pendingReplies = this.plugin.GetPendingReplies()
            .OrderByDescending(reply => reply.CreatedAtUtc)
            .ToArray();

        this.DrawTopBar();
        ImGui.Separator();

        if (pendingReplies.Length == 0)
        {
            ImGui.TextWrapped("No pending drafts right now.");
            ImGui.TextDisabled("This window opens automatically when approval is required and a draft is ready.");
            ImGui.Spacing();
            if (ImGui.Button("Read Situation"))
            {
                this.plugin.ReadSituation();
            }

            this.SaveWindowPosition();
            return;
        }

        ImGui.TextDisabled(pendingReplies.Length == 1
            ? "1 pending draft"
            : $"{pendingReplies.Length} pending drafts");
        if (ImGui.Button("Read Situation"))
        {
            this.plugin.ReadSituation();
        }

        ImGui.Separator();

        var availableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 8f);
        if (ImGui.BeginChild("drafts", new Vector2(0f, availableHeight), false))
        {
            foreach (var pendingReply in pendingReplies)
            {
                ImGui.PushID(pendingReply.Id.ToString());
                ImGui.TextDisabled($"{pendingReply.ChannelLabel} • {pendingReply.CreatedAtUtc.ToLocalTime():HH:mm:ss}");
                ImGui.TextWrapped(pendingReply.ReplyText);

                if (ImGui.Button("OK"))
                {
                    this.plugin.ApprovePendingReply(pendingReply.Id);
                }

                ImGui.SameLine();
                if (ImGui.Button("Dismiss"))
                {
                    this.plugin.DismissPendingReply(pendingReply.Id);
                }

                ImGui.Separator();
                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        this.SaveWindowPosition();
    }

    private void DrawTopBar()
    {
        var gearWidth = ImGui.GetFrameHeight();
        var englishWidth = ImGui.CalcTextSize("EN").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var chineseWidth = ImGui.CalcTextSize("中").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var japaneseWidth = ImGui.CalcTextSize("日").X + (ImGui.GetStyle().FramePadding.X * 2f);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var controlsWidth = englishWidth + spacing + chineseWidth + spacing + japaneseWidth + spacing + gearWidth;
        var controlsX = MathF.Max(0f, ImGui.GetWindowContentRegionMax().X - controlsWidth);
        ImGui.SetCursorPosX(controlsX);
        this.DrawHeaderLanguageButton("EN", BuiltInPromptPresets.EnglishName, "Switch to English");
        ImGui.SameLine(0f, spacing);
        this.DrawHeaderLanguageButton("中", BuiltInPromptPresets.TraditionalChineseName, "Switch to Traditional Chinese");
        ImGui.SameLine(0f, spacing);
        this.DrawHeaderLanguageButton("日", BuiltInPromptPresets.JapaneseName, "Switch to Japanese");
        ImGui.SameLine(0f, spacing);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
        {
            this.plugin.ToggleConfigUi();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Toggle settings window");
        }
    }

    private void DrawHeaderLanguageButton(string label, string presetName, string tooltip)
    {
        if (ImGui.SmallButton(label))
        {
            this.plugin.SwitchLanguagePreset(presetName);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
    }

    private void SaveWindowPosition()
    {
        var position = ImGui.GetWindowPos();
        if (MathF.Abs(this.plugin.Configuration.DraftPopupPositionX - position.X) < 0.5f &&
            MathF.Abs(this.plugin.Configuration.DraftPopupPositionY - position.Y) < 0.5f)
        {
            return;
        }

        this.plugin.Configuration.DraftPopupPositionX = position.X;
        this.plugin.Configuration.DraftPopupPositionY = position.Y;
        this.plugin.SaveConfiguration();
    }
}
