using System;
using System.IO;
using System.Threading;
using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ReSharper disable StringLiteralTypo

namespace Elite.Buttons
{

    [PluginActionId("com.mhwlng.elite.uinavigation")]
    public class UiNavigation : EliteKeypadBase
    {
        // A press count of 1 makes the interval irrelevant, so both fields can stay
        // visible all the time. Clamped so a typo in the property inspector can't
        // lock up the plugin thread for minutes.
        private const int DefaultPresses = 1;
        private const int MaxPresses = 50;
        private const int DefaultIntervalMs = 50;
        private const int MaxIntervalMs = 2000;

        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                var instance = new PluginSettings
                {
                    Function = string.Empty,
                    Presses = DefaultPresses.ToString(),
                    IntervalMs = DefaultIntervalMs.ToString(),
                    ClickSoundFilename = string.Empty
                };

                return instance;
            }

            [JsonProperty(PropertyName = "function")]
            public string Function { get; set; }

            [JsonProperty(PropertyName = "presses")]
            public string Presses { get; set; }

            [JsonProperty(PropertyName = "intervalMs")]
            public string IntervalMs { get; set; }

            [FilenameProperty]
            [JsonProperty(PropertyName = "clickSound")]
            public string ClickSoundFilename { get; set; }
        }


        PluginSettings settings;
        private CachedSound _clickSound = null;


        public UiNavigation(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else
            {
                settings = payload.Settings.ToObject<PluginSettings>();
                HandleFileNames();
            }
        }

        private static int Clamp(string value, int fallback, int min, int max)
        {
            if (!int.TryParse(value, out var result)) result = fallback;

            if (result < min) result = min;
            if (result > max) result = max;

            return result;
        }

        public override void KeyPressed(KeyPayload payload)
        {
            if (string.IsNullOrEmpty(settings.Function)) return;

            var presses = Clamp(settings.Presses, DefaultPresses, 1, MaxPresses);
            var interval = Clamp(settings.IntervalMs, DefaultIntervalMs, 0, MaxIntervalMs);

            // Deliberately synchronous: the menu has to settle between presses, and
            // returning early would let the next step of a multi-action interleave
            // its keystrokes with ours.
            for (var i = 0; i < presses; i++)
            {
                if (i > 0 && interval > 0)
                {
                    Thread.Sleep(interval);
                }

                EliteKeys.SendKeypress(settings.Function);
            }

            if (_clickSound != null)
            {
                try
                {
                    AudioPlaybackEngine.Instance.PlaySound(_clickSound);
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.FATAL, $"PlaySound: {ex}");
                }
            }
        }

        public override void Dispose()
        {
            base.Dispose();
        }


        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
            HandleFileNames();
        }

        private void HandleFileNames()
        {
            _clickSound = null;
            if (File.Exists(settings.ClickSoundFilename))
            {
                try
                {
                    _clickSound = new CachedSound(settings.ClickSoundFilename);
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.FATAL, $"CachedSound: {settings.ClickSoundFilename} {ex}");

                    _clickSound = null;
                    settings.ClickSoundFilename = null;
                }
            }

            Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
        }

    }
}
