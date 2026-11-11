using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;

namespace GilDom;

public sealed class TradeHandover : IDisposable
{
    public const long TradeLimit = 1_000_000;
    private const float TradeRange = 2.8f;

    private readonly Action<FriendLink, long> finished;
    private readonly ICallGateSubscriber<bool>? navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool>? moveClose;
    private readonly ICallGateSubscriber<bool>? pathRunning;
    private readonly ICallGateSubscriber<object>? pathStop;

    private FriendLink? friend;
    private string tradeName = "";
    private string tradeWorld = "";
    private long remaining;
    private long paidTotal;
    private long offered;
    private long gilAtOpen;
    private bool tradeOpen;
    private bool filling;
    private bool toldMissing;
    private bool toldNav;
    private bool toldFill;
    private int fillFrames;
    private Vector3 lastDest;
    private DateTime nextMoveUtc = DateTime.MinValue;
    private DateTime nextRequestUtc = DateTime.MinValue;

    public bool CountsAsPayment { get; private set; }

    public TradeHandover(Action<FriendLink, long> finished)
    {
        this.finished = finished;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Trade", OnTradeShow);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Trade", OnTradeHide);
        try
        {
            navReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            moveClose = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
            pathRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            pathStop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "vnavmesh is not available");
        }
    }

    public void Start(FriendLink link, long amount, string name, string world)
    {
        if (amount <= 0)
            return;

        friend = link;
        tradeName = name.Trim();
        tradeWorld = world.Trim();
        remaining = amount;
        paidTotal = 0;
        offered = 0;
        filling = false;
        toldMissing = false;
        toldNav = false;
        toldFill = false;
        fillFrames = 0;
        CountsAsPayment = true;
        nextRequestUtc = DateTime.MinValue;
        nextMoveUtc = DateTime.MinValue;
        Plugin.Chat.Print($"GilDom: handing {GilMath.Format(amount)} gil to {link.DisplayName}. Press OK on the trade. GilDom will not press it for you.");
    }

    public void Stop()
    {
        StopWalking();
        friend = null;
        tradeName = "";
        tradeWorld = "";
        remaining = 0;
        CountsAsPayment = false;
        filling = false;
    }

    public void Tick()
    {
        if (friend == null || remaining <= 0)
            return;

        if (tradeOpen)
        {
            StopWalking();
            FillOpenTrade();
            return;
        }

        if (filling)
        {
            CloseTrade();
            return;
        }

        var them = FindPartner();
        if (them == null)
        {
            if (!toldMissing)
            {
                toldMissing = true;
                Plugin.Chat.Print("GilDom: they are not in this area, so the trade has not started.");
            }

            return;
        }

        toldMissing = false;
        var local = Plugin.Objects.LocalPlayer;
        if (local == null)
            return;

        var distance = Vector3.Distance(local.Position, them.Position);
        if (distance <= TradeRange)
        {
            StopWalking();
            if (DateTime.UtcNow >= nextRequestUtc)
            {
                Plugin.TargetManager.Target = them;
                RequestTrade(them.EntityId);
                nextRequestUtc = DateTime.UtcNow.AddSeconds(4);
            }

            return;
        }

        if (!TryWalk(them.Position) && !toldNav)
        {
            toldNav = true;
            Plugin.Chat.Print("GilDom: they are in this area but out of trade range. Walk closer, or install vnavmesh so GilDom can walk over.");
        }
    }

    public void Dispose()
    {
        Stop();
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Trade", OnTradeShow);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Trade", OnTradeHide);
    }

    private void OnTradeShow(AddonEvent type, AddonArgs args)
    {
        tradeOpen = true;
        if (friend == null)
            return;

        gilAtOpen = GilDisplay.ReadServerGil();
        offered = Math.Min(remaining, Math.Min(TradeLimit, gilAtOpen));
        filling = offered > 0;
        fillFrames = 0;
        toldFill = false;
    }

    private void OnTradeHide(AddonEvent type, AddonArgs args)
    {
        tradeOpen = false;
    }

    private void FillOpenTrade()
    {
        if (!filling || offered <= 0)
            return;

        fillFrames++;
        if (fillFrames <= 20 && !GilIsFilled())
            SetGil((uint)offered);

        if (toldFill)
            return;

        toldFill = true;
        var after = Math.Max(0, remaining - offered);
        var more = after > 0 ? $" {GilMath.Format(after)} gil remains after this trade." : "";
        Plugin.Chat.Print($"GilDom: {GilMath.Format(offered)} gil is in the trade. Press OK.{more}");
    }

    private void CloseTrade()
    {
        filling = false;
        var paid = gilAtOpen - GilDisplay.ReadServerGil();
        if (paid <= 0)
        {
            CountsAsPayment = false;
            friend = null;
            Plugin.Chat.Print("GilDom: the trade closed before any gil moved.");
            return;
        }

        paidTotal += paid;
        remaining = Math.Max(0, remaining - paid);
        if (remaining <= 0 || friend == null)
        {
            var done = friend;
            var total = paidTotal;
            Stop();
            if (done != null)
                finished(done, total);
            Plugin.Chat.Print("GilDom: the hand-over is finished.");
            return;
        }

        toldFill = false;
        nextRequestUtc = DateTime.UtcNow.AddSeconds(1);
        Plugin.Chat.Print($"GilDom: {GilMath.Format(remaining)} gil still to hand over. Opening the next trade.");
    }

    private IPlayerCharacter? FindPartner()
    {
        var local = Plugin.Objects.LocalPlayer;
        if (friend == null || local == null || tradeName.Length == 0 || tradeWorld.Length == 0)
            return null;

        foreach (var obj in Plugin.Objects)
        {
            if (obj is not IPlayerCharacter player || player.Address == local.Address)
                continue;
            if (!string.Equals(player.Name.ToString(), tradeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var home = player.HomeWorld.ValueNullable?.Name.ToString() ?? "";
            var current = player.CurrentWorld.ValueNullable?.Name.ToString() ?? "";
            if (string.Equals(home, tradeWorld, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, tradeWorld, StringComparison.OrdinalIgnoreCase))
                return player;
        }

        return null;
    }

    private bool TryWalk(Vector3 dest)
    {
        if (moveClose == null || navReady == null)
            return false;
        if (DateTime.UtcNow < nextMoveUtc && Vector3.Distance(lastDest, dest) < 4f)
            return true;

        try
        {
            if (!navReady.InvokeFunc())
                return false;
            if (pathRunning != null && pathRunning.InvokeFunc() && Vector3.Distance(lastDest, dest) < 4f)
                return true;

            moveClose.InvokeFunc(dest, false, 2f);
            lastDest = dest;
            nextMoveUtc = DateTime.UtcNow.AddSeconds(1);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "vnavmesh could not start the walk");
            return false;
        }
    }

    private void StopWalking()
    {
        if (pathStop == null)
            return;
        try
        {
            if (pathRunning != null && !pathRunning.InvokeFunc())
                return;
            pathStop.InvokeAction();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "vnavmesh could not stop");
        }
    }

    private static unsafe void RequestTrade(uint entityId)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
            inventory->SendTradeRequest(entityId);
    }

    private static unsafe void SetGil(uint amount)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null)
            inventory->SetTradeGilAmount(amount);
    }

    private static unsafe bool GilIsFilled()
    {
        var numbers = TradeNumberArray.Instance();
        return numbers != null && numbers->SendGilCount > 0;
    }
}
