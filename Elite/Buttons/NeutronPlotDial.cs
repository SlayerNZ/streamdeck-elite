using System;
using System.Threading;
using System.Threading.Tasks;
using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Elite.Buttons
{
    [PluginActionId("com.mhwlng.elite.neutronplotdial")]
    public class NeutronPlotDial : EliteDialBase
    {
        protected class PluginSettings
        {
            public static PluginSettings CreateDefaultSettings() => new PluginSettings
            {
                InfoLeft          = string.Empty,
                InfoLeftColor     = "#ffffff",
                InfoRight         = string.Empty,
                InfoRightColor    = "#ffffff",
                FunctionLongPress = string.Empty,
            };

            [JsonProperty(PropertyName = "infoLeft")]
            public string InfoLeft { get; set; }

            [JsonProperty(PropertyName = "infoLeftColor")]
            public string InfoLeftColor { get; set; }

            [JsonProperty(PropertyName = "infoRight")]
            public string InfoRight { get; set; }

            [JsonProperty(PropertyName = "infoRightColor")]
            public string InfoRightColor { get; set; }

            [JsonProperty(PropertyName = "functionLongPress")]
            public string FunctionLongPress { get; set; }
        }

        private const string LayoutDual   = "layout3.json";
        private const string LayoutWideLg = "layout3_wide.json";    // ≤ 12 chars — 24px
        private const string LayoutWideMd = "layout3_wide_md.json"; // 13–20 chars — 18px
        private const string LayoutWideSm = "layout3_wide_sm.json"; // 21+ chars   — 14px

        private static string WideLayoutFor(string name) =>
            name == null || name.Length <= 12 ? LayoutWideLg :
            name.Length <= 20                 ? LayoutWideMd : LayoutWideSm;

        private static readonly TimeSpan LongPressThreshold = TimeSpan.FromMilliseconds(2000);

        private PluginSettings settings;
        private DateTime dialPressedAt = DateTime.MinValue;
        private string currentLayout = LayoutDual;

        public NeutronPlotDial(SDConnection connection, InitialPayload payload) : base(connection, payload)
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

        public override void DialRotate(DialRotatePayload payload)
        {
            var ticks = Math.Abs(payload.Ticks);
            if (payload.Ticks > 0)
            {
                for (var i = 0; i < ticks; i++)
                    NeutronPlotRoute.RouteNext();
            }
            else if (payload.Ticks < 0)
            {
                for (var i = 0; i < ticks; i++)
                    NeutronPlotRoute.RoutePrevious();
            }
        }

        public override void DialDown(DialPayload payload)
        {
            dialPressedAt = DateTime.UtcNow;
        }

        public override void DialUp(DialPayload payload)
        {
            var elapsed = DateTime.UtcNow - dialPressedAt;
            dialPressedAt = DateTime.MinValue;

            if (elapsed >= LongPressThreshold)
                ExecuteFunction(settings.FunctionLongPress);
            else
                NeutronPlotRoute.RouteSelect();
        }

        public override void TouchPress(TouchpadPressPayload payload)
        {
            if (payload.IsLongPress)
                ExecuteFunction(settings.FunctionLongPress);
            else
                NeutronPlotRoute.RouteSelect();
        }

        public override async void OnTick()
        {
            base.OnTick();
            try
            {
                var snapshot = NeutronPlotRoute.GetSnapshot();

                var leftValue  = snapshot.IsLoaded ? GetDisplayValue(settings.InfoLeft,  snapshot) : "---";
                var rightValue = snapshot.IsLoaded ? GetDisplayValue(settings.InfoRight, snapshot) : "---";

                bool leftIsSystem  = IsSystemNameType(settings.InfoLeft);
                bool rightIsSystem = IsSystemNameType(settings.InfoRight);
                bool useWide       = leftIsSystem || rightIsSystem;

                var wideInfo  = leftIsSystem ? settings.InfoLeft : settings.InfoRight;
                var wideValue = useWide ? (leftIsSystem ? leftValue : rightValue) : null;
                var targetLayout = useWide ? WideLayoutFor(wideValue) : LayoutDual;

                if (targetLayout != currentLayout)
                {
                    await Connection.SetFeedbackLayoutAsync(targetLayout);
                    currentLayout = targetLayout;
                }

                if (useWide)
                {
                    await Connection.SetFeedbackAsync("label", GetAutoLabel(wideInfo));
                    await Connection.SetFeedbackAsync("value", wideValue ?? string.Empty);
                }
                else
                {
                    await Connection.SetFeedbackAsync("label1", GetAutoLabel(settings.InfoLeft));
                    await Connection.SetFeedbackAsync("value1", leftValue  ?? string.Empty);
                    await Connection.SetFeedbackAsync("label2", GetAutoLabel(settings.InfoRight));
                    await Connection.SetFeedbackAsync("value2", rightValue ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotDial OnTick: " + ex);
            }
        }

        public override void ReceivedSettings(ReceivedSettingsPayload payload)
        {
            BarRaider.SdTools.Tools.AutoPopulateSettings(settings, payload.Settings);
        }

        private static bool IsSystemNameType(string infoType) =>
            infoType == "currentSystemName" ||
            infoType == "targetSystemName"  ||
            infoType == "previousSystemName" ||
            infoType == "nextSystemName";

        private static string GetAutoLabel(string infoType)
        {
            switch (infoType)
            {
                case "currentSystemName":   return "CURRENT";
                case "targetSystemName":    return "TARGET";
                case "previousSystemName":  return "PREVIOUS";
                case "nextSystemName":      return "NEXT";
                case "routeStatus":         return "STATUS";
                case "distanceTravelled":   return "TRIP DIST";
                case "distanceTarget":      return "TGT DIST";
                case "jumpsToRejoin":       return "REJOIN";
                case "destinationDistance": return "DEST DIST";
                case "currentJumpNumber":   return "JUMP NBR";
                case "totalJumps":          return "JUMPS TOT";
                case "jumpsRemaining":      return "JUMPS LEFT";
                case "jumpSummary":         return "SUMMARY";
                case "tripPercentage":      return "PROGRESS";
                case "refuelAtTarget":      return "FUEL STOP";
                case "neutronAtTarget":     return "NEUTRON";
                case "jumpRange":           return "RANGE";
                case "fuelMain":            return "FUEL";
                case "nextJumpStar":        return "NEXT STAR";
                default:                    return string.Empty;
            }
        }

        private static string GetDisplayValue(string infoType, NeutronPlotSnapshot snapshot)
        {
            switch (infoType)
            {
                case "currentSystemName":   return snapshot.SystemCurrent;
                case "targetSystemName":    return snapshot.SystemTarget;
                case "routeStatus":         return snapshot.RouteStatus;
                case "distanceTravelled":   return snapshot.WaypointCurrent >= 0 ? $"{snapshot.DistanceTravelled:#,##0.0} LY" : string.Empty;
                case "distanceTarget":
                    if (snapshot.WaypointCurrent >= 0) return $"{snapshot.DistanceTarget:#,##0.0} LY";
                    return snapshot.IsOffRouteEstimate ? $"~{snapshot.DistanceTarget:#,##0.0} LY" : string.Empty;
                case "jumpsToRejoin":       return snapshot.IsOffRouteEstimate ? $"~{snapshot.JumpsToRejoin}" : string.Empty;
                case "destinationDistance": return snapshot.WaypointCurrent >= 0 ? $"{snapshot.DistanceDestination:#,##0.0} LY" : string.Empty;
                case "currentJumpNumber":   return (snapshot.WaypointMax - snapshot.JumpRemaining).ToString();
                case "totalJumps":          return snapshot.WaypointMax.ToString();
                case "jumpsRemaining":      return snapshot.JumpRemaining.ToString();
                case "jumpSummary":         return snapshot.JumpSummary;
                case "tripPercentage":      return $"{snapshot.JumpPercent:F1}%";
                case "refuelAtTarget":      return FormatRefuel(snapshot);
                case "neutronAtTarget":     return snapshot.StarNeutron;
                case "jumpRange":           return FormatJumpRange();
                case "fuelMain":            return $"{EliteData.StatusData.Fuel.FuelMain:0.0}t";
                case "nextJumpStar":        return EliteData.FormatNextJumpStar();
                default:                    return string.Empty;
            }
        }

        // ⛽ glyph behind a constant so it can be swapped for a text tag if it doesn't render on device.
        private const string FuelIcon = "⛽";

        // Extends "Refuel at Target": nearest scoopable star distance once EDSM-enriched, else the
        // original yes/blank fallback until the lookup completes.
        private static string FormatRefuel(NeutronPlotSnapshot snapshot)
        {
            if (!double.IsNaN(snapshot.ScoopableLs))
            {
                if (snapshot.ScoopableLs < 0) return string.Empty;
                return snapshot.ScoopableLs < 100
                    ? $"{FuelIcon} {snapshot.ScoopableLs:0.0} Ls"
                    : $"{FuelIcon} >100 Ls";
            }
            return snapshot.StarRefuel;
        }

        private static string FormatJumpRange()
        {
            double range;
            if (EliteData.FSDOptimalMass > 0 && EliteData.FSDMaxFuelPerJump > 0 && EliteData.UnladenMass > 0)
            {
                var totalMass = EliteData.UnladenMass + EliteData.StatusData.Fuel.FuelMain + EliteData.StatusData.Cargo;
                var fsdRange = EliteData.FSDOptimalMass / totalMass
                    * Math.Pow(EliteData.FSDMaxFuelPerJump / EliteData.FSDLinearConstant, 1.0 / EliteData.FSDPowerConstant);
                var currentRange = fsdRange + EliteData.GuardianFSDBonus;
                range = EliteData.IsFsdBoosted ? currentRange * EliteData.BoostValue : currentRange;
            }
            else
            {
                range = EliteData.BaseJumpRange > 0
                    ? EliteData.BaseJumpRange * (EliteData.IsFsdBoosted ? EliteData.BoostValue : 1.0)
                    : EliteData.LastJumpDistance;
            }
            return $"{range:0.0} LY{(EliteData.IsFsdBoosted ? " ⚡" : "")}";
        }

        private static void ExecuteFunction(string function)
        {
            switch (function)
            {
                case "initializeRoute":
                    NeutronPlotRoute.RouteInitialize();
                    break;
                case "clearRoute":
                    NeutronPlotRoute.CsvClear();
                    break;
            }
        }
    }
}
