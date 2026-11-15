using VigilWatt.Core.Plugins;

namespace TaskBarWidgets.PeripheralHost;

internal static class PluginRegistry
{
    // Explicit built-ins only. No reflection, assembly scanning or external loading.
    public static BatteryPluginBase[] Create() =>
    [
        new VigilWatt.Core.Plugins.Logitech.LogitechPlugin(),
        new VigilWatt.Core.Plugins.Razer.RazerPlugin(),
        new VigilWatt.Core.Plugins.Razer.RazerKrakenPlugin(),
        new VigilWatt.Core.Plugins.Haute.HautePlugin(),
        new VigilWatt.Core.Plugins.Sony.DualSensePlugin(),
        new VigilWatt.Core.Plugins.Sony.InzoneH9Plugin(),
        new VigilWatt.Core.Plugins.Ajazz.AjazzPlugin(),
        new VigilWatt.Core.Plugins.Akg.AkgPlugin(),
        new VigilWatt.Core.Plugins.Asus.AsusPlugin(),
        new VigilWatt.Core.Plugins.Atk.AtkPlugin(),
        new VigilWatt.Core.Plugins.Aula.AulaG9Plugin(),
        new VigilWatt.Core.Plugins.Aula.AulaPlugin(),
        new VigilWatt.Core.Plugins.Betop.BetopPlugin(),
        new VigilWatt.Core.Plugins.Cherry.CherryPlugin(),
        new VigilWatt.Core.Plugins.Darkrim.DarkrimPlugin(),
        new VigilWatt.Core.Plugins.Delux.DeluxPlugin(),
        new VigilWatt.Core.Plugins.Eweadn.EweadnPlugin(),
        new VigilWatt.Core.Plugins.GameSir.GameSirPlugin(),
        new VigilWatt.Core.Plugins.Ganss.GanssPlugin(),
        new VigilWatt.Core.Plugins.Hecate.HecatePlugin(),
        new VigilWatt.Core.Plugins.Hecate.HecateWirelessPlugin(),
        new VigilWatt.Core.Plugins.HyperX.HyperXPlugin(),
        new VigilWatt.Core.Plugins.Inphic.InphicPlugin(),
        new VigilWatt.Core.Plugins.Lingbao.LingbaoPlugin(),
        new VigilWatt.Core.Plugins.Mchose.MchoseA5Plugin(),
        new VigilWatt.Core.Plugins.Mchose.MchosePlugin(),
        new VigilWatt.Core.Plugins.Mojhon.MojhonPlugin(),
        new VigilWatt.Core.Plugins.Pulsar.PulsarPlugin(),
        new VigilWatt.Core.Plugins.Rapoo.RapooPlugin(),
        new VigilWatt.Core.Plugins.Thunderobot.ThunderobotPlugin(),
        new VigilWatt.Core.Plugins.Vgn.VgnPlugin(),
    ];
}
