namespace GilDom;

public enum PartnerRole
{
    Unset,
    Sub,
    Dom,
}

public enum LinkState
{
    PendingOut,
    PendingIn,
    Linked,
}

public sealed class CustomMoodle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public int IconId { get; set; } = 215125;
    public int StatusType { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

public sealed class LedgerLine
{
    public DateTime When { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
}

public sealed class FriendLink
{
    public string Code { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public string World { get; set; } = "";
    public bool Online { get; set; }
    public LinkState State { get; set; }
    public PartnerRole MyRole { get; set; } = PartnerRole.Unset;
    public PartnerRole TheirRole { get; set; } = PartnerRole.Unset;
    public PartnerRole IncomingOffer { get; set; } = PartnerRole.Unset;
    public PartnerRole MyProposal { get; set; } = PartnerRole.Unset;
    public PartnerRole TheirProposal { get; set; } = PartnerRole.Unset;
    public bool IConfirmed { get; set; }
    public bool TheyConfirmed { get; set; }

    public int TaxPercent { get; set; }
    public long GilCap { get; set; }
    public string MoodleTemplate { get; set; } = "Tax to deliver: <gilowed> gil";
    public string MoodleDescription { get; set; } = "Gil to hand to your partner.";
    public int MoodleIconId { get; set; } = 215125;
    public int MoodleStatusType { get; set; } = 1;
    public bool MoodleEnabled { get; set; } = true;
    public Guid TaxMoodleId { get; set; } = Guid.NewGuid();
    public List<CustomMoodle> ExtraMoodles { get; set; } = [];

    public bool LedgerSeeded { get; set; }
    public long LastRealGil { get; set; }
    public long PocketGil { get; set; }
    public long OwedGil { get; set; }
    public long TaxGil { get; set; }
    public long ReportedRealGil { get; set; }
    public long ReportedPocketGil { get; set; }
    public long ReportedOwedGil { get; set; }
    public long ReportedTaxGil { get; set; }
    public DateTime LastReportLocal { get; set; }
    public bool TributeRequested { get; set; }
    public long CollectRequest { get; set; }
    public long HandoverPaid { get; set; }
    public int SeenTributeSeq { get; set; }
    public int SeenPaidSeq { get; set; }
    public List<LedgerLine> Ledger { get; set; } = [];

    public bool IsActive =>
        State == LinkState.Linked
        && MyRole != PartnerRole.Unset
        && TheirRole != PartnerRole.Unset
        && MyRole != TheirRole;

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? Code : Nickname;

    public string PartnerTag =>
        string.IsNullOrWhiteSpace(CharacterName)
            ? ""
            : string.IsNullOrWhiteSpace(World) ? CharacterName : $"{CharacterName}@{World}";
}
