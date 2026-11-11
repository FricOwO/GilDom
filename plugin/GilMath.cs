using System.Globalization;

namespace GilDom;

public static class GilMath
{
    public static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string ApplyTemplate(string? template, FriendLink friend, bool reported)
    {
        var owed = reported ? friend.ReportedOwedGil : friend.OwedGil;
        return ApplyTemplate(template, owed, friend.TaxPercent, friend.GilCap);
    }

    public static string ApplyTemplate(string? template, long owed, int taxPercent, long maxGil)
    {
        var source = string.IsNullOrWhiteSpace(template) ? "Tax to deliver: <gilowed> gil" : template;
        var percent = Math.Clamp(taxPercent, 0, 100);
        source = source.Replace("<gilowed>", Format(owed), StringComparison.OrdinalIgnoreCase);
        source = source.Replace("<gildom>", Format(owed), StringComparison.OrdinalIgnoreCase);
        source = source.Replace("<taxgil>", percent + "%", StringComparison.OrdinalIgnoreCase);
        source = source.Replace("<maxgil>", maxGil > 0 ? Format(maxGil) : "", StringComparison.OrdinalIgnoreCase);
        return source;
    }

    public static void Seed(FriendLink friend, long realGil)
    {
        friend.LastRealGil = realGil;
        friend.PocketGil = realGil;
        friend.OwedGil = 0;
        friend.TaxGil = 0;
        friend.LedgerSeeded = true;
        friend.Ledger.Clear();
        Note(friend, $"Started from {Format(realGil)} gil.");
        ApplyCap(friend);
    }

    public static void ApplyCap(FriendLink friend)
    {
        if (friend.GilCap <= 0 || friend.PocketGil <= friend.GilCap)
            return;

        var overflow = friend.PocketGil - friend.GilCap;
        friend.PocketGil = friend.GilCap;
        friend.OwedGil += overflow;
        Note(friend, $"Allowance of {Format(friend.GilCap)} set aside {Format(overflow)} gil.");
    }

    public static bool Observe(FriendLink friend, long realGil, bool payingDebt)
    {
        if (!friend.LedgerSeeded)
        {
            Seed(friend, realGil);
            return true;
        }

        var delta = realGil - friend.LastRealGil;
        if (delta == 0)
            return false;

        friend.LastRealGil = realGil;
        if (delta > 0)
            AddEarning(friend, delta);
        else if (payingDebt)
            PayDebt(friend, -delta, realGil);
        else
            AddSpend(friend, -delta);

        return true;
    }

    public static long Deliver(FriendLink friend, long amount)
    {
        if (amount <= 0)
            amount = friend.OwedGil;
        amount = Math.Min(amount, friend.OwedGil);
        friend.TributeRequested = false;
        friend.CollectRequest = 0;
        if (amount <= 0)
            return 0;

        friend.OwedGil -= amount;
        friend.TaxGil = Math.Max(0, friend.TaxGil - amount);
        Note(friend, $"Marked {Format(amount)} gil as handed over.");
        return amount;
    }

    public static void Reset(FriendLink friend, long realGil)
    {
        Seed(friend, realGil);
    }

    private static void AddEarning(FriendLink friend, long delta)
    {
        var tax = friend.TaxPercent;
        if (tax < 0) tax = 0;
        if (tax > 100) tax = 100;

        var taxOnly = Math.Min(delta, (delta * tax + 50) / 100);
        var kept = delta - taxOnly;
        var owedAdd = taxOnly;

        if (friend.GilCap > 0 && friend.PocketGil + kept > friend.GilCap)
        {
            var overflow = friend.PocketGil + kept - friend.GilCap;
            kept -= overflow;
            owedAdd += overflow;
        }

        friend.PocketGil += kept;
        friend.TaxGil += taxOnly;
        friend.OwedGil += owedAdd;
        Note(friend, $"Earned {Format(delta)}. Shown {Format(kept)}. Tax {Format(taxOnly)}.");
    }

    private static void PayDebt(FriendLink friend, long spend, long realGil)
    {
        var pay = Math.Min(spend, friend.OwedGil);
        friend.OwedGil -= pay;
        friend.TaxGil = Math.Max(0, friend.TaxGil - pay);
        friend.PocketGil = Math.Max(0, realGil - friend.OwedGil);
        ApplyCap(friend);
        Note(friend, $"Handed over {Format(spend)}. Shown gil is {Format(friend.PocketGil)}.");
    }

    private static void AddSpend(FriendLink friend, long spend)
    {
        var fromPocket = Math.Min(friend.PocketGil, spend);
        friend.PocketGil -= fromPocket;
        var rest = spend - fromPocket;
        if (rest > 0)
        {
            friend.OwedGil = Math.Max(0, friend.OwedGil - rest);
            friend.TaxGil = Math.Max(0, friend.TaxGil - rest);
        }

        Note(friend, $"Spent {Format(spend)}. Shown gil is {Format(friend.PocketGil)}.");
    }

    private static void Note(FriendLink friend, string text)
    {
        friend.Ledger.Add(new LedgerLine { When = DateTime.Now, Text = text });
        if (friend.Ledger.Count > 200)
            friend.Ledger.RemoveRange(0, friend.Ledger.Count - 200);
    }
}
