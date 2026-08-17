using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVCrystalTransfer.Services;
using FFXIVCrystalTransfer.Windows;

namespace FFXIVCrystalTransfer;

/// <summary>FFXIV-CrystalTransfer 插件入口。</summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "FFXIV-CrystalTransfer";

    private const string CommandName = "/crystaltransfer";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly WindowSystem _windowSystem = new("CrystalTransfer");
    private readonly ConfigWindow _configWindow;
    private readonly TransferService _transfer;

    public Configuration Configuration { get; }

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IDataManager dataManager,
        IGameGui gameGui,
        IObjectTable objectTable,
        IPlayerState playerState,
        IChatGui chatGui)
    {
        _pluginInterface = pi;
        _commandManager = commandManager;

        Configuration = pi.GetPluginConfig() as Configuration ?? new Configuration();

        var inventoryTools = new InventoryToolsClient(pi, log);
        _transfer = new TransferService(framework, log, gameGui, objectTable, inventoryTools, Configuration, chatGui);
        _configWindow = new ConfigWindow(Configuration, _transfer, inventoryTools, dataManager, playerState, pi);

        _windowSystem.AddWindow(_configWindow);

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 FFXIV-CrystalTransfer 配置窗口；/crystaltransfer auto 直接执行水晶转移",
        });

        pi.UiBuilder.Draw += _windowSystem.Draw;
        pi.UiBuilder.OpenConfigUi += ToggleConfig;
    }

    public void Dispose()
    {
        _transfer.Dispose();
        _windowSystem.RemoveAllWindows();
        _configWindow.Dispose();
        _commandManager.RemoveHandler(CommandName);

        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _transfer.Start();
        }
        else
        {
            ToggleConfig();
        }
    }

    private void ToggleConfig() => _configWindow.Toggle();
}
