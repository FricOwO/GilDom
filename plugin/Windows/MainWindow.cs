using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;

namespace GilDom.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private string pasteCode = "";
    private int requestRole;
    private string selectedCode = "";
    private string nickname = "";
    private string template = "";
    private string description = "";
    private int tax;
    private int cap;
    private int collectAmount;
    private int iconId = MoodlePaste.DefaultIconId;
    private string moodlePasteNote = "";
    private int statusType = 1;
    private bool moodleEnabled = true;
    private string loadedCode = "";
    private bool showSettings;
    private bool peopleCollapsed;
    private string secretDraft = "";
    private bool secretVisible;
    private bool confirmRotate;
    private string confirmUnlink = "";
    private int seenSecretVersion;
    private string copiedLabel = "";
    private DateTime copiedUntil;
    private string savedNote = "";
    private DateTime savedUntil;
    private readonly Dictionary<Guid, ExtraDraft> extraDrafts = [];
    private readonly List<Guid> pendingMoodles = [];
    private readonly IFontHandle codeFontLarge;
    private readonly IFontHandle codeFontMedium;
    private static readonly Vector4 Gold = new(1f, 0.84f, 0.42f, 1f);

    private sealed class ExtraDraft
    {
        public string Title = "";
        public string Description = "";
        public int IconId = MoodlePaste.DefaultIconId;
        public int StatusType = 1;
        public bool Enabled = true;
    }

    public MainWindow() : base("GilDom###GilDomMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 480),
            MaximumSize = new Vector2(1400, 1000),
        };
        TitleBarButtons =
        [
            new TitleBarButton
            {
                Icon = FontAwesomeIcon.Cog,
                IconColor = new Vector4(0.98f, 0.75f, 0.84f, 1f),
                Click = ToggleSettings,
                ShowTooltip = () => ImGui.SetTooltip("Settings"),
            },
        ];
        var atlas = Plugin.PluginInterface.UiBuilder.FontAtlas;
        codeFontLarge = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(48)));
        codeFontMedium = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(32)));
    }

    private void ToggleSettings(ImGuiMouseButton button)
    {
        if (button != ImGuiMouseButton.Left)
            return;

        showSettings = !showSettings;
        if (!showSettings)
            return;

        secretDraft = Plugin.Configuration.LinkSecret;
        seenSecretVersion = Plugin.Session.Relay.SecretVersion;
    }

    public void Dispose()
    {
        codeFontLarge.Dispose();
        codeFontMedium.Dispose();
    }

    public override void PreDraw()
    {
        if (TitleBarButtons is { Count: > 0 })
        {
            TitleBarButtons[0].IconColor = showSettings
                ? new Vector4(1f, 0.96f, 0.97f, 1f)
                : new Vector4(0.98f, 0.75f, 0.84f, 1f);
        }

        Theme.Push();
    }

    public override void PostDraw() => Theme.Pop();

    public override void Draw()
    {
        if (!string.IsNullOrEmpty(selectedCode) && Plugin.Configuration.Find(selectedCode) == null)
        {
            selectedCode = "";
            loadedCode = "";
        }

        DrawIdentity();
        if (showSettings && Plugin.Session.Relay.SecretVersion != seenSecretVersion)
        {
            seenSecretVersion = Plugin.Session.Relay.SecretVersion;
            secretDraft = Plugin.Configuration.LinkSecret;
        }

        if (showSettings)
        {
            DrawSettings();
            return;
        }

        var height = ImGui.GetContentRegionAvail().Y;
        if (peopleCollapsed)
        {
            var pad = 4f;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, 8f));
            var width = IconButtonWidth(FontAwesomeIcon.ChevronRight) + pad * 2f + 4f;
            ImGui.BeginChild("people-toggle", new Vector2(width, height), true);
            if (ImGuiComponents.IconButton("##show-people", FontAwesomeIcon.ChevronRight))
                peopleCollapsed = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show people");
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.SameLine();
        }
        else
        {
            ImGui.BeginChild("friend-column", new Vector2(300f, height), true);
            if (ImGuiComponents.IconButton("##hide-people", FontAwesomeIcon.ChevronLeft))
                peopleCollapsed = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hide people");
            DrawFriendList();
            ImGui.EndChild();
            ImGui.SameLine();
        }

        ImGui.BeginChild("friend-detail", new Vector2(0f, height), true);
        DrawDetail();
        ImGui.EndChild();
    }

    private void DrawIdentity()
    {
        if (!Plugin.Configuration.IsBound)
            CenterText("Log in with this character");
        else if (Configuration.IsFriendCode(Plugin.Configuration.FriendCode))
            DrawFriendCode(Plugin.Configuration.FriendCode);
        else
            CenterText("Assigned on the first connection");

        ImGui.Spacing();
        if (showSettings)
            return;

        ImGui.Separator();
        ImGui.SetNextItemWidth(140f);
        ImGui.InputTextWithHint("##paste", "Friend code", ref pasteCode, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        var roles = new[] { "I will be the dom", "I will be the sub" };
        ImGui.Combo("##request-role", ref requestRole, roles, roles.Length);
        ImGui.SameLine();
        if (ImGui.Button("Send request") && pasteCode.Trim().Length > 0)
        {
            var role = requestRole == 0 ? PartnerRole.Dom : PartnerRole.Sub;
            Plugin.Session.SendFriendRequest(pasteCode.Trim(), role);
            pasteCode = "";
        }

        if (!string.IsNullOrWhiteSpace(Plugin.Session.LastError))
            ImGui.TextWrapped(Plugin.Session.LastError);
        ImGui.Spacing();
    }

    private void DrawSettings()
    {
        if (ImGui.Button("Back"))
            showSettings = false;
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Gold, "Settings");

        DrawSectionTitle("Server");
        var url = ServerAddress.Url;
        var iconWidth = IconButtonWidth(FontAwesomeIcon.Copy);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Math.Max(1f, ImGui.GetContentRegionAvail().X - iconWidth - spacing));
        ImGui.InputText("##server", ref url, url.Length, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##copy-server", FontAwesomeIcon.Copy))
            Copy("server", ServerAddress.Url);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Copied("server") ? "Copied" : "Copy");
        DrawHint("GilDom always uses this server. It cannot be changed.");
        DrawConnection();

        DrawSectionTitle("Secret");
        DrawHint("The server knows this character by this secret, and stores only a hash. A new secret keeps your friend code and your links. The previous one stops working, including on any other computer.");
        var flags = secretVisible ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##secret", ref secretDraft, 80, flags);
        ImGui.Checkbox("Show secret", ref secretVisible);
        if (confirmRotate)
            ImGui.TextWrapped("This replaces the secret. The one you have now stops working.");
        if (!Plugin.Configuration.IsBound)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save secret"))
            Plugin.Session.ChangeSecret(secretDraft);
        ImGui.SameLine();
        if (ImGui.Button(Copied("secret") ? "Copied" : "Copy secret") && secretDraft.Length > 0)
            Copy("secret", secretDraft);
        ImGui.SameLine(0f, 24f);
        if (confirmRotate)
        {
            if (ImGui.Button("Replace the secret"))
            {
                confirmRotate = false;
                Plugin.Session.RequestNewSecret();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                confirmRotate = false;
        }
        else if (ImGui.Button("Request a new secret"))
        {
            confirmRotate = true;
        }

        if (!Plugin.Configuration.IsBound)
            ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(Plugin.Session.Relay.SecretStatus))
            ImGui.TextWrapped(Plugin.Session.Relay.SecretStatus);
        if (!string.IsNullOrWhiteSpace(Plugin.Session.LastError))
            ImGui.TextWrapped(Plugin.Session.LastError);

        DrawSectionTitle("Gil counter");
        if (!Plugin.Configuration.IsBound)
            ImGui.BeginDisabled();
        var rewrite = Plugin.Configuration.RewriteGilDisplay;
        if (ImGui.Checkbox("Cap the gil counter", ref rewrite) && Plugin.Configuration.IsBound)
        {
            Plugin.Configuration.RewriteGilDisplay = rewrite;
            Plugin.Configuration.Save();
        }

        if (!Plugin.Configuration.IsBound)
            ImGui.EndDisabled();
        DrawHint("While you are the sub, the gil counter shows your allowance instead of the full amount.");
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Gold, title);
        ImGui.Separator();
    }

    private static void DrawHint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.72f, 0.52f, 0.60f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static float IconButtonWidth(FontAwesomeIcon icon)
    {
        var width = ImGui.GetFrameHeight();
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var measured = ImGui.CalcTextSize(icon.ToIconString()).X + ImGui.GetStyle().FramePadding.X * 2f;
            if (measured > width)
                width = measured;
        }

        return width;
    }

    private void DrawFriendList()
    {
        var style = ImGui.GetStyle();
        var statusHeight = ImGui.CalcTextSize(Plugin.Session.Relay.Status, false, ImGui.GetContentRegionAvail().X).Y;
        var footer = style.ItemSpacing.Y + statusHeight + style.ItemSpacing.Y + ImGui.GetFrameHeight() + 8f;
        var listHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y - footer);
        ImGui.BeginChild("friend-list", new Vector2(0f, listHeight), false);
        var friends = Plugin.Configuration.Friends
            .OrderBy(Rank)
            .ThenBy(friend => friend.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (friends.Count == 0)
            ImGui.TextWrapped("No one here yet. Paste their friend code above.");

        foreach (var friend in friends)
            DrawFriendRow(friend);

        ImGui.EndChild();
        ImGui.Separator();
        DrawConnection();
        if (ImGui.Button("Sync now"))
            Plugin.Session.Relay.SyncNow();
    }

    private void DrawFriendRow(FriendLink friend)
    {
        var line = ImGui.GetTextLineHeight();
        var height = line * 2f + 8f;
        var pos = ImGui.GetCursorScreenPos();
        if (ImGui.Selectable($"##friend-{friend.Code}", selectedCode == friend.Code, ImGuiSelectableFlags.None, new Vector2(0f, height)))
            selectedCode = friend.Code;

        var draw = ImGui.GetWindowDrawList();
        draw.AddText(pos + new Vector2(8f, 2f), ImGui.GetColorU32(ImGuiCol.Text), friend.DisplayName);
        draw.AddText(pos + new Vector2(8f, line + 3f), ImGui.GetColorU32(ImGuiCol.TextDisabled), $"{friend.Code}   {ListDetail(friend)}");
    }

    private static void DrawConnection()
    {
        var color = Plugin.Session.Relay.IsConnected
            ? new Vector4(0.96f, 0.62f, 0.76f, 1f)
            : new Vector4(0.72f, 0.52f, 0.60f, 1f);
        ImGui.TextColored(color, Plugin.Session.Relay.Status);
    }

    private void DrawDetail()
    {
        var friend = Plugin.Configuration.Find(selectedCode);
        if (friend == null)
        {
            ImGui.TextWrapped(Plugin.Configuration.Friends.Count == 0
                ? "When someone accepts, their dynamic shows up here."
                : "Select someone in the list.");
            return;
        }

        if (friend.State == LinkState.PendingIn)
        {
            var offer = RoleName(friend.IncomingOffer);
            ImGui.TextWrapped(friend.IncomingOffer == PartnerRole.Unset
                ? $"{friend.DisplayName} sent a request without a role. Ask them to send it again."
                : $"{friend.DisplayName} wants to be the {offer.ToLowerInvariant()}. You would be the {RoleName(Opposite(friend.IncomingOffer)).ToLowerInvariant()}.");
            if (friend.IncomingOffer != PartnerRole.Unset && ImGui.Button("Accept"))
                Plugin.Session.Accept(friend);
            ImGui.SameLine();
            if (ImGui.Button("Decline"))
            {
                Plugin.Session.Decline(friend);
                selectedCode = "";
            }

            return;
        }

        if (friend.State == LinkState.PendingOut)
        {
            ImGui.TextWrapped($"Waiting for them to accept. You asked to be the {RoleName(friend.MyRole).ToLowerInvariant()}.");
            if (ImGui.Button("Cancel request"))
            {
                Plugin.Session.Unlink(friend);
                selectedCode = "";
            }

            return;
        }

        if (!ImGui.BeginTabBar("person-tabs"))
            return;

        if (ImGui.BeginTabItem("Dynamics"))
        {
            DrawDynamics(friend);
            ImGui.EndTabItem();
        }

        if (friend.IsActive && friend.MyRole == PartnerRole.Dom)
        {
            if (ImGui.BeginTabItem("Tax"))
            {
                DrawTax(friend);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Allowance"))
            {
                DrawAllowance(friend);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Moodles"))
            {
                DrawMoodles(friend);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Collect"))
            {
                DrawCollect(friend);
                ImGui.EndTabItem();
            }
        }

        ImGui.EndTabBar();
    }

    private void DrawDynamics(FriendLink friend)
    {
        LoadEditors(friend);
        var saveWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - saveWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##name", "Name", ref nickname, 64);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##save-name", FontAwesomeIcon.Save))
        {
            Plugin.Session.SetNickname(friend, nickname);
            MarkSaved("Name saved.");
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Save name");
        DrawSaved();

        ImGui.Spacing();
        if (friend.IsActive)
            ImGui.TextColored(new Vector4(0.95f, 0.55f, 0.72f, 1f), $"You are the {RoleName(friend.MyRole).ToLowerInvariant()}. They are the {RoleName(friend.TheirRole).ToLowerInvariant()}.");
        else
            ImGui.TextWrapped("This link has no active dynamic yet. One of you asks, and the other accepts.");

        if (friend.MyRole == PartnerRole.Sub && friend.TributeRequested)
        {
            ImGui.Spacing();
            var amount = friend.CollectRequest > 0 ? friend.CollectRequest : friend.OwedGil;
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), $"Hand over {GilMath.Format(amount)} gil.");
        }

        ImGui.Spacing();
        ImGui.Text($"Tax Rate: {friend.TaxPercent}%");
        ImGui.Text($"Allowance: {GilMath.Format(friend.GilCap)}");
        ImGui.Spacing();
        DrawRoleChange(friend);
        ImGui.Spacing();
        if (confirmUnlink == friend.Code)
        {
            ImGui.TextWrapped($"Unlink {friend.DisplayName}? This pair stops, including the moodles.");
            if (ImGui.Button("Unlink"))
            {
                confirmUnlink = "";
                Plugin.Session.Unlink(friend);
                selectedCode = "";
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##unlink"))
                confirmUnlink = "";
        }
        else if (ImGui.Button("Unlink"))
        {
            confirmUnlink = friend.Code;
        }

        if (friend.MyRole == PartnerRole.Sub)
        {
            ImGui.Spacing();
            DrawRecent(friend);
        }
    }

    private static void DrawRecent(FriendLink friend)
    {
        ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        if (!ImGui.CollapsingHeader("Recent Activity"))
            return;

        if (friend.Ledger.Count == 0)
        {
            ImGui.TextDisabled("Nothing yet.");
            return;
        }

        var start = Math.Max(0, friend.Ledger.Count - 8);
        for (var i = friend.Ledger.Count - 1; i >= start; i--)
        {
            var line = friend.Ledger[i];
            ImGui.TextWrapped($"{line.When:t}   {line.Text}");
        }
    }

    private static void DrawRoleChange(FriendLink friend)
    {
        if (friend.MyProposal != PartnerRole.Unset)
        {
            ImGui.TextWrapped($"Waiting for them to accept you as the {RoleName(friend.MyProposal).ToLowerInvariant()}.");
            if (ImGui.Button("Cancel role request"))
                Plugin.Session.CancelProposal(friend);
            return;
        }

        if (friend.TheirProposal != PartnerRole.Unset)
        {
            var mine = Opposite(friend.TheirProposal);
            ImGui.TextWrapped($"They asked to be the {RoleName(friend.TheirProposal).ToLowerInvariant()}. You would be the {RoleName(mine).ToLowerInvariant()}.");
            if (ImGui.Button("Accept role"))
                Plugin.Session.AcceptProposal(friend);
            ImGui.SameLine();
            if (ImGui.Button("Decline role"))
                Plugin.Session.DeclineProposal(friend);
            return;
        }

        if (friend.MyRole != PartnerRole.Dom && ImGui.Button("Ask to be the dom"))
            Plugin.Session.ProposeRole(friend, PartnerRole.Dom);
        if (friend.MyRole != PartnerRole.Sub && ImGui.Button("Ask to be the sub"))
            Plugin.Session.ProposeRole(friend, PartnerRole.Sub);
    }

    private void DrawTax(FriendLink friend)
    {
        LoadEditors(friend);
        ImGui.Text("Tax");
        ImGui.SliderInt("Tax %", ref tax, 0, 100);
        ImGui.SameLine();
        if (ImGui.Button("Save##tax"))
        {
            friend.TaxPercent = Math.Clamp(tax, 0, 100);
            tax = friend.TaxPercent;
            Plugin.Session.PushDynamics(friend);
            MarkSaved("Tax saved.");
        }

        DrawSaved();
    }

    private void DrawAllowance(FriendLink friend)
    {
        LoadEditors(friend);
        ImGui.Text("Allowance");
        ImGui.SetNextItemWidth(180f);
        ImGui.InputInt("Max gil", ref cap);
        if (cap < 0)
            cap = 0;
        ImGui.SameLine();
        if (ImGui.Button("Save##allowance"))
        {
            friend.GilCap = cap;
            Plugin.Session.PushDynamics(friend);
            MarkSaved("Allowance saved.");
        }

        DrawSaved();
    }

    private void DrawMoodles(FriendLink friend)
    {
        LoadEditors(friend);
        DrawToken("<gilowed>", "Everything they still hand over.");
        DrawToken("<taxgil>", "The tax percent.");
        DrawToken("<maxgil>", "Their allowance. Blank when it is 0.");
        ImGui.Spacing();
        ImGui.Checkbox("Show this moodle", ref moodleEnabled);
        ImGui.InputTextWithHint("##title", "Title", ref template, 160);
        ImGui.InputTextMultiline("##text", ref description, 800, new Vector2(-1f, 72f));
        ImGui.SetNextItemWidth(110f);
        ImGui.InputInt("Icon", ref iconId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        ImGui.Combo("Kind", ref statusType, MoodleKinds, MoodleKinds.Length);
        if (ImGui.Button("Paste"))
            moodlePasteNote = PasteIntoMain() ? "Pasted. Save to send it." : moodlePasteNote;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy a moodle in Moodles, then paste it here.");
        ImGui.SameLine();
        if (ImGui.Button("Save##moodle"))
        {
            friend.MoodleEnabled = moodleEnabled;
            friend.MoodleTemplate = template;
            friend.MoodleDescription = description;
            friend.MoodleIconId = iconId;
            friend.MoodleStatusType = statusType;
            Plugin.Session.PushDynamics(friend);
            MarkSaved("Moodle saved.");
        }

        DrawSaved();
        if (moodlePasteNote.Length > 0)
            ImGui.TextWrapped(moodlePasteNote);
        ImGui.TextDisabled("The sub must allow moodles from other plugins.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Other moodles");
        for (var i = 0; i < friend.ExtraMoodles.Count; i++)
        {
            var moodle = friend.ExtraMoodles[i];
            var draft = DraftFor(moodle);
            var action = DrawExtraCard(moodle.Id.ToString(), draft);
            if (action == CardAction.Save)
            {
                moodle.Title = draft.Title;
                moodle.Description = draft.Description;
                moodle.IconId = draft.IconId;
                moodle.StatusType = draft.StatusType;
                moodle.Enabled = draft.Enabled;
                Plugin.Session.PushDynamics(friend);
                MarkSaved("Moodle saved.");
            }
            else if (action == CardAction.Remove)
            {
                extraDrafts.Remove(moodle.Id);
                friend.ExtraMoodles.RemoveAt(i);
                Plugin.Session.PushDynamics(friend);
                break;
            }
        }

        for (var i = 0; i < pendingMoodles.Count; i++)
        {
            var id = pendingMoodles[i];
            var action = DrawExtraCard(id.ToString(), extraDrafts[id]);
            if (action == CardAction.Save)
            {
                var draft = extraDrafts[id];
                friend.ExtraMoodles.Add(new CustomMoodle
                {
                    Id = id,
                    Title = draft.Title,
                    Description = draft.Description,
                    IconId = draft.IconId,
                    StatusType = draft.StatusType,
                    Enabled = draft.Enabled,
                });
                pendingMoodles.RemoveAt(i);
                Plugin.Session.PushDynamics(friend);
                MarkSaved("Moodle saved.");
                break;
            }

            if (action == CardAction.Remove)
            {
                pendingMoodles.RemoveAt(i);
                extraDrafts.Remove(id);
                break;
            }
        }

        if (ImGui.Button("Add"))
        {
            var id = Guid.NewGuid();
            pendingMoodles.Add(id);
            extraDrafts[id] = new ExtraDraft();
        }

        ImGui.SameLine();
        if (ImGui.Button("Paste another"))
        {
            if (MoodlePaste.TryRead(ImGui.GetClipboardText(), out var pasted, out var error))
            {
                var id = Guid.NewGuid();
                pendingMoodles.Add(id);
                extraDrafts[id] = new ExtraDraft
                {
                    Title = pasted.Title,
                    Description = pasted.Description,
                    IconId = pasted.IconId,
                    StatusType = pasted.StatusType,
                };
                moodlePasteNote = "Pasted. Save that moodle to send it.";
            }
            else
            {
                moodlePasteNote = error;
            }
        }
    }

    private void DrawToken(string token, string meaning)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(token);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton($"##copy-{token}", FontAwesomeIcon.Copy))
            Copy(token, token);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Copied(token) ? "Copied" : "Copy");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(meaning);
    }

    private static CardAction DrawExtraCard(string id, ExtraDraft draft)
    {
        var action = CardAction.None;
        ImGui.PushID(id);
        ImGui.InputTextWithHint("##title", "Title", ref draft.Title, 160);
        ImGui.InputTextMultiline("##text", ref draft.Description, 800, new Vector2(-1f, 56f));
        ImGui.SetNextItemWidth(110f);
        ImGui.InputInt("Icon", ref draft.IconId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        ImGui.Combo("Kind", ref draft.StatusType, MoodleKinds, MoodleKinds.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Show", ref draft.Enabled);
        if (ImGui.Button("Save"))
            action = CardAction.Save;
        ImGui.SameLine();
        if (ImGui.Button("Remove"))
            action = CardAction.Remove;
        ImGui.Separator();
        ImGui.PopID();
        return action;
    }

    private enum CardAction
    {
        None,
        Save,
        Remove,
    }

    private static readonly string[] MoodleKinds = ["Positive", "Negative", "Special"];

    private bool PasteIntoMain()
    {
        if (!MoodlePaste.TryRead(ImGui.GetClipboardText(), out var pasted, out var error))
        {
            moodlePasteNote = error;
            return false;
        }

        template = pasted.Title;
        description = pasted.Description;
        iconId = pasted.IconId;
        statusType = pasted.StatusType;
        return true;
    }

    private void DrawCollect(FriendLink friend)
    {
        if (friend.LastReportLocal == default)
        {
            ImGui.TextDisabled("No gil report yet.");
        }
        else
        {
            DrawStat("Shown", GilMath.Format(friend.ReportedPocketGil));
            DrawStat("To hand over", GilMath.Format(friend.ReportedOwedGil), true);
            DrawStat("Tax", GilMath.Format(friend.ReportedTaxGil));
            DrawStat("On their character", GilMath.Format(friend.ReportedRealGil));
            DrawStat("Updated", friend.LastReportLocal.ToString("t"));
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputInt("##drain-amount", ref collectAmount);
        if (collectAmount < 0)
            collectAmount = 0;
        ImGui.SameLine();
        var canDrain = friend.IsActive && collectAmount > 0;
        if (!canDrain)
            ImGui.BeginDisabled();
        if (ImGui.Button("Drain"))
            Plugin.Session.RequestTribute(friend, collectAmount);
        if (!canDrain)
            ImGui.EndDisabled();
        if (friend.TributeRequested)
        {
            ImGui.SameLine();
            if (ImGui.Button("Received"))
                Plugin.Session.ApproveHandover(friend);
        }

        ImGui.TextWrapped("When pressing drain, the sub will be forced to start a trade with your current character if you are in their proximity.");
    }

    private void DrawFriendCode(string code)
    {
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var iconRoom = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var font = Fits(codeFontLarge, code, avail - iconRoom * 2f) ? codeFontLarge : codeFontMedium;
        Vector2 textMin;
        Vector2 textMax;
        using (font.Push())
        {
            var size = ImGui.CalcTextSize(code);
            var x = origin.X + Math.Max(0f, (avail - size.X) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(MathF.Round(x), MathF.Round(origin.Y)));
            ImGui.InvisibleButton("##friend-code", size);
            textMin = ImGui.GetItemRectMin();
            textMax = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), textMin, ImGui.GetColorU32(Gold), code);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked())
            Copy("code", code);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Copied("code") ? "Copied" : "Copy");

        var icon = ImGui.GetFrameHeight();
        var iconY = textMin.Y + (textMax.Y - textMin.Y - icon) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(MathF.Round(textMax.X + ImGui.GetStyle().ItemSpacing.X), MathF.Round(iconY)));
        if (ImGuiComponents.IconButton("##copy-code", FontAwesomeIcon.Copy))
            Copy("code", code);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Copied("code") ? "Copied" : "Copy");

        ImGui.SetCursorScreenPos(new Vector2(origin.X, textMax.Y + ImGui.GetStyle().ItemSpacing.Y));
    }

    private static bool Fits(IFontHandle font, string text, float width)
    {
        using (font.Push())
            return ImGui.CalcTextSize(text).X <= width - 8f;
    }

    private static void CenterText(string text)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail - width) * 0.5f));
        ImGui.TextUnformatted(text);
    }

    private static void DrawStat(string label, string value, bool emphasis = false)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(150f);
        if (emphasis)
            ImGui.TextColored(new Vector4(0.95f, 0.55f, 0.72f, 1f), value);
        else
            ImGui.TextUnformatted(value);
    }

    private void LoadEditors(FriendLink friend)
    {
        if (loadedCode == friend.Code)
            return;

        loadedCode = friend.Code;
        nickname = friend.Nickname;
        template = friend.MoodleTemplate;
        description = friend.MoodleDescription;
        tax = friend.TaxPercent;
        cap = (int)Math.Clamp(friend.GilCap, 0, int.MaxValue);
        iconId = friend.MoodleIconId;
        statusType = Math.Clamp(friend.MoodleStatusType, 0, 2);
        moodlePasteNote = "";
        moodleEnabled = friend.MoodleEnabled;
        collectAmount = (int)Math.Clamp(friend.ReportedOwedGil, 0, int.MaxValue);
        extraDrafts.Clear();
        pendingMoodles.Clear();
        foreach (var moodle in friend.ExtraMoodles)
            extraDrafts[moodle.Id] = DraftFrom(moodle);
    }

    private ExtraDraft DraftFor(CustomMoodle moodle)
    {
        if (!extraDrafts.TryGetValue(moodle.Id, out var draft))
        {
            draft = DraftFrom(moodle);
            extraDrafts[moodle.Id] = draft;
        }

        return draft;
    }

    private static ExtraDraft DraftFrom(CustomMoodle moodle) => new()
    {
        Title = moodle.Title,
        Description = moodle.Description,
        IconId = moodle.IconId,
        StatusType = Math.Clamp(moodle.StatusType, 0, 2),
        Enabled = moodle.Enabled,
    };

    private static string ListDetail(FriendLink friend) => friend.State switch
    {
        LinkState.PendingIn => friend.IncomingOffer == PartnerRole.Unset
            ? "wants to link"
            : $"wants to be {RoleName(friend.IncomingOffer).ToLowerInvariant()}",
        LinkState.PendingOut => friend.MyRole == PartnerRole.Unset
            ? "request sent"
            : $"asked to be {RoleName(friend.MyRole).ToLowerInvariant()}",
        _ when friend.IsActive => (friend.MyRole == PartnerRole.Sub ? "sub" : "dom")
            + (friend.Online ? " · online" : " · offline"),
        _ => friend.Online ? "linked · online" : "linked · offline",
    };

    private static int Rank(FriendLink friend) => friend.State switch
    {
        LinkState.PendingIn => 0,
        LinkState.Linked => 1,
        _ => 2,
    };

    private void Copy(string label, string text)
    {
        if (text.Length == 0)
            return;

        ImGui.SetClipboardText(text);
        copiedLabel = label;
        copiedUntil = DateTime.UtcNow.AddSeconds(2);
    }

    private bool Copied(string label) => copiedLabel == label && DateTime.UtcNow < copiedUntil;

    private void MarkSaved(string note)
    {
        savedNote = note;
        savedUntil = DateTime.UtcNow.AddSeconds(2);
    }

    private void DrawSaved()
    {
        if (savedNote.Length > 0 && DateTime.UtcNow < savedUntil)
            ImGui.TextColored(new Vector4(0.96f, 0.62f, 0.76f, 1f), savedNote);
    }

    private static string RoleName(PartnerRole role) => role switch
    {
        PartnerRole.Sub => "Sub",
        PartnerRole.Dom => "Dom",
        _ => "not chosen",
    };

    private static PartnerRole Opposite(PartnerRole role) => role switch
    {
        PartnerRole.Dom => PartnerRole.Sub,
        PartnerRole.Sub => PartnerRole.Dom,
        _ => PartnerRole.Unset,
    };
}
