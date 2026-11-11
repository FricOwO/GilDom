using System.Globalization;
using System.Text.Json;

namespace GilDom;

public sealed class Session : IDisposable
{
    private readonly RelayClient relay = new();
    private readonly MoodleBridge moodles = new();
    private readonly GilDisplay display;
    private readonly TradeHandover handover;
    private DateTime nextMoodleForceUtc = DateTime.MinValue;
    private static readonly JsonSerializerOptions BodyJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public RelayClient Relay => relay;
    public MoodleBridge Moodles => moodles;
    public string? LastError { get; private set; }

    public Session(GilDisplay display)
    {
        this.display = display;
        handover = new TradeHandover(FinishHandover);
        relay.MessageReceived += OnSync;
        foreach (var friend in Plugin.Configuration.Friends.Where(friend => friend.State == LinkState.Linked))
            SendSnapshot(friend);
        relay.Start();
        Plugin.Framework.Update += OnFramework;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFramework;
        handover.Dispose();
        relay.MessageReceived -= OnSync;
        relay.Dispose();
    }

    public void ReleaseCharacter()
    {
        handover.Stop();
        var sub = ActiveSubLink();
        if (sub != null)
            moodles.Clear(sub);
        else
            moodles.Forget();
        display.SetMask(0, 0, false);
    }

    public void AdoptCharacter()
    {
        nextMoodleForceUtc = DateTime.UtcNow;
        relay.ResetIdentity();
        if (!Plugin.Configuration.IsBound)
            return;

        foreach (var friend in Plugin.Configuration.Friends.Where(friend => friend.State == LinkState.Linked))
            SendSnapshot(friend);
        relay.SyncNow();
    }

    public static bool IsFriendCode(string code) => Configuration.IsFriendCode(code);

    public void SendFriendRequest(string code, PartnerRole role)
    {
        code = code.Trim().ToUpperInvariant();
        if (!Plugin.Configuration.IsBound)
        {
            LastError = "Log in with the character you want to use.";
            return;
        }

        if (!IsFriendCode(code))
        {
            LastError = "A friend code is 8 letters or digits.";
            return;
        }

        if (role is not (PartnerRole.Dom or PartnerRole.Sub))
        {
            LastError = "Choose Dom or Sub before you send the request.";
            return;
        }

        if (string.Equals(code, Plugin.Configuration.FriendCode, StringComparison.OrdinalIgnoreCase))
        {
            LastError = "That is your own friend code.";
            return;
        }

        if (role == PartnerRole.Sub && HasOtherDom(code))
        {
            LastError = "You already have an active dom.";
            return;
        }

        var existing = Plugin.Configuration.Find(code);
        if (existing == null)
        {
            existing = new FriendLink { Code = code, State = LinkState.PendingOut };
            Plugin.Configuration.Friends.Add(existing);
        }
        else if (existing.State == LinkState.Linked)
        {
            LastError = "You are already linked. Ask them to change the dynamic instead.";
            return;
        }
        else
        {
            existing.State = LinkState.PendingOut;
        }

        existing.MyRole = role;
        existing.IConfirmed = false;
        existing.TheyConfirmed = false;
        LastError = null;
        Plugin.Configuration.Save();
        relay.Send(new { t = "friend_request", to = code, role = role.ToString() });
    }

    public void Accept(FriendLink friend)
    {
        var offered = friend.IncomingOffer;
        if (offered is not (PartnerRole.Dom or PartnerRole.Sub))
        {
            LastError = "That request does not say who is the dom. Ask them to send it again.";
            return;
        }

        var mine = Opposite(offered);
        if (mine == PartnerRole.Sub && HasOtherDom(friend.Code))
        {
            LastError = "You already have an active dom.";
            return;
        }

        friend.State = LinkState.Linked;
        friend.TheirRole = offered;
        friend.MyRole = mine;
        friend.IConfirmed = true;
        friend.TheyConfirmed = true;
        friend.IncomingOffer = PartnerRole.Unset;
        friend.MyProposal = PartnerRole.Unset;
        friend.TheirProposal = PartnerRole.Unset;
        LastError = null;
        Plugin.Configuration.Save();
        relay.Send(new { t = "friend_accept", to = friend.Code });
        SendSnapshot(friend);
        if (friend.MyRole == PartnerRole.Sub)
            EnsureSeeded(friend);
    }

    public void Decline(FriendLink friend)
    {
        relay.Send(new { t = "friend_decline", to = friend.Code });
        Plugin.Configuration.Friends.Remove(friend);
        Plugin.Configuration.Save();
    }

    public void Unlink(FriendLink friend)
    {
        if (friend.MyRole == PartnerRole.Sub)
            moodles.Clear(friend);
        friend.State = LinkState.PendingOut;
        friend.IConfirmed = false;
        friend.TheyConfirmed = false;
        friend.Online = false;
        Plugin.Configuration.Friends.Remove(friend);
        Plugin.Configuration.Save();
        relay.Send(new { t = "unlink", to = friend.Code });
    }

    public void SetNickname(FriendLink friend, string nickname)
    {
        friend.Nickname = nickname.Trim();
        Plugin.Configuration.Save();
    }

    public void ProposeRole(FriendLink friend, PartnerRole role)
    {
        if (friend.State != LinkState.Linked || role is not (PartnerRole.Dom or PartnerRole.Sub))
            return;
        if (role == friend.MyRole && friend.MyProposal == PartnerRole.Unset)
            return;
        if (role == PartnerRole.Sub && HasOtherDom(friend.Code))
        {
            LastError = "You already have an active dom.";
            return;
        }

        friend.MyProposal = role;
        LastError = null;
        Plugin.Configuration.Save();
        relay.Send(new { t = "role_proposal", to = friend.Code, role = role.ToString() });
    }

    public void CancelProposal(FriendLink friend)
    {
        friend.MyProposal = PartnerRole.Unset;
        Plugin.Configuration.Save();
        relay.Send(new { t = "role_cancel", to = friend.Code });
    }

    public void AcceptProposal(FriendLink friend)
    {
        var offered = friend.TheirProposal;
        if (offered is not (PartnerRole.Dom or PartnerRole.Sub))
            return;

        var mine = Opposite(offered);
        if (mine == PartnerRole.Sub && HasOtherDom(friend.Code))
        {
            LastError = "You already have an active dom.";
            return;
        }

        friend.TheirRole = offered;
        friend.MyRole = mine;
        friend.IConfirmed = true;
        friend.TheyConfirmed = true;
        friend.MyProposal = PartnerRole.Unset;
        friend.TheirProposal = PartnerRole.Unset;
        LastError = null;
        Plugin.Configuration.Save();
        relay.Send(new { t = "role_accept", to = friend.Code });
        if (friend.IsActive && friend.MyRole == PartnerRole.Sub)
            EnsureSeeded(friend);
        if (friend.IsActive && friend.MyRole == PartnerRole.Dom)
            PushDynamics(friend);
    }

    public void DeclineProposal(FriendLink friend)
    {
        friend.TheirProposal = PartnerRole.Unset;
        Plugin.Configuration.Save();
        relay.Send(new { t = "role_decline", to = friend.Code });
    }

    public void RequestTribute(FriendLink friend, long amount)
    {
        if (!friend.IsActive || friend.MyRole != PartnerRole.Dom || amount <= 0)
            return;

        friend.CollectRequest = amount;
        friend.TributeRequested = true;
        var name = "";
        var world = "";
        var self = Plugin.Objects.LocalPlayer;
        if (self != null)
        {
            name = self.Name.ToString();
            world = self.HomeWorld.ValueNullable?.Name.ToString() ?? "";
        }

        relay.Send(new
        {
            t = "state",
            to = friend.Code,
            kind = "tribute",
            body = new { amount, name, world },
        });
        Note(friend, $"Asked them to hand over {GilMath.Format(amount)} gil.");
        Plugin.Configuration.Save();
        Plugin.Chat.Print($"GilDom: asked {friend.DisplayName} to hand over {GilMath.Format(amount)} gil.");
    }

    public void PushDynamics(FriendLink friend)
    {
        friend.TaxPercent = Math.Clamp(friend.TaxPercent, 0, 100);
        if (friend.GilCap < 0)
            friend.GilCap = 0;
        if (friend.MoodleIconId < 200000)
            friend.MoodleIconId = 215125;

        Plugin.Configuration.Save();
        if (friend.State != LinkState.Linked || friend.MyRole != PartnerRole.Dom)
            return;

        relay.Send(new
        {
            t = "state",
            to = friend.Code,
            kind = "dynamics",
            body = new
            {
                friend.TaxPercent,
                friend.GilCap,
                friend.MoodleTemplate,
                friend.MoodleDescription,
                friend.MoodleIconId,
                friend.MoodleStatusType,
                friend.MoodleEnabled,
                friend.TaxMoodleId,
                friend.ExtraMoodles,
            },
        });
    }

    public void ApproveHandover(FriendLink friend)
    {
        if (!friend.IsActive || friend.MyRole != PartnerRole.Dom || !friend.TributeRequested)
            return;

        var amount = friend.CollectRequest > 0 ? friend.CollectRequest : friend.ReportedOwedGil;
        if (amount <= 0)
            return;

        friend.TributeRequested = false;
        friend.CollectRequest = 0;
        friend.ReportedOwedGil = Math.Max(0, friend.ReportedOwedGil - amount);
        friend.ReportedTaxGil = Math.Max(0, friend.ReportedTaxGil - amount);
        Note(friend, $"Marked {GilMath.Format(amount)} gil as received.");
        Plugin.Configuration.Save();
        relay.Send(new
        {
            t = "state",
            to = friend.Code,
            kind = "tribute_paid",
            body = new { amount },
        });
        Plugin.Chat.Print($"GilDom: marked {GilMath.Format(amount)} gil from {friend.DisplayName} as received.");
    }

    public void ChangeSecret(string secret)
    {
        secret = secret.Trim();
        if (!Plugin.Configuration.IsBound)
        {
            LastError = "Log in with the character you want to use.";
            return;
        }

        if (Plugin.Configuration.LinkSecret.Length < 16)
        {
            LastError = "This character does not have a secret yet.";
            return;
        }

        if (secret.Length is < 16 or > 64 || !secret.All(char.IsLetterOrDigit))
        {
            LastError = "A secret is 16 to 64 letters or digits.";
            return;
        }

        if (secret == Plugin.Configuration.LinkSecret)
        {
            LastError = null;
            return;
        }

        LastError = null;
        relay.QueueSecretChange(secret, false);
    }

    public void RequestNewSecret()
    {
        if (!Plugin.Configuration.IsBound || Plugin.Configuration.LinkSecret.Length < 16)
        {
            LastError = "This character does not have a secret yet.";
            return;
        }

        LastError = null;
        relay.QueueSecretChange(null, true);
    }

    public void ResetLedger(FriendLink friend)
    {
        if (friend.MyRole != PartnerRole.Sub)
            return;
        GilMath.Reset(friend, display.ServerGil);
        Plugin.Configuration.Save();
        SendGil(friend);
        moodles.PushTax(friend, force: true);
    }

    private void OnFramework(object framework)
    {
        moodles.Tick();
        Plugin.EnsureCharacter();
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        var real = loggedIn ? GilDisplay.ReadServerGil() : 0u;
        var sub = ActiveSubLink();

        if (loggedIn && sub is { IsActive: true })
        {
            if (!sub.LedgerSeeded)
                EnsureSeeded(sub);

            var capped = false;
            if (sub.GilCap > 0 && sub.PocketGil > sub.GilCap)
            {
                GilMath.ApplyCap(sub);
                capped = true;
            }

            var changed = GilMath.Observe(sub, real, handover.CountsAsPayment);
            if (changed || capped)
            {
                Plugin.Configuration.Save();
                SendGil(sub);
                if (changed)
                {
                    moodles.PushTax(sub, force: false);
                    moodles.PushExtras(sub, replace: false);
                }
            }

            if (DateTime.UtcNow >= nextMoodleForceUtc)
            {
                moodles.PushTax(sub, force: true);
                moodles.PushExtras(sub, replace: true);
                nextMoodleForceUtc = DateTime.UtcNow.AddMinutes(30);
            }

            var shown = sub.GilCap > 0 ? Math.Min(sub.PocketGil, sub.GilCap) : sub.PocketGil;
            display.SetMask(real, shown, Plugin.Configuration.RewriteGilDisplay || sub.GilCap > 0);
        }
        else
        {
            display.SetMask(real, real, false);
        }

        handover.Tick();
    }

    private void ApplyApprovedPayment(FriendLink friend, long paid)
    {
        var mark = Math.Max(0, paid - friend.HandoverPaid);
        friend.HandoverPaid = 0;
        if (mark > 0)
            GilMath.Deliver(friend, mark);
        else
        {
            friend.TributeRequested = false;
            friend.CollectRequest = 0;
            Note(friend, $"Marked {GilMath.Format(paid)} gil as handed over.");
        }

        Plugin.Configuration.Save();
        SendGil(friend);
        moodles.PushTax(friend, force: true);
    }

    private void FinishHandover(FriendLink friend, long paid)
    {
        if (paid > 0)
            friend.HandoverPaid += paid;
        Plugin.Configuration.Save();
        SendGil(friend);
        moodles.PushTax(friend, force: true);
    }

    private void EnsureSeeded(FriendLink friend)
    {
        if (friend.LedgerSeeded)
            return;
        GilMath.Seed(friend, GilDisplay.ReadServerGil());
        Plugin.Configuration.Save();
        SendGil(friend);
        nextMoodleForceUtc = DateTime.UtcNow;
    }

    private void OnSync(JsonElement root)
    {
        if (!Plugin.Configuration.IsBound)
            return;

        LastError = null;
        var issued = Text(root, "secret");
        if (issued.Length == 64 && issued.All(char.IsLetterOrDigit))
        {
            var saved = Plugin.Configuration.LinkSecret.Length < 16
                ? Plugin.Configuration.AcceptIssuedSecret(issued)
                : Plugin.Configuration.ReplaceSecret(issued);
            if (saved)
            {
                Plugin.Configuration.Save();
                relay.ClearSecretChange(true);
            }
            else
            {
                relay.ClearSecretChange(false);
            }
        }

        var assigned = Text(root, "code").ToUpperInvariant();
        if (IsFriendCode(assigned) && !string.Equals(Plugin.Configuration.FriendCode, assigned, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Configuration.FriendCode = assigned;
            Plugin.Configuration.Save();
        }

        if (root.TryGetProperty("requests", out var requests) && requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var request in requests.EnumerateArray())
                IncomingRequest(Text(request, "from"), Text(request, "role"));
        }

        if (root.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.Array)
        {
            foreach (var code in closed.EnumerateArray())
            {
                if (code.ValueKind == JsonValueKind.String)
                    IncomingUnlink(code.GetString() ?? "");
            }
        }

        if (root.TryGetProperty("friends", out var friends) && friends.ValueKind == JsonValueKind.Array)
        {
            foreach (var friend in friends.EnumerateArray())
                ApplyFriend(friend);
        }
    }

    private void ApplyFriend(JsonElement row)
    {
        var code = Text(row, "code").ToUpperInvariant();
        if (!IsFriendCode(code))
            return;

        var friend = Plugin.Configuration.Find(code);
        if (friend == null || friend.State != LinkState.Linked)
        {
            IncomingAccept(code);
            friend = Plugin.Configuration.Find(code);
            if (friend == null)
                return;
        }

        var changed = false;
        var online = row.TryGetProperty("online", out var onlineValue) && onlineValue.GetBoolean();
        if (friend.Online != online)
        {
            friend.Online = online;
            changed = true;
        }

        changed |= ApplyRoles(friend, row);
        var hasGil = row.TryGetProperty("gil", out var gil) && gil.ValueKind == JsonValueKind.Object;
        if (row.TryGetProperty("dynamics", out var dynamics) && dynamics.ValueKind == JsonValueKind.Object)
            changed |= ApplyDynamics(friend, dynamics);
        if (hasGil)
            changed |= ApplyGilReport(friend, gil);

        var tributeSeq = Int(row, "tributeSeq");
        if (tributeSeq > friend.SeenTributeSeq)
        {
            friend.SeenTributeSeq = tributeSeq;
            friend.TributeRequested = true;
            var amount = Long(row, "tributeAmount");
            if (amount <= 0)
                amount = friend.OwedGil;
            friend.CollectRequest = amount;
            friend.HandoverPaid = 0;
            Note(friend, $"{friend.DisplayName} asked you to hand over {GilMath.Format(amount)} gil.");
            Plugin.Chat.Print($"GilDom: {friend.DisplayName} asked you to hand over {GilMath.Format(amount)} gil.");
            if (friend.MyRole == PartnerRole.Sub)
                handover.Start(friend, amount, Text(row, "tradeName"), Text(row, "tradeWorld"));
            changed = true;
        }

        var paidSeq = Int(row, "paidSeq");
        if (paidSeq > friend.SeenPaidSeq)
        {
            friend.SeenPaidSeq = paidSeq;
            var paid = Long(row, "paidAmount");
            if (friend.MyRole == PartnerRole.Sub)
            {
                ApplyApprovedPayment(friend, paid);
            }
            else
            {
                if (!hasGil)
                    friend.ReportedOwedGil = Math.Max(0, friend.ReportedOwedGil - paid);
                Note(friend, $"{friend.DisplayName} marked {GilMath.Format(paid)} gil as handed over.");
            }

            changed = true;
        }

        if (changed)
            Plugin.Configuration.Save();
    }

    private void IncomingRequest(string code, string role)
    {
        if (!IsFriendCode(code))
            return;

        var offer = ParseRole(role);
        var friend = Plugin.Configuration.Find(code) ?? new FriendLink { Code = code };
        if (friend.State == LinkState.Linked)
            return;
        if (friend.State == LinkState.PendingIn && friend.IncomingOffer == offer)
            return;

        friend.State = LinkState.PendingIn;
        friend.IncomingOffer = offer;
        if (!Plugin.Configuration.Friends.Contains(friend))
            Plugin.Configuration.Friends.Add(friend);
        Plugin.Configuration.Save();
    }

    private void IncomingAccept(string code)
    {
        var friend = Plugin.Configuration.Find(code);
        if (friend == null)
        {
            friend = new FriendLink { Code = code };
            Plugin.Configuration.Friends.Add(friend);
        }

        friend.State = LinkState.Linked;
        friend.Online = true;
        Plugin.Configuration.Save();
    }

    private void IncomingUnlink(string code)
    {
        var friend = Plugin.Configuration.Find(code);
        if (friend == null)
            return;
        if (friend.MyRole == PartnerRole.Sub)
            moodles.Clear(friend);
        Plugin.Configuration.Friends.Remove(friend);
        Plugin.Configuration.Save();
    }

    private bool ApplyRoles(FriendLink friend, JsonElement row)
    {
        var changed = false;
        var rolesChanged = false;
        if (row.TryGetProperty("myRole", out _))
        {
            var myRole = ParseRole(Text(row, "myRole"));
            var myConfirmed = Bool(row, "myConfirmed");
            var myProposal = ParseRole(Text(row, "myProposal"));
            if (myRole != PartnerRole.Unset && friend.MyRole != myRole)
            {
                friend.MyRole = myRole;
                changed = true;
                rolesChanged = true;
            }

            if (friend.IConfirmed != myConfirmed)
            {
                friend.IConfirmed = myConfirmed;
                changed = true;
                rolesChanged = true;
            }

            if (friend.MyProposal != myProposal)
            {
                friend.MyProposal = myProposal;
                changed = true;
            }
        }

        var theirRole = ParseRole(Text(row, "role"));
        var theirConfirmed = Bool(row, "confirmed");
        var theirProposal = ParseRole(Text(row, "theirProposal"));
        if (friend.TheirRole != theirRole || friend.TheyConfirmed != theirConfirmed || friend.TheirProposal != theirProposal)
        {
            if (friend.TheirRole != theirRole || friend.TheyConfirmed != theirConfirmed)
                rolesChanged = true;
            friend.TheirRole = theirRole;
            friend.TheyConfirmed = theirConfirmed;
            friend.TheirProposal = theirProposal;
            changed = true;
        }

        if (friend.MyRole != PartnerRole.Unset
            && friend.TheirRole != PartnerRole.Unset
            && friend.MyRole != friend.TheirRole
            && (!friend.IConfirmed || !friend.TheyConfirmed))
        {
            friend.IConfirmed = true;
            friend.TheyConfirmed = true;
            changed = true;
            rolesChanged = true;
            SendRole(friend);
        }

        if (rolesChanged && friend.IsActive && friend.MyRole == PartnerRole.Sub)
            EnsureSeeded(friend);
        if (rolesChanged && friend.IsActive && friend.MyRole == PartnerRole.Dom)
            PushDynamics(friend);
        return changed;
    }

    private bool ApplyDynamics(FriendLink friend, JsonElement body)
    {
        if (friend.TheirRole == PartnerRole.Sub)
            return false;

        var tax = Math.Clamp(Int(body, "taxPercent"), 0, 100);
        var cap = Math.Max(0, Long(body, "gilCap"));
        var template = Text(body, "moodleTemplate");
        var description = Text(body, "moodleDescription");
        var icon = Math.Max(200000, Int(body, "moodleIconId"));
        var statusType = Int(body, "moodleStatusType");
        var moodleEnabled = !TryProp(body, "moodleEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.False;
        var taxId = friend.TaxMoodleId;
        if (TryProp(body, "taxMoodleId", out var id) && Guid.TryParse(id.GetString(), out var guid))
            taxId = guid;
        var extras = friend.ExtraMoodles;
        if (TryProp(body, "extraMoodles", out var extrasElement))
            extras = extrasElement.Deserialize<List<CustomMoodle>>(BodyJson) ?? [];

        if (friend.TaxPercent == tax
            && friend.GilCap == cap
            && friend.MoodleTemplate == template
            && friend.MoodleDescription == description
            && friend.MoodleIconId == icon
            && friend.MoodleStatusType == statusType
            && friend.MoodleEnabled == moodleEnabled
            && friend.TaxMoodleId == taxId
            && SameExtras(friend.ExtraMoodles, extras))
            return false;

        var previousTax = friend.TaxMoodleId;
        var previousExtras = friend.ExtraMoodles.Select(moodle => moodle.Id).ToList();
        friend.TaxPercent = tax;
        friend.GilCap = cap;
        friend.MoodleTemplate = template;
        friend.MoodleDescription = description;
        friend.MoodleIconId = icon;
        friend.MoodleStatusType = statusType;
        friend.MoodleEnabled = moodleEnabled;
        friend.TaxMoodleId = taxId;
        friend.ExtraMoodles = extras;

        if (previousTax != friend.TaxMoodleId)
            moodles.Dismiss(previousTax);
        foreach (var extraId in previousExtras)
        {
            if (friend.ExtraMoodles.All(moodle => moodle.Id != extraId))
                moodles.Dismiss(extraId);
        }

        if (friend.IsActive && friend.MyRole == PartnerRole.Sub)
        {
            var pocket = friend.PocketGil;
            var owed = friend.OwedGil;
            GilMath.ApplyCap(friend);
            moodles.PushTax(friend, force: true);
            moodles.PushExtras(friend, replace: true);
            if (pocket != friend.PocketGil || owed != friend.OwedGil)
                SendGil(friend);
        }

        return true;
    }

    private static bool SameExtras(List<CustomMoodle> current, List<CustomMoodle> incoming)
    {
        if (current.Count != incoming.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            var left = current[i];
            var right = incoming[i];
            if (left.Id != right.Id || left.Title != right.Title || left.Description != right.Description
                || left.IconId != right.IconId || left.StatusType != right.StatusType || left.Enabled != right.Enabled)
                return false;
        }

        return true;
    }

    private static bool ApplyGilReport(FriendLink friend, JsonElement body)
    {
        if (friend.MyRole != PartnerRole.Dom)
            return false;

        var real = Long(body, "real");
        var pocket = Long(body, "pocket");
        var owed = Long(body, "owed");
        var taxGil = Long(body, "tax");
        if (friend.ReportedRealGil == real && friend.ReportedPocketGil == pocket && friend.ReportedOwedGil == owed && friend.ReportedTaxGil == taxGil)
            return false;

        friend.ReportedRealGil = real;
        friend.ReportedPocketGil = pocket;
        friend.ReportedOwedGil = owed;
        friend.ReportedTaxGil = taxGil;
        friend.LastReportLocal = DateTime.Now;
        return true;
    }

    private void SendSnapshot(FriendLink friend)
    {
        if (friend.State != LinkState.Linked)
            return;
        SendRole(friend);
        if (friend.MyRole == PartnerRole.Dom)
            PushDynamics(friend);
        if (friend.MyRole == PartnerRole.Sub && friend.LedgerSeeded)
            SendGil(friend);
    }

    private void SendRole(FriendLink friend)
    {
        if (!friend.IConfirmed || friend.MyRole == PartnerRole.Unset)
            return;

        relay.Send(new
        {
            t = "state",
            to = friend.Code,
            kind = "role",
            body = new
            {
                role = friend.MyRole.ToString(),
                confirmed = friend.IConfirmed,
            },
        });
    }

    private void SendGil(FriendLink friend)
    {
        relay.Send(new
        {
            t = "state",
            to = friend.Code,
            kind = "gil",
            body = new
            {
                real = friend.LastRealGil,
                pocket = friend.PocketGil,
                owed = friend.OwedGil,
                tax = friend.TaxGil,
            },
        });
    }

    private FriendLink? ActiveSubLink() =>
        Plugin.Configuration.Friends.FirstOrDefault(friend => friend.IsActive && friend.MyRole == PartnerRole.Sub);

    private static void Note(FriendLink friend, string text)
    {
        friend.Ledger.Add(new LedgerLine { When = DateTime.Now, Text = text });
        if (friend.Ledger.Count > 200)
            friend.Ledger.RemoveRange(0, friend.Ledger.Count - 200);
    }

    private bool HasOtherDom(string code)
    {
        var other = ActiveSubLink();
        return other != null && !string.Equals(other.Code, code, StringComparison.OrdinalIgnoreCase);
    }

    private static PartnerRole Opposite(PartnerRole role) => role switch
    {
        PartnerRole.Dom => PartnerRole.Sub,
        PartnerRole.Sub => PartnerRole.Dom,
        _ => PartnerRole.Unset,
    };

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static PartnerRole ParseRole(string role) => role switch
    {
        "Sub" => PartnerRole.Sub,
        "Dom" => PartnerRole.Dom,
        _ => PartnerRole.Unset,
    };

    private static bool TryProp(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object || name.Length == 0)
            return false;
        if (element.TryGetProperty(name, out value))
            return true;

        var other = char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : char.ToUpperInvariant(name[0]) + name[1..];
        return element.TryGetProperty(other, out value);
    }

    private static string Text(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name) =>
        TryProp(element, name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement element, string name)
    {
        if (!TryProp(element, name, out var value))
            return 0;
        if (value.TryGetInt64(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
