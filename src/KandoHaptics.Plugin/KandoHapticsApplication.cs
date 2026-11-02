namespace Loupedeck.KandoHapticsPlugin
{
    using System;

    /// <summary>
    /// Обязательный класс приложения. Logi Plugin Service не загружает сборку плагина без
    /// наследника <see cref="ClientApplication"/> (как в официальном HapticsPlugin), даже если
    /// плагин универсальный (<c>HasNoApplication = true</c>) и ни к какому процессу не привязан.
    /// </summary>
    public class KandoHapticsApplication : ClientApplication
    {
        public KandoHapticsApplication()
        {
        }

        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
