using System;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ReSharper disable StringLiteralTypo

namespace Elite.Buttons
{

    [PluginActionId("com.mhwlng.elite.explorationdial")]
    public class ExplorationDial : EliteDialBase
    {
        // How long a body resolved in the FSS stays on the touch strip before the
        // display falls back to whatever body you are actually at.
        private const int FssFlashMs = 5000;

        // A fast spin arrives as one payload with several ticks. Cap it so a flick of
        // the encoder cannot queue a long blocking run of keypresses.
        private const int MaxTicksPerRotate = 10;
        private const int TickIntervalMs = 40;

        // Some FSS actions charge while the key is held rather than firing on a tap - the
        // Discovery Scan (honk) is the obvious one. The dial press holds for as long as you
        // hold it, but a touch tap is a single event with no release to follow, so the touch
        // slots hold the key for this long instead.
        private const int DefaultHoldMs = 0;
        private const int MaxHoldMs = 5000;

        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings()
            {
                return new PluginSettings
                {
                    FunctionCcw = string.Empty,
                    FunctionCw = string.Empty,
                    FunctionPress = string.Empty,
                    FunctionTouchPress = string.Empty,
                    FunctionTouchLongPress = string.Empty,
                    HoldMs = DefaultHoldMs.ToString()
                };
            }

            [JsonProperty(PropertyName = "functionccw")]
            public string FunctionCcw { get; set; }

            [JsonProperty(PropertyName = "functioncw")]
            public string FunctionCw { get; set; }

            [JsonProperty(PropertyName = "functionpress")]
            public string FunctionPress { get; set; }

            [JsonProperty(PropertyName = "functiontouchpress")]
            public string FunctionTouchPress { get; set; }

            [JsonProperty(PropertyName = "functiontouchlongpress")]
            public string FunctionTouchLongPress { get; set; }

            [JsonProperty(PropertyName = "holdms")]
            public string HoldMs { get; set; }
        }

        private PluginSettings settings;
        private string _lastTitle = null;
        private string _lastValue = null;

        public ExplorationDial(SDConnection connection, InitialPayload payload) : base(connection, payload)
        {
            if (payload.Settings == null || payload.Settings.Count == 0)
            {
                settings = PluginSettings.CreateDefaultSettings();
                Connection.SetSettingsAsync(JObject.FromObject(settings)).Wait();
            }
            else
            {
                settings = payload.Settings.ToObject<PluginSettings>();
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
        /// Which body the touch strip should describe: one just resolved in the FSS wins
        /// for a few seconds, otherwise the body we are actually at.
        /// </summary>
        private static string DisplayBody()
        {
            if (!string.IsNullOrEmpty(EliteData.LastSignalBody) &&
                (DateTime.UtcNow - EliteData.LastSignalUtc).TotalMilliseconds < FssFlashMs)
            {
                return EliteData.LastSignalBody;
            }

            return EliteData.StatusData?.BodyName;
        }

        /// <summary>
        /// Bodies are named "<system> <designator>"; the system part is noise on a 200px strip.
        /// </summary>
        private static string ShortBodyName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return string.Empty;

            var system = EliteData.StarSystem;
            if (!string.IsNullOrEmpty(system) &&
                bodyName.StartsWith(system, StringComparison.OrdinalIgnoreCase) &&
                bodyName.Length > system.Length)
            {
                return bodyName.Substring(system.Length).Trim();
            }

            return bodyName;
        }

        public override void OnTick()
        {
            base.OnTick();

            var body = DisplayBody();

            var title = string.Empty;
            var value = string.Empty;

            if (!string.IsNullOrEmpty(body) &&
                EliteData.SignalCache.TryGetValue(body, out var signals) &&
                (signals.BiologyCount > 0 || signals.GeologyCount > 0))
            {
                title = ShortBodyName(body);
                value = $"{signals.BiologyCount}\U0001F33F  {signals.GeologyCount}\U0001F30B";
            }

            // Only push when something actually changed - the tick runs continuously and
            // the touch strip does not need redrawing every second.
            if (title == _lastTitle && value == _lastValue) return;

            _lastTitle = title;
            _lastValue = value;

            Task.Run(async () =>
            {
                try
                {
                    await Connection.SetFeedbackAsync("title", title);
                    await Connection.SetFeedbackAsync("value", value);
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.ERROR, $"ExplorationDial SetFeedback: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Discrete press per detent, unlike the generic Dial which holds the key down while
        /// you turn. Everything here - tuning, stepped zoom, genus - is a stepped control, and
        /// holding would either overshoot or run away on key auto-repeat.
        /// </summary>
        private static void SendSteps(string function, int ticks)
        {
            if (string.IsNullOrEmpty(function)) return;

            if (ticks < 1) ticks = 1;
            if (ticks > MaxTicksPerRotate) ticks = MaxTicksPerRotate;

            for (var i = 0; i < ticks; i++)
            {
                if (i > 0) Thread.Sleep(TickIntervalMs);

                EliteKeys.SendKeypress(function);
            }
        }

        public override void DialRotate(DialRotatePayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            if (payload.Ticks > 0)
            {
                SendSteps(settings.FunctionCw, payload.Ticks);
            }
            else if (payload.Ticks < 0)
            {
                SendSteps(settings.FunctionCcw, -payload.Ticks);
            }
        }

        public override void DialDown(DialPayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            // Held, not tapped: the Discovery Scan charges while its key is down, so the dial
            // press has to mirror how long you actually hold the encoder. Discrete actions are
            // unaffected - a quick press is still a quick press.
            EliteKeys.SendKeypressDown(settings.FunctionPress);
        }

        public override void DialUp(DialPayload payload)
        {
            if (Program.Binding == null) return;

            EliteKeys.SendKeypressUp(settings.FunctionPress);
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            if (StreamDeckCommon.InputRunning || Program.Binding == null)
            {
                StreamDeckCommon.ForceStop = true;
                return;
            }

            StreamDeckCommon.ForceStop = false;

            var function = payload.IsLongPress
                ? settings.FunctionTouchLongPress
                : settings.FunctionTouchPress;

            if (string.IsNullOrEmpty(function)) return;

            var hold = Clamp(settings.HoldMs, DefaultHoldMs, 0, MaxHoldMs);

            if (hold <= 0)
            {
                EliteKeys.SendKeypress(function);
                return;
            }

            // A touch is one event with no release to wait for, so synthesise the hold.
            EliteKeys.SendKeypressDown(function);
            Thread.Sleep(hold);
            EliteKeys.SendKeypressUp(function);
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
        }

        public override void Dispose()
        {
            base.Dispose();
        }

    }
}
