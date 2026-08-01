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
                    Condition = string.Empty,
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

            [JsonProperty(PropertyName = "condition")]
            public string Condition { get; set; }

            [JsonProperty(PropertyName = "holdMode")]
            public bool HoldMode { get; set; }

            [FilenameProperty]
            [JsonProperty(PropertyName = "clickSound")]
            public string ClickSoundFilename { get; set; }
        }


        PluginSettings settings;
        private CachedSound _clickSound = null;
        private bool _keyIsDown = false;


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

        /// <summary>
        /// True when the configured condition holds, or when no condition is set.
        /// Fails closed: an unrecognised condition blocks the press rather than
        /// letting a guarded button fire unguarded.
        /// </summary>
        private bool ConditionMet()
        {
            if (string.IsNullOrEmpty(settings.Condition)) return true;

            if (!Enum.TryParse<Profile.ProfileType>(settings.Condition, out var profileType))
            {
                Logger.Instance.LogMessage(TracingLevel.WARN,
                    $"UiNavigation: unknown condition '{settings.Condition}', blocking keypress");
                return false;
            }

            return StreamDeckCommon.CheckProfileState(profileType);
        }

        public override void KeyPressed(KeyPayload payload)
        {
            if (string.IsNullOrEmpty(settings.Function)) return;

            // Checked once up front, not per press: a multi-press should not abort
            // half way through because the state changed as a result of its own input.
            if (!ConditionMet()) return;

            // Hold mode maps the button straight onto the key, exactly like a real keyboard:
            // a tap is one short press (one step), holding it down repeats via the game's own
            // auto-repeat, and releasing stops immediately. Presses/Interval do not apply.
            if (settings.HoldMode && IsHoldable(settings.Function))
            {
                _keyIsDown = true;
                EliteKeys.SendKeypressDown(settings.Function);

                PlayClickSound();
                return;
            }

            if (settings.HoldMode)
            {
                // Composite action with no keydown/keyup dispatcher - fall back to a normal press
                // rather than silently doing nothing.
                EliteKeys.SendKeypress(settings.Function);

                PlayClickSound();
                return;
            }

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

            PlayClickSound();
        }

        /// <summary>
        /// Releases the key in hold mode. Guarded by _keyIsDown so a press blocked by the
        /// condition check cannot produce an unmatched key-up.
        /// </summary>
        public override void KeyReleased(KeyPayload payload)
        {
            if (!_keyIsDown) return;

            _keyIsDown = false;
            EliteKeys.SendKeypressUp(settings.Function);
        }

        /// <summary>
        /// The "-ON" style entries are composite actions that check GuiFocus and send a short
        /// sequence; EliteKeys only dispatches them as a whole press, with no keydown/keyup pair.
        /// Holding one would send nothing at all, so they are excluded from hold mode.
        /// </summary>
        private static bool IsHoldable(string function)
        {
            return !string.IsNullOrEmpty(function) && function.IndexOf('-') < 0;
        }

        private void PlayClickSound()
        {
            if (_clickSound == null) return;

            try
            {
                AudioPlaybackEngine.Instance.PlaySound(_clickSound);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, $"PlaySound: {ex}");
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
