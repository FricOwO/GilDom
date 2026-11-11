using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GilDom.Windows;

namespace GilDom;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;

    private const string CommandName = "/gildom";

    public static Configuration Configuration { get; private set; } = null!;
    public static Session Session { get; private set; } = null!;

    private readonly GilDisplay display;
    private readonly WindowSystem windowSystem = new("GilDom");
    private readonly MainWindow window;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Prepare();
        if (ClientState.IsLoggedIn)
            Configuration.Bind(CurrentContentId());

        display = new GilDisplay();
        Session = new Session(display);
        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
        window = new MainWindow();
        windowSystem.AddWindow(window);

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += window.Toggle;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the GilDom window",
        });
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= window.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= window.Toggle;
        windowSystem.RemoveAllWindows();
        window.Dispose();
        Session.Dispose();
        display.Dispose();
    }

    internal static void EnsureCharacter()
    {
        var id = CurrentContentId();
        var idText = id == 0 ? "" : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Configuration.ActiveContentId == idText)
            return;

        if (Configuration.IsBound)
            Session?.ReleaseCharacter();

        if (id == 0)
            Configuration.Unbind();
        else
            Configuration.Bind(id);

        Session?.AdoptCharacter();
    }

    private static unsafe ulong CurrentContentId()
    {
        if (!ClientState.IsLoggedIn)
            return 0;

        var state = PlayerState.Instance();
        return state == null ? 0 : state->ContentId;
    }

    private static void OnLogin() => EnsureCharacter();

    private static void OnLogout(int type, int code) => EnsureCharacter();

    private void OnCommand(string command, string args) => window.Toggle();
}
