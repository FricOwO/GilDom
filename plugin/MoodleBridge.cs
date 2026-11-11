using System.Text;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;

namespace GilDom;

public sealed class MoodleBridge
{
    private readonly ICallGateSubscriber<int>? version;
    private readonly ICallGateSubscriber<string, object>? apply;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object>? remove;
    private static readonly JsonSerializerOptions MoodleJson = new() { IncludeFields = true };
    private DateTime nextAttemptUtc = DateTime.MinValue;
    private DateTime permissionCheckedUtc = DateTime.MinValue;
    private string lastTaxText = "";
    private bool permissionAllows = true;
    private bool warned;
    private PendingApply? taxMoodle;
    private readonly Queue<PendingApply> pending = new();

    public string Status { get; private set; } = "Moodles has not been checked yet";

    public MoodleBridge()
    {
        try
        {
            version = Plugin.PluginInterface.GetIpcSubscriber<int>("Moodles.Version");
            apply = Plugin.PluginInterface.GetIpcSubscriber<string, object>("Moodles.ApplyMoodlesByString");
            remove = Plugin.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Plugin.Log.Warning(ex, "Moodles IPC is unavailable");
        }
    }

    public void PushTax(FriendLink friend, bool force)
    {
        if (!friend.MoodleEnabled || !friend.IsActive || friend.MyRole != PartnerRole.Sub)
        {
            taxMoodle = null;
            if (force)
                Remove(friend.TaxMoodleId);
            return;
        }

        var title = GilMath.ApplyTemplate(friend.MoodleTemplate, friend, reported: false);
        var description = GilMath.ApplyTemplate(friend.MoodleDescription, friend, reported: false);
        var rendered = title + "\n" + description;
        if (force)
        {
            if (MoodlesAllowsRemote())
                Remove(friend.TaxMoodleId);
            else
                WarnBlocked();
            taxMoodle = new PendingApply(friend.TaxMoodleId, title, description, friend.MoodleIconId, friend.MoodleStatusType, friend.PartnerTag);
            pending.Enqueue(taxMoodle.Value);
            lastTaxText = rendered;
            nextAttemptUtc = DateTime.UtcNow.Add(MoodlesAllowsRemote() ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(3));
            return;
        }

        if (rendered == lastTaxText && DateTime.UtcNow < nextAttemptUtc)
            return;

        taxMoodle = new PendingApply(friend.TaxMoodleId, title, description, friend.MoodleIconId, friend.MoodleStatusType, friend.PartnerTag);
        if (Apply(friend.TaxMoodleId, title, description, friend.MoodleIconId, friend.MoodleStatusType, friend.PartnerTag))
        {
            lastTaxText = rendered;
            nextAttemptUtc = DateTime.UtcNow.Add(MoodlesAllowsRemote() ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(3));
        }
    }

    public void PushExtras(FriendLink friend, bool replace)
    {
        if (friend.MyRole != PartnerRole.Sub || !friend.IsActive)
            return;

        foreach (var moodle in friend.ExtraMoodles)
        {
            if (!moodle.Enabled || string.IsNullOrWhiteSpace(moodle.Title))
            {
                Remove(moodle.Id);
                continue;
            }

            var title = GilMath.ApplyTemplate(moodle.Title, friend, reported: false);
            var description = GilMath.ApplyTemplate(moodle.Description, friend, reported: false);
            if (replace)
            {
                Remove(moodle.Id);
                pending.Enqueue(new PendingApply(moodle.Id, title, description, moodle.IconId, moodle.StatusType, friend.PartnerTag));
            }
            else
            {
                Apply(moodle.Id, title, description, moodle.IconId, moodle.StatusType, friend.PartnerTag);
            }
        }
    }

    public void Tick()
    {
        while (pending.Count > 0)
        {
            var item = pending.Dequeue();
            Apply(item.Id, item.Title, item.Description, item.IconId, item.StatusType, item.Applier);
        }

        if (taxMoodle is not { } moodle || DateTime.UtcNow < nextAttemptUtc || MoodlesAllowsRemote())
            return;

        WarnBlocked();
        Apply(moodle.Id, moodle.Title, moodle.Description, moodle.IconId, moodle.StatusType, moodle.Applier);
        nextAttemptUtc = DateTime.UtcNow.AddSeconds(3);
    }

    private bool MoodlesAllowsRemote()
    {
        if (DateTime.UtcNow - permissionCheckedUtc < TimeSpan.FromSeconds(2))
            return permissionAllows;

        permissionCheckedUtc = DateTime.UtcNow;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher",
                "pluginConfigs",
                "Moodles",
                "DefaultConfig.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            permissionAllows = Flag(root, "AllowRemoteApply")
                && (Flag(root, "BroadcastAllowAll") || Flag(root, "BroadcastAllowFriends") || Flag(root, "BroadcastAllowParty"));
        }
        catch (Exception ex)
        {
            permissionAllows = true;
            Plugin.Log.Debug(ex, "Could not read the Moodles permission settings");
        }

        return permissionAllows;
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private void WarnBlocked()
    {
        Status = "Moodles is refusing moodles from other plugins.";
        if (warned)
            return;

        warned = true;
        Plugin.Chat.Print("GilDom: Moodles is refusing this moodle. On the sub, open Moodles, then Settings, and enable \"Allow other plugins apply Moodles\" and \"Allow applying moodles from everyone\". Friends works too, if your partner is on your friend list.");
    }

    public void Forget()
    {
        lastTaxText = "";
        taxMoodle = null;
    }

    public void Dismiss(Guid id) => Remove(id);

    public void Clear(FriendLink friend)
    {
        Remove(friend.TaxMoodleId);
        foreach (var moodle in friend.ExtraMoodles)
            Remove(moodle.Id);
        lastTaxText = "";
        taxMoodle = null;
    }

    private bool Apply(Guid id, string title, string description, int iconId, int statusType, string applier)
    {
        if (apply == null || version == null)
        {
            Status = "Moodles IPC is missing";
            return false;
        }

        if (iconId < 200000)
            iconId = 215125;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        try
        {
            _ = version.InvokeFunc();
            var payload = new MoodlePayload
            {
                GUID = id,
                IconID = iconId,
                Title = title,
                Description = description ?? "",
                Type = statusType,
                Modifiers = 0,
                Stacks = 1,
                StackSteps = 0,
                ChainedStatus = Guid.Empty,
                ChainTrigger = 0,
                Applier = applier ?? "",
                Dispeller = "",
                NoExpire = true,
                AsPermanent = true,
                Persistent = true,
                ExpiresAt = long.MaxValue,
            };
            var json = JsonSerializer.Serialize(payload, MoodleJson);
            apply.InvokeAction(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            Status = MoodlesAllowsRemote()
                ? "Moodles accepted the last update"
                : "Moodles is refusing moodles from other plugins.";
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Plugin.Log.Warning(ex, "Could not apply a GilDom moodle");
            nextAttemptUtc = DateTime.UtcNow.AddSeconds(15);
            return false;
        }
    }

    private void Remove(Guid id)
    {
        if (remove == null || id == Guid.Empty)
            return;
        var player = Plugin.Objects.LocalPlayer;
        if (player == null)
            return;

        try
        {
            remove.InvokeAction(id, player);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Plugin.Log.Debug(ex, "Could not remove moodle {Id}", id);
        }
    }

    private sealed class MoodlePayload
    {
        public Guid GUID;
        public int IconID;
        public string Title = "";
        public string Description = "";
        public string CustomFXPath = "";
        public long ExpiresAt = long.MaxValue;
        public int Type = 1;
        public int Modifiers = 1;
        public int Stacks = 1;
        public int StackSteps;
        public Guid ChainedStatus;
        public int ChainTrigger;
        public string Applier = "";
        public string Dispeller = "";
        public bool NoExpire = true;
        public bool AsPermanent = true;
        public bool Persistent = true;
    }

    private readonly record struct PendingApply(Guid Id, string Title, string Description, int IconId, int StatusType, string Applier);
}
