using System.Globalization;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GilDom;

public sealed unsafe class GilDisplay : IDisposable
{
    private static readonly string[] AddonNames =
    [
        "Currency",
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
        "InventoryRetainer",
        "InventoryRetainerLarge",
        "InventoryBuddy",
        "ArmouryBoard",
        "CabinetWithdraw",
        "Bank",
        "_Money",
        "_BagWidget",
    ];

    private readonly HashSet<string> maskedAddons = new(AddonNames, StringComparer.Ordinal);
    private readonly HashSet<nint> visited = [];
    private readonly Hook<SetNumberDelegate>? setNumberHook;

    private delegate void SetNumberDelegate(AtkCounterNode* counter, int number);

    public long ShownGil { get; private set; }
    public long ServerGil { get; private set; }
    public bool Active { get; private set; }

    public GilDisplay()
    {
        foreach (var name in AddonNames)
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, name, OnPreDraw);

        var address = (nint)AtkCounterNode.MemberFunctionPointers.SetNumber;
        if (address != 0)
        {
            setNumberHook = Plugin.Interop.HookFromAddress<SetNumberDelegate>(address, OnSetNumber);
            setNumberHook.Enable();
        }
        else
        {
            Plugin.Log.Error("GilDom could not find the gil counter function.");
        }
    }

    public void SetMask(long serverGil, long shownGil, bool active)
    {
        ServerGil = serverGil;
        ShownGil = shownGil;
        Active = active;
    }

    public void Dispose()
    {
        setNumberHook?.Dispose();
        foreach (var name in AddonNames)
            Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, name, OnPreDraw);
    }

    public static unsafe uint ReadServerGil()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetGil();
    }

    private void OnSetNumber(AtkCounterNode* counter, int number)
    {
        if (TryShown(number, out var shown) && BelongsToMaskedAddon((AtkResNode*)counter))
            number = shown;

        setNumberHook!.Original(counter, number);
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args) => Rewrite(args);

    private void Rewrite(AddonArgs args)
    {
        if (!Active || args.Addon.IsNull || !args.Addon.IsVisible || ServerGil == ShownGil)
            return;
        if (ShownGil is < 0 or > int.MaxValue)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon->UldManager.NodeList == null)
            return;

        if (args.AddonName == "InventoryExpansion")
        {
            var gil = ((AddonInventoryExpansion*)addon)->GilText;
            if (gil != null && MatchesGil(gil->NodeText.ToString()))
                gil->SetNumber((int)ShownGil, true);
        }

        if (args.AddonName == "_Money")
        {
            var counter = ((AddonMoney*)addon)->CounterNode;
            if (counter != null)
                counter->SetNumber((int)ShownGil);
        }

        visited.Clear();
        var count = addon->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
            Visit(addon->UldManager.NodeList[i]);
    }

    private void Visit(AtkResNode* node)
    {
        if (node == null || !visited.Add((nint)node) || visited.Count > 4000)
            return;

        if (node->Type == NodeType.Text)
            TryText(node->GetAsAtkTextNode());
        else if (node->Type == NodeType.Counter)
            TryCounter(node->GetAsAtkCounterNode());

        if (node->Type == NodeType.Component || (int)node->Type >= 1000)
        {
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
            {
                var list = componentNode->Component->UldManager;
                for (var i = 0; i < list.NodeListCount; i++)
                    Visit(list.NodeList[i]);
            }
        }

        for (var child = node->ChildNode; child != null; child = child->PrevSiblingNode)
            Visit(child);
    }

    private void TryText(AtkTextNode* node)
    {
        if (node == null || !MatchesGil(node->NodeText.ToString()))
            return;

        var useComma = node->NodeText.ToString().IndexOfAny([',', '.', ' ']) >= 0;
        node->SetNumber((int)ShownGil, useComma);
    }

    private void TryCounter(AtkCounterNode* node)
    {
        if (node == null || !MatchesGil(node->NodeText.ToString()))
            return;

        node->SetNumber((int)ShownGil);
    }

    private bool TryShown(int number, out int shown)
    {
        shown = 0;
        if (!Active || ServerGil == ShownGil || ServerGil is < 0 or > int.MaxValue || ShownGil is < 0 or > int.MaxValue)
            return false;
        if (number != (int)ServerGil)
            return false;

        shown = (int)ShownGil;
        return true;
    }

    private bool BelongsToMaskedAddon(AtkResNode* node)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null || node == null)
            return false;

        var addon = manager->GetAddonByNode(node);
        return addon != null && maskedAddons.Contains(addon->Name.ToString());
    }

    private bool MatchesGil(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 48)
            return false;

        Span<char> digits = stackalloc char[24];
        var length = 0;
        foreach (var character in text)
        {
            if (!char.IsDigit(character))
                continue;
            if (length >= digits.Length)
                return false;
            digits[length++] = character;
        }

        return length > 0 && digits[..length].SequenceEqual(ServerGil.ToString(CultureInfo.InvariantCulture));
    }
}
