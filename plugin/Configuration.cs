using Dalamud.Configuration;
using Newtonsoft.Json;

namespace GilDom;

public sealed class CharacterProfile
{
    public string ContentId { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string LinkSecret { get; set; } = "";
    public bool RewriteGilDisplay { get; set; } = true;
    public List<FriendLink> Friends { get; set; } = [];
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private static readonly Mutex FileLock = new(false, @"Local\GilDom.PluginConfig");

    private CharacterProfile? active;

    public int Version { get; set; } = 2;
    public List<CharacterProfile> Characters { get; set; } = [];

    // Older builds stored one identity for the whole launcher. It is claimed by the first character that logs in.
    [JsonProperty("FriendCode")]
    public string StoredFriendCode { get; set; } = "";

    [JsonProperty("LinkSecret")]
    public string StoredLinkSecret { get; set; } = "";

    [JsonProperty("RewriteGilDisplay")]
    public bool StoredRewriteGilDisplay { get; set; } = true;

    [JsonProperty("Friends")]
    public List<FriendLink> StoredFriends { get; set; } = [];

    [JsonIgnore]
    public bool IsBound => active != null;

    [JsonIgnore]
    public string ActiveContentId => active?.ContentId ?? "";

    [JsonIgnore]
    public string FriendCode
    {
        get => active?.FriendCode ?? "";
        set
        {
            if (active != null)
                active.FriendCode = value;
        }
    }

    [JsonIgnore]
    public string LinkSecret => active?.LinkSecret ?? "";

    public bool AcceptIssuedSecret(string secret)
    {
        if (active == null || active.LinkSecret.Length >= 16 || secret.Length != 64 || !secret.All(IsCodeChar))
            return false;

        active.LinkSecret = secret;
        return true;
    }

    public bool ReplaceSecret(string secret)
    {
        if (active == null || secret.Length is < 16 or > 64 || !secret.All(IsCodeChar) || secret == active.LinkSecret)
            return false;

        active.LinkSecret = secret;
        return true;
    }

    [JsonIgnore]
    public bool RewriteGilDisplay
    {
        get => active?.RewriteGilDisplay ?? false;
        set
        {
            if (active != null)
                active.RewriteGilDisplay = value;
        }
    }

    [JsonIgnore]
    public List<FriendLink> Friends => active?.Friends ?? [];

    public void Save()
    {
        if (active == null)
            return;

        var owned = false;
        try
        {
            try
            {
                FileLock.WaitOne();
                owned = true;
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (Plugin.PluginInterface.GetPluginConfig() is Configuration disk)
            {
                disk.Characters ??= [];
                var taken = disk.Characters.Any(profile =>
                    profile.ContentId != active.ContentId
                    && profile.LinkSecret.Length > 0
                    && string.Equals(profile.LinkSecret, active.LinkSecret, StringComparison.Ordinal));
                if (taken)
                {
                    active.LinkSecret = "";
                    active.FriendCode = "";
                    active.Friends = [];
                }

                Characters = disk.Characters.Where(profile => profile.ContentId != active.ContentId).ToList();
                Characters.Add(active);
            }

            StoredFriendCode = "";
            StoredLinkSecret = "";
            StoredFriends = [];
            Plugin.PluginInterface.SavePluginConfig(this);
        }
        finally
        {
            if (owned)
                FileLock.ReleaseMutex();
        }
    }

    public static bool IsFriendCode(string? code) =>
        code != null && code.Length == 8 && code.All(IsCodeChar);

    public void Prepare()
    {
        Characters ??= [];
        StoredFriends ??= [];
        foreach (var profile in Characters)
            Normalize(profile);
        foreach (var friend in StoredFriends)
            NormalizeFriend(friend);
    }

    public void Bind(ulong contentId)
    {
        if (contentId == 0)
        {
            active = null;
            return;
        }

        var id = contentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (active?.ContentId == id)
            return;

        Characters ??= [];
        var profile = Characters.FirstOrDefault(item => item.ContentId == id);
        if (profile == null)
        {
            profile = new CharacterProfile { ContentId = id };
            if (CanClaimStoredIdentity(id))
            {
                profile.FriendCode = StoredFriendCode;
                profile.LinkSecret = StoredLinkSecret;
                profile.RewriteGilDisplay = StoredRewriteGilDisplay;
                profile.Friends = StoredFriends ?? [];
                StoredFriendCode = "";
                StoredLinkSecret = "";
                StoredFriends = [];
            }
            else
            {
                profile.Friends = [];
            }

            Characters.Add(profile);
        }

        Normalize(profile);
        active = profile;
        Save();
    }

    public void Unbind() => active = null;

    public FriendLink? Find(string code) =>
        Friends.FirstOrDefault(friend => string.Equals(friend.Code, code, StringComparison.OrdinalIgnoreCase));

    private bool CanClaimStoredIdentity(string contentId) =>
        StoredLinkSecret.Length >= 16
        && !Characters.Any(profile =>
            profile.ContentId != contentId
            && string.Equals(profile.LinkSecret, StoredLinkSecret, StringComparison.Ordinal));

    private static void Normalize(CharacterProfile profile)
    {
        profile.Friends ??= [];
        if (!IsFriendCode(profile.FriendCode))
            profile.FriendCode = "";
        foreach (var friend in profile.Friends)
            NormalizeFriend(friend);
    }

    private static void NormalizeFriend(FriendLink friend)
    {
        friend.ExtraMoodles ??= [];
        friend.Ledger ??= [];
        if (friend.TaxMoodleId == Guid.Empty)
            friend.TaxMoodleId = Guid.NewGuid();
        friend.MoodleTemplate = RenameGilTag(friend.MoodleTemplate);
        friend.MoodleDescription = RenameGilTag(friend.MoodleDescription);
        foreach (var moodle in friend.ExtraMoodles)
        {
            moodle.Title = RenameGilTag(moodle.Title);
            moodle.Description = RenameGilTag(moodle.Description);
        }

        if (string.IsNullOrWhiteSpace(friend.MoodleTemplate))
            friend.MoodleTemplate = "Tax to deliver: <gilowed> gil";
    }

    private static string RenameGilTag(string? text) =>
        string.IsNullOrEmpty(text)
            ? text ?? ""
            : text.Replace("<gildom>", "<gilowed>", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodeChar(char c) =>
        c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
