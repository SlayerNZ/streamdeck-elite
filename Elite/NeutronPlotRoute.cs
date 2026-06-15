using BarRaider.SdTools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Elite
{
    public class NeutronPlotWaypoint
    {
        public string SystemName { get; set; } = string.Empty;
        public double JumpDistance { get; set; }
        public double DistanceRemaining { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public double CumulativeDistance { get; set; }

        // Spansh system id64 — stored for Phase 2 per-system scoopable enrichment. 0 for CSV routes.
        public long Id64 { get; set; }

        // Exact-plotter (Phase 3) fuel modelling — populated for Spansh auto-plot routes only.
        public bool IsScoopable { get; set; }
        public bool MustInject { get; set; }
        public double FuelInTank { get; set; }
        public double FuelUsed { get; set; }

        // Off-route EDSM enrichment — persisted with the route, wiped on replot.
        // Nullable so "not looked up yet" is distinct from a real 0/absent value.
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }
        public double? FuelStarLs { get; set; }   // nearest scoopable star distance (Ls); -1 = none
    }

    public class NeutronPlotSnapshot
    {
        public string CsvPath { get; set; } = string.Empty;
        public bool IsLoaded { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int WaypointTarget { get; set; }
        public int WaypointMax { get; set; }
        public string SystemCurrent { get; set; } = string.Empty;
        public string RouteStatus { get; set; } = string.Empty;
        public int WaypointCurrent { get; set; } = -1;
        public string SystemTarget { get; set; } = string.Empty;
        public string SystemDestination { get; set; } = string.Empty;
        public int JumpRemaining { get; set; }
        public string JumpSummary { get; set; } = string.Empty;
        public double JumpPercent { get; set; }
        public double DistanceTravelled { get; set; }
        public double DistanceTarget { get; set; }
        public double DistanceDestination { get; set; }
        public bool IsRefuel { get; set; }
        public bool IsNeutron { get; set; }
        public string StarRefuel { get; set; } = string.Empty;
        public string StarNeutron { get; set; } = string.Empty;

        // Spansh auto-plot state
        public bool IsPlotting { get; set; }
        public bool IsSpanshRoute { get; set; }
        public string SpanshError { get; set; } = string.Empty;

        // Nearest scoopable star to the target system, in light-seconds (Phase 2 EDSM enrichment).
        // double.NaN = not yet looked up; -1 = looked up, none found; >= 0 = nearest scoopable distance.
        public double ScoopableLs { get; set; } = double.NaN;
    }

    public static class NeutronPlotRoute
    {
        private const string StateFileName = "neutronPlotState.json";
        // Single serialized waypoint table for BOTH route types (CSV + Spansh), with EDSM
        // enrichment (coords/fuel) baked in. Replaced on each replot; wiped on clear.
        private const string WaypointsFileName = "neutronRouteWaypoints.json";

        // EDSM enrichment is written straight onto the waypoints (FuelStarLs / X,Y,Z) and persists
        // with the serialized route, so there is no separate cross-route cache. enrichInFlight just
        // dedupes concurrent lookups for the same system name while a fetch is running.
        private static readonly HashSet<string> enrichInFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object SyncRoot = new object();
        private static readonly List<NeutronPlotWaypoint> Waypoints = new List<NeutronPlotWaypoint>();
        private static PersistedState state = new PersistedState();

        // Spansh auto-plot — shared HttpClient + cancellable background polling task
        private static readonly HttpClient Http = new HttpClient();
        private static volatile bool isPlotting;
        private static string spanshError = string.Empty;
        private static DateTime spanshErrorAt = DateTime.MinValue;
        private static readonly TimeSpan SpanshErrorDisplay = TimeSpan.FromSeconds(8);
        private static CancellationTokenSource spanshCts;

        public static bool IsPlotting => isPlotting;

        private class PersistedState
        {
            public string CsvPath { get; set; } = string.Empty;
            public DateTime CsvLastWriteTimeUtc { get; set; }
            public long CsvFileSizeBytes { get; set; }
            public int WaypointTarget { get; set; }
            public string SystemCurrent { get; set; } = string.Empty;
            public bool IsSpanshRoute { get; set; }
        }

        private static int WaypointMax => Waypoints.Count > 0 ? Waypoints.Count - 1 : 0;

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadState();
                if (state.IsSpanshRoute)
                {
                    if (!LoadWaypointsFromDisk()) state.IsSpanshRoute = false;
                }
                else
                {
                    ReloadRoute(resetIndexIfChanged: true);
                }
            }
        }

        public static NeutronPlotSnapshot RefreshFromDisk()
        {
            lock (SyncRoot)
            {
                ReloadRoute(resetIndexIfChanged: true);
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot GetSnapshot()
        {
            lock (SyncRoot)
            {
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot CsvNew(string csvPath)
        {
            lock (SyncRoot)
            {
                // Choosing a CSV cancels any in-progress Spansh fetch and replaces the active route.
                CancelSpanshPlot();
                state.IsSpanshRoute = false;
                DeleteWaypointsFile();

                state.CsvPath = csvPath ?? string.Empty;
                state.WaypointTarget = 0;
                state.CsvLastWriteTimeUtc = DateTime.MinValue;
                state.CsvFileSizeBytes = 0;

                ReloadRoute(resetIndexIfChanged: true);
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot CsvClear()
        {
            lock (SyncRoot)
            {
                // Clear File cancels any in-progress Spansh fetch and clears the active route.
                CancelSpanshPlot();
                state.IsSpanshRoute = false;
                DeleteWaypointsFile();

                state.CsvPath = string.Empty;
                state.CsvLastWriteTimeUtc = default;
                state.CsvFileSizeBytes = 0;
                ClearRouteState();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot SetWaypointTarget(int waypointTarget)
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = ClampIndex(waypointTarget);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteClear()
        {
            lock (SyncRoot)
            {
                ClearRouteState();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteInitialize()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = GetInitialWaypoint();
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteNext()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = Math.Min(state.WaypointTarget + 1, WaypointMax);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RoutePrevious()
        {
            lock (SyncRoot)
            {
                state.WaypointTarget = Math.Max(state.WaypointTarget - 1, 0);
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteSelect()
        {
            lock (SyncRoot)
            {
                var snapshot = CreateSnapshot(string.Empty);
                if (!string.IsNullOrEmpty(snapshot.SystemTarget))
                {
                    var text = snapshot.SystemTarget;
                    var thread = new Thread(() =>
                    {
                        try { Clipboard.SetText(text); }
                        catch (Exception ex) { Logger.Instance.LogMessage(TracingLevel.ERROR, "RouteSelect clipboard: " + ex); }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                }
                return snapshot;
            }
        }

        public static NeutronPlotSnapshot SetSystemCurrent(string systemName)
        {
            lock (SyncRoot)
            {
                state.SystemCurrent = systemName ?? string.Empty;
                SaveState();
                return CreateSnapshot(string.Empty);
            }
        }

        public static NeutronPlotSnapshot RouteAutoAdvance(string systemName)
        {
            lock (SyncRoot)
            {
                state.SystemCurrent = systemName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(systemName) && Waypoints.Count > 0)
                {
                    var lastIndex = -1;
                    for (var i = 0; i < Waypoints.Count; i++)
                    {
                        if (string.Equals(Waypoints[i].SystemName, systemName, StringComparison.OrdinalIgnoreCase))
                            lastIndex = i;
                    }
                    if (lastIndex >= 0)
                    {
                        var next = Math.Min(lastIndex + 1, WaypointMax);
                        if (next != state.WaypointTarget)
                        {
                            state.WaypointTarget = next;
                            SaveState();
                        }
                    }
                }
                return CreateSnapshot(string.Empty);
            }
        }

        private static void ReloadRoute(bool resetIndexIfChanged)
        {
            // Spansh routes carry no CSV on disk — just load the serialized waypoint table.
            if (state.IsSpanshRoute)
            {
                if (!LoadWaypointsFromDisk()) state.IsSpanshRoute = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(state.CsvPath) || !File.Exists(state.CsvPath))
            {
                Waypoints.Clear();
                return;
            }

            var fileInfo = new FileInfo(state.CsvPath);
            var fileChanged = fileInfo.LastWriteTimeUtc != state.CsvLastWriteTimeUtc ||
                              fileInfo.Length != state.CsvFileSizeBytes;

            // Unchanged CSV: reuse the serialized table so EDSM enrichment (coords/fuel) survives
            // restarts instead of being thrown away by a re-parse.
            if (!fileChanged && LoadWaypointsFromDisk())
            {
                state.WaypointTarget = ClampIndex(state.WaypointTarget);
                return;
            }

            // Changed (or no serialized table yet): re-parse the CSV from scratch.
            Waypoints.Clear();

            var rows = ReadCsvRows(state.CsvPath);

            if (!CsvCheck(rows))
            {
                ClearRouteState();
                SaveState();
                return;
            }

            foreach (var row in rows)
            {
                if (row.Count == 0)
                {
                    continue;
                }

                Waypoints.Add(new NeutronPlotWaypoint
                {
                    SystemName = GetColumn(row, 0),
                    JumpDistance = ParseDouble(GetColumn(row, 1)),
                    DistanceRemaining = ParseDouble(GetColumn(row, 2)),
                    IsRefuel = IsYes(GetColumn(row, 5)),
                    IsNeutron = IsYes(GetColumn(row, 6))
                });
            }

            // Precompute cumulative distances for O(1) distance lookups
            var cumulative = 0.0;
            foreach (var wp in Waypoints)
            {
                cumulative += wp.JumpDistance;
                wp.CumulativeDistance = cumulative;
            }

            state.CsvLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            state.CsvFileSizeBytes = fileInfo.Length;

            // Persist the freshly parsed table; EDSM enrichment fills coords/fuel in later and re-saves.
            SaveWaypoints();

            if (fileChanged && resetIndexIfChanged)
            {
                state.WaypointTarget = GetInitialWaypoint();
            }
            else
            {
                state.WaypointTarget = ClampIndex(state.WaypointTarget);
            }

            SaveState();
        }

        private static List<List<string>> ReadCsvRows(string csvPath)
        {
            var rows = new List<List<string>>();
            var isHeader = true;

            foreach (var line in File.ReadLines(csvPath))
            {
                if (isHeader)
                {
                    isHeader = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                rows.Add(ParseCsvLine(line));
            }

            return rows;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var values = new List<string>();
            var value = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    values.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(c);
                }
            }

            values.Add(value.ToString());
            return values;
        }

        private static string GetColumn(IReadOnlyList<string> row, int index)
        {
            return index < row.Count ? row[index] : string.Empty;
        }

        private static void ClearRouteState()
        {
            Waypoints.Clear();
            state.WaypointTarget = 0;
        }

        private static bool IsYes(string value)
        {
            return string.Equals(value?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static double ParseDouble(string value)
        {
            return double.TryParse(value?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0.0;
        }

        private static bool CsvCheck(List<List<string>> rows)
        {
            if (rows.Count <= 2) return false;
            foreach (var row in rows)
            {
                if (row.Count != 7) return false;
                if (!double.TryParse(row[1]?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
                if (!double.TryParse(row[2]?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
            }
            return true;
        }

        private static int GetInitialWaypoint()
        {
            return Math.Min(1, WaypointMax);
        }

        private static int ClampIndex(int waypointTarget)
        {
            if (Waypoints.Count == 0)
            {
                return 0;
            }

            if (waypointTarget < 0) 
            {
                return 0;
            }

            var maxIndex = Waypoints.Count - 1;
            return waypointTarget > maxIndex ? maxIndex : waypointTarget;
        }

        private static string ComputeRouteStatus(string systemCurrent)
        {
            if (string.IsNullOrWhiteSpace(systemCurrent))
                return "OFF ROUTE";

            var isAtDest   = string.Equals(Waypoints[WaypointMax].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase);
            if (isAtDest)   return "At Dest";

            var isAtOrigin = string.Equals(Waypoints[0].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase);
            if (isAtOrigin) return "At Origin";

            for (var i = 1; i < Waypoints.Count - 1; i++)
            {
                if (string.Equals(Waypoints[i].SystemName, systemCurrent, StringComparison.OrdinalIgnoreCase))
                    return "On Route";
            }

            return "OFF ROUTE";
        }

        private static NeutronPlotSnapshot CreateSnapshot(string errorMessage)
        {
            var snapshot = new NeutronPlotSnapshot
            {
                CsvPath = state.CsvPath,
                IsLoaded = Waypoints.Count > 0,
                ErrorMessage = errorMessage,
                WaypointTarget = state.WaypointTarget,
                WaypointMax = WaypointMax,
                SystemCurrent = state.SystemCurrent,
                IsPlotting = isPlotting,
                IsSpanshRoute = state.IsSpanshRoute,
                SpanshError = (DateTime.UtcNow - spanshErrorAt) < SpanshErrorDisplay ? spanshError : string.Empty,
            };

            if (Waypoints.Count == 0)
            {
                snapshot.RouteStatus = "NO ROUTE";
                return snapshot;
            }

            // SystemDestination is always the last row, independent of WaypointTarget
            snapshot.SystemDestination = Waypoints[WaypointMax].SystemName;

            snapshot.RouteStatus = ComputeRouteStatus(state.SystemCurrent);

            // WaypointCurrent — index of SystemCurrent in the route (last occurrence, -1 if off-route)
            var waypointCurrentIndex = -1;
            if (!string.IsNullOrWhiteSpace(state.SystemCurrent))
            {
                for (var i = 0; i < Waypoints.Count; i++)
                {
                    if (string.Equals(Waypoints[i].SystemName, state.SystemCurrent, StringComparison.OrdinalIgnoreCase))
                        waypointCurrentIndex = i;
                }
            }
            snapshot.WaypointCurrent = waypointCurrentIndex;

            // Distance fields based on WaypointCurrent
            if (waypointCurrentIndex >= 0)
            {
                var cumCurrent = Waypoints[waypointCurrentIndex].CumulativeDistance;
                snapshot.DistanceTravelled    = cumCurrent;
                snapshot.DistanceDestination  = Waypoints[WaypointMax].CumulativeDistance - cumCurrent;

                var waypointTargetClamped = Math.Max(0, Math.Min(state.WaypointTarget, WaypointMax));
                snapshot.DistanceTarget = waypointTargetClamped >= waypointCurrentIndex
                    ? Waypoints[waypointTargetClamped].CumulativeDistance - cumCurrent
                    : 0.0;
            }

            // WaypointTarget fields
            var waypointIndex = state.WaypointTarget;
            if (waypointIndex < 0 || waypointIndex >= Waypoints.Count)
            {
                return snapshot;
            }

            var waypoint = Waypoints[waypointIndex];
            snapshot.SystemTarget = waypoint.SystemName;
            snapshot.IsRefuel = waypoint.IsRefuel;
            snapshot.IsNeutron = waypoint.IsNeutron;

            // Phase 2: surface the cached scoopable distance for the target and lazily enrich it.
            // Triggered here so Next/Previous/auto-advance (button + dial) all enrich the new target.
            snapshot.ScoopableLs = waypoint.FuelStarLs ?? double.NaN;
            EnsureEnriched(waypoint.SystemName);
            snapshot.WaypointTarget = Math.Max(0, state.WaypointTarget);

            // Use WaypointCurrent for position metrics; fall back to WaypointTarget-1 when off-route
            var positionIndex = snapshot.WaypointCurrent >= 0
                ? snapshot.WaypointCurrent
                : Math.Max(0, state.WaypointTarget - 1);
            snapshot.JumpRemaining = Math.Max(0, snapshot.WaypointMax - positionIndex);
            snapshot.JumpSummary = $"{positionIndex}/{snapshot.WaypointMax}";
            var totalDistance = snapshot.DistanceTravelled + snapshot.DistanceDestination;
            snapshot.JumpPercent = totalDistance > 0
                ? snapshot.DistanceTravelled / totalDistance * 100.0
                : 0.0;
            snapshot.StarRefuel = waypoint.IsRefuel ? "Refuel" : string.Empty;
            snapshot.StarNeutron = waypoint.IsNeutron ? "Neutron" : string.Empty;
            return snapshot;
        }

        // ---- Spansh auto-plot (exact / galaxy plotter) -----------------------

        // Kicks off a background fuel-aware route fetch from Spansh's exact plotter (/api/generic/route)
        // using live ship + target data. Origin/destination come from the current and FSD-targeted system
        // id64s; the ship's fuel model is supplied from EliteData (no Coriolis build needed).
        // Returns false (leaving any existing route untouched) when it cannot start — no FSD target,
        // no origin, missing ship data, or a fetch already running. Callers flash the SD alert icon.
        // The current route is only replaced once a new result successfully arrives.
        public static bool StartSpanshPlot()
        {
            lock (SyncRoot)
            {
                if (isPlotting) return false;

                var from = EliteData.StarSystemAddress;
                var to = EliteData.FsdTargetAddress;

                if (from == 0)
                {
                    SetSpanshError("NO ORIGIN");
                    return false;
                }
                if (to == 0)
                {
                    SetSpanshError("NO TARGET");
                    return false;
                }
                // Need the FSD fuel model to drive the exact plotter (seen from the Loadout event).
                if (EliteData.FSDOptimalMass <= 0 || EliteData.FSDMaxFuelPerJump <= 0 ||
                    EliteData.UnladenMass <= 0 || EliteData.FuelCapacityMain <= 0)
                {
                    SetSpanshError("NO SHIP");
                    return false;
                }

                spanshError = string.Empty;
                spanshCts?.Cancel();
                spanshCts?.Dispose();
                spanshCts = new CancellationTokenSource();
                isPlotting = true;

                var form = BuildExactPlotterForm(from, to);
                var ct = spanshCts.Token;
                _ = Task.Run(() => FetchFromSpanshAsync(from, to, form, ct));
                return true;
            }
        }

        // Assembles the exact-plotter form parameters from live EliteData. Snapshotted here (under lock)
        // so the background task uses a stable copy of the ship state at submit time.
        private static Dictionary<string, string> BuildExactPlotterForm(long source, long destination)
        {
            string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            return new Dictionary<string, string>
            {
                ["source"] = source.ToString(CultureInfo.InvariantCulture),
                ["destination"] = destination.ToString(CultureInfo.InvariantCulture),
                ["is_supercharged"] = "0",
                ["use_supercharge"] = "1",   // allow neutron supercharging
                ["use_injections"] = "0",
                ["use_injections_when_required"] = "0",
                ["exclude_secondary"] = "0",
                ["refuel_every_scoopable"] = "0",
                ["fuel_power"] = N(EliteData.FSDPowerConstant),
                ["fuel_multiplier"] = N(EliteData.FSDLinearConstant),
                ["optimal_mass"] = N(EliteData.FSDOptimalMass),
                ["base_mass"] = N(EliteData.UnladenMass),                 // fuel-excluded (= Coriolis dryMass)
                ["tank_size"] = N(EliteData.FuelCapacityMain),
                ["internal_tank_size"] = N(EliteData.FuelCapacityReserve),
                ["reserve_size"] = "0",   // no extra main-tank buffer; plan with the full usable tank
                ["max_fuel_per_jump"] = N(EliteData.FSDMaxFuelPerJump),
                ["range_boost"] = N(EliteData.GuardianFSDBonus),
                ["supercharge_multiplier"] = "4",
                ["injection_multiplier"] = "1.25",
                ["max_time"] = "60",
                ["cargo"] = N(EliteData.StatusData.Cargo),
                ["algorithm"] = "optimistic",
                ["ship_build"] = ""
            };
        }

        private static async Task FetchFromSpanshAsync(long source, long destination, Dictionary<string, string> form, CancellationToken ct)
        {
            try
            {
                // Spansh can only route between systems in its DB. Unvisited procgen frontier systems
                // aren't there — pre-check so the user gets a clear reason instead of a server 500.
                if (!await SystemKnownAsync(destination, ct).ConfigureAwait(false))
                {
                    SetSpanshError("DEST UNKNOWN");
                    EndPlotting();
                    return;
                }
                if (!await SystemKnownAsync(source, ct).ConfigureAwait(false))
                {
                    SetSpanshError("ORIGIN UNKNOWN");
                    EndPlotting();
                    return;
                }

                using (var content = new FormUrlEncodedContent(form))
                {
                    var submitResp = await Http.PostAsync("https://spansh.co.uk/api/generic/route", content, ct).ConfigureAwait(false);
                    var submitBody = await submitResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var submit = JsonConvert.DeserializeObject<SpanshSubmitResponse>(submitBody);

                    if (submit != null && string.Equals(submit.Status, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Instance.LogMessage(TracingLevel.ERROR, "Spansh submit error: " + submitBody);
                        SetSpanshError("API ERROR");
                        EndPlotting();
                        return;
                    }
                    if (submit == null || string.IsNullOrEmpty(submit.Job))
                    {
                        Logger.Instance.LogMessage(TracingLevel.ERROR, $"Spansh submit HTTP {(int)submitResp.StatusCode}: {submitBody}");
                        SetSpanshError(submitResp.IsSuccessStatusCode ? "NO JOB" : "API ERROR");
                        EndPlotting();
                        return;
                    }

                    // Poll until HTTP 200 (complete). 202 = still processing. No overall timeout.
                    var resultsUrl = "https://spansh.co.uk/api/results/" + submit.Job;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        var resp = await Http.GetAsync(resultsUrl, ct).ConfigureAwait(false);
                        if (resp.StatusCode == HttpStatusCode.OK)
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var parsed = JsonConvert.DeserializeObject<SpanshResultResponse>(body);
                            var jumps = parsed?.Result?.Jumps;
                            if (jumps == null || jumps.Count == 0)
                            {
                                SetSpanshError("NO ROUTE");
                                EndPlotting();
                                return;
                            }

                            ApplySpanshResult(jumps);
                            EndPlotting();
                            return;
                        }

                        if (resp.StatusCode == HttpStatusCode.Accepted)
                        {
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                            continue;
                        }

                        // Job failed server-side (most often an un-routable system) — surface + log.
                        Logger.Instance.LogMessage(TracingLevel.ERROR, $"Spansh results HTTP {(int)resp.StatusCode} for job {submit.Job}");
                        SetSpanshError((int)resp.StatusCode >= 500 ? "NO ROUTE" : "API ERROR");
                        EndPlotting();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by Clear/Choose File — leave route as-is, just stop plotting.
                EndPlotting();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute FetchFromSpanshAsync " + ex);
                SetSpanshError("API ERROR");
                EndPlotting();
            }
        }

        private static void EndPlotting()
        {
            lock (SyncRoot) { isPlotting = false; }
        }

        // Is the system in Spansh's database? /api/system/{id64}: 200 = known, 404 = unknown.
        // On a transient/network error we assume known (true) so we don't wrongly block a valid plot.
        private static async Task<bool> SystemKnownAsync(long id64, CancellationToken ct)
        {
            try
            {
                var resp = await Http.GetAsync("https://spansh.co.uk/api/system/" + id64, ct).ConfigureAwait(false);
                return resp.StatusCode != HttpStatusCode.NotFound;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute SystemKnownAsync " + ex);
                return true;
            }
        }

        private static void SetSpanshError(string message)
        {
            spanshError = message;
            spanshErrorAt = DateTime.UtcNow;
        }

        private static void CancelSpanshPlot()
        {
            try { spanshCts?.Cancel(); }
            catch (Exception ex) { Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute CancelSpanshPlot " + ex); }
            isPlotting = false;
            spanshError = string.Empty;
        }

        // Converts the exact-plotter jumps array into internal waypoints + persists to JSON.
        // `distance` is the per-hop distance; cumulative is summed exactly like the CSV path. The exact
        // plotter models fuel, so must_refuel/is_scoopable/fuel levels come straight from the result.
        private static void ApplySpanshResult(List<ExactJump> jumps)
        {
            lock (SyncRoot)
            {
                Waypoints.Clear();

                var cumulative = 0.0;
                foreach (var j in jumps)
                {
                    cumulative += j.Distance;
                    Waypoints.Add(new NeutronPlotWaypoint
                    {
                        SystemName = j.Name ?? string.Empty,
                        JumpDistance = j.Distance,
                        DistanceRemaining = j.DistanceToDestination,
                        IsNeutron = j.HasNeutron,
                        IsRefuel = j.MustRefuel,
                        IsScoopable = j.IsScoopable,
                        MustInject = j.MustInject > 0,
                        FuelInTank = j.FuelInTank,
                        FuelUsed = j.FuelUsed,
                        CumulativeDistance = cumulative,
                        Id64 = j.Id64
                    });
                }

                state.IsSpanshRoute = true;
                state.CsvPath = string.Empty;
                state.CsvLastWriteTimeUtc = default;
                state.CsvFileSizeBytes = 0;
                state.WaypointTarget = GetInitialWaypoint();

                // Anchor route position to where the player actually is right now.
                if (!string.IsNullOrWhiteSpace(EliteData.StarSystem))
                    state.SystemCurrent = EliteData.StarSystem;

                SaveWaypoints();
                SaveState();
            }
        }

        // Loads the serialized waypoint table (both route types) into Waypoints, preserving any baked-in
        // EDSM enrichment. Pure: returns true on a non-empty load, false otherwise; leaves route-type
        // flags to the caller. Clears Waypoints first so a failed load yields an empty route.
        private static bool LoadWaypointsFromDisk()
        {
            Waypoints.Clear();
            try
            {
                var path = GetWaypointsFilePath();
                if (!File.Exists(path)) return false;

                var wps = JsonConvert.DeserializeObject<List<NeutronPlotWaypoint>>(File.ReadAllText(path));
                if (wps == null || wps.Count == 0) return false;

                Waypoints.AddRange(wps);
                state.WaypointTarget = ClampIndex(state.WaypointTarget);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute LoadWaypointsFromDisk " + ex);
                Waypoints.Clear();
                return false;
            }
        }

        private static void SaveWaypoints()
        {
            try
            {
                var path = GetWaypointsFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(Waypoints, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute SaveWaypoints " + ex);
            }
        }

        private static void DeleteWaypointsFile()
        {
            try
            {
                var path = GetWaypointsFilePath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute DeleteWaypointsFile " + ex);
            }
        }

        private static string GetWaypointsFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "EliteDangerousStreamDeck", WaypointsFileName);
        }

        private class SpanshSubmitResponse
        {
            [JsonProperty("job")] public string Job { get; set; }
            [JsonProperty("status")] public string Status { get; set; }
            [JsonProperty("error")] public string Error { get; set; }
        }

        private class SpanshResultResponse
        {
            [JsonProperty("result")] public SpanshResult Result { get; set; }
        }

        private class SpanshResult
        {
            [JsonProperty("jumps")] public List<ExactJump> Jumps { get; set; }
        }

        // Exact-plotter (/api/generic/route) per-jump record — fuel-aware.
        private class ExactJump
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("distance")] public double Distance { get; set; }
            [JsonProperty("distance_to_destination")] public double DistanceToDestination { get; set; }
            [JsonProperty("fuel_in_tank")] public double FuelInTank { get; set; }
            [JsonProperty("fuel_used")] public double FuelUsed { get; set; }
            [JsonProperty("has_neutron")] public bool HasNeutron { get; set; }
            [JsonProperty("is_scoopable")] public bool IsScoopable { get; set; }
            [JsonProperty("must_refuel")] public bool MustRefuel { get; set; }
            [JsonProperty("must_inject")] public int MustInject { get; set; }
            [JsonProperty("id64")] public long Id64 { get; set; }
        }

        // ---- Phase 2: EDSM fuel-star enrichment ------------------------------

        // Fires a one-time background EDSM lookup for a system's fuel star if any waypoint of that
        // name still lacks it. Must be called under SyncRoot. Cheap no-op once resolved.
        private static void EnsureEnriched(string systemName)
        {
            if (string.IsNullOrWhiteSpace(systemName)) return;
            if (WaypointFuelKnown(systemName)) return;
            if (!enrichInFlight.Add(systemName)) return;
            _ = Task.Run(() => EnrichFromEdsmAsync(systemName));
        }

        // True when the named system's fuel-star distance is already resolved on every matching
        // waypoint (or the system isn't in the route, so there is nothing to fetch).
        private static bool WaypointFuelKnown(string systemName)
        {
            foreach (var wp in Waypoints)
            {
                if (string.Equals(wp.SystemName, systemName, StringComparison.OrdinalIgnoreCase)
                    && wp.FuelStarLs == null)
                    return false;
            }
            return true;
        }

        private static async Task EnrichFromEdsmAsync(string systemName)
        {
            try
            {
                var url = "https://www.edsm.net/api-system-v1/bodies?systemName=" + Uri.EscapeDataString(systemName);
                var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return; // leave uncached so a later trigger retries

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var parsed = JsonConvert.DeserializeObject<EdsmBodiesResponse>(body);

                // Nearest scoopable star by arrival distance; -1 when the system has none (or is unknown).
                var nearest = -1.0;
                if (parsed?.Bodies != null)
                {
                    foreach (var b in parsed.Bodies)
                    {
                        if (!string.Equals(b.Type, "Star", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!IsScoopable(b.SubType)) continue;
                        if (nearest < 0 || b.DistanceToArrival < nearest)
                            nearest = b.DistanceToArrival;
                    }
                }

                lock (SyncRoot)
                {
                    var changed = false;
                    foreach (var wp in Waypoints)
                    {
                        if (string.Equals(wp.SystemName, systemName, StringComparison.OrdinalIgnoreCase))
                        {
                            wp.FuelStarLs = nearest;
                            changed = true;
                        }
                    }
                    if (changed) SaveWaypoints();
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, "NeutronPlotRoute EnrichFromEdsmAsync " + ex);
            }
            finally
            {
                lock (SyncRoot) { enrichInFlight.Remove(systemName); }
            }
        }

        // Scoopable = main-sequence KGBFOAM. EDSM subType examples: "M (Red dwarf) Star",
        // "K (Yellow-Orange) Star", "A (Blue-White) Star". Non-scoopable names start with a word
        // ("Neutron Star", "White Dwarf", "Black Hole", "T Tauri Star") so the class letter must be
        // followed by a space or '(' to avoid false matches like "Black".
        private static bool IsScoopable(string subType)
        {
            if (string.IsNullOrEmpty(subType)) return false;
            var c = char.ToUpperInvariant(subType[0]);
            if ("OBAFGKM".IndexOf(c) < 0) return false;
            return subType.Length == 1 || subType[1] == ' ' || subType[1] == '(';
        }

        private class EdsmBodiesResponse
        {
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("id64")] public long Id64 { get; set; }
            [JsonProperty("bodies")] public List<EdsmBody> Bodies { get; set; }
        }

        private class EdsmBody
        {
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("subType")] public string SubType { get; set; }
            [JsonProperty("distanceToArrival")] public double DistanceToArrival { get; set; }
        }

        private static void LoadState()
        {
            try
            {
                var path = GetStateFilePath();
                if (!File.Exists(path))
                {
                    state = new PersistedState();
                    return;
                }

                state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(path)) ?? new PersistedState();
            }
            catch (Exception ex)
            {
                state = new PersistedState();
                Logger.Instance.LogMessage(TracingLevel.FATAL, "NeutronPlotRoute LoadState " + ex);
            }
        }

        private static void SaveState()
        {
            try
            {
                var path = GetStateFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.FATAL, "NeutronPlotRoute SaveState " + ex);
            }
        }

        private static string GetStateFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "EliteDangerousStreamDeck", StateFileName);
        }
    }
}
