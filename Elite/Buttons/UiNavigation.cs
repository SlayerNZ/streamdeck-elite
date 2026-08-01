using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        // Hold Mode keeps the configured presses for a tap and only starts holding the key once
        // the button has been down longer than this. Named to match the Exploration Dial's field.
        private const int DefaultHoldThresholdMs = 100;
        private const int MaxHoldThresholdMs = 2000;
        private const int HoldPollMs = 20;

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
                    HoldThresholdMs = DefaultHoldThresholdMs.ToString(),
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

            [JsonProperty(PropertyName = "holdThresholdMs")]
            public string HoldThresholdMs { get; set; }

            [FilenameProperty]
            [JsonProperty(PropertyName = "clickSound")]
            public string ClickSoundFilename { get; set; }
        }


        PluginSettings settings;
        private CachedSound _clickSound = null;
        private readonly object _holdLock = new object();
        private bool _keyIsDown = false;      // the game key is currently held down
        private bool _buttonIsDown = false;   // the Stream Deck button is currently held down
        private int _pressToken = 0;          // invalidates a pending hold watch from an older press


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

                // A button placed before a setting existed has no key for it, so it deserialises
                // as null and would clamp to whatever the floor happens to be. Fill the gap so
                // older buttons show a real value in the property inspector.
                if (string.IsNullOrEmpty(settings.HoldThresholdMs))
                {
                    settings.HoldThresholdMs = DefaultHoldThresholdMs.ToString();
                }

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

            // A tap keeps the deterministic press count above. Only once the button has been held
            // past the threshold does the key go down and stay down, so menu buttons configured
            // with Presses = 2 or 3 still land on exactly that many steps.
            if (settings.HoldMode && IsHoldable(settings.Function))
            {
                StartHoldWatch();
            }
        }

        /// <summary>
        /// Waits for the hold threshold, then presses the key and leaves it down until release.
        /// Abandoned if the button comes up first, which is the ordinary tap case.
        /// </summary>
        private void StartHoldWatch()
        {
            var threshold = Clamp(settings.HoldThresholdMs, DefaultHoldThresholdMs, 0, MaxHoldThresholdMs);
            var token = ++_pressToken;

            _buttonIsDown = true;

            Task.Run(() =>
            {
                var waited = 0;
                while (waited < threshold)
                {
                    Thread.Sleep(HoldPollMs);
                    waited += HoldPollMs;

                    lock (_holdLock)
                    {
                        // Released, or superseded by a newer press - this was a tap.
                        if (!_buttonIsDown || token != _pressToken) return;
                    }
                }

                lock (_holdLock)
                {
                    if (!_buttonIsDown || token != _pressToken) return;

                    _keyIsDown = true;
                    EliteKeys.SendKeypressDown(settings.Function);
                }
            });
        }

        /// <summary>
        /// Ends the press. Releases the key if the hold had started; a plain tap has nothing to do.
        /// Guarded by _keyIsDown so a press blocked by the condition check cannot produce an
        /// unmatched key-up.
        /// </summary>
        public override void KeyReleased(KeyPayload payload)
        {
            lock (_holdLock)
            {
                _buttonIsDown = false;

                if (!_keyIsDown) return;

                _keyIsDown = false;
                EliteKeys.SendKeypressUp(settings.Function);
            }
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
