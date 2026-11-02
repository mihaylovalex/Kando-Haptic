namespace Loupedeck.KandoHapticsPlugin
{
    using System;

    // Необязательные действия. Для работы тактильной отдачи их НЕ нужно назначать на кнопки:
    // плагин работает в фоне. Они лишь позволяют быстро выключить вибрацию или проверить её.

    /// <summary>Включает/выключает тактильную отдачу (поле "enabled" в settings.json).</summary>
    public class ToggleKandoHapticsCommand : PluginDynamicCommand
    {
        public ToggleKandoHapticsCommand()
            : base(displayName: "Toggle Kando Haptics", description: "Enables or disables Kando menu haptic feedback", groupName: "Kando Haptics")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            var runtime = (this.Plugin as KandoHapticsPlugin)?.Runtime;
            if (runtime is null)
            {
                return;
            }

            runtime.Settings.SetEnabled(!runtime.Settings.Current.Enabled);
            this.ActionImageChanged();
        }

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize)
        {
            var enabled = (this.Plugin as KandoHapticsPlugin)?.Runtime?.Settings.Current.Enabled ?? true;
            return $"Kando Haptics{Environment.NewLine}{(enabled ? "On" : "Off")}";
        }
    }

    /// <summary>Воспроизводит эффект, назначенный selectButton, для проверки устройства.</summary>
    public class TestKandoHapticsCommand : PluginDynamicCommand
    {
        public TestKandoHapticsCommand()
            : base(displayName: "Test Kando Haptic", description: "Plays the selectButton waveform once", groupName: "Kando Haptics")
        {
        }

        protected override void RunCommand(String actionParameter) =>
            (this.Plugin as KandoHapticsPlugin)?.Runtime?.PlayTest();
    }
}
