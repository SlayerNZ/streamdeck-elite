using BarRaider.SdTools;
using EliteJournalReader;
using EliteJournalReader.Events;
using System;
using System.Collections.Generic;
using System.Linq;


namespace Elite
{
    public class EliteData
    {

        public static bool UnderAttack = false;
        public static DateTime LastUnderAttackEvent = DateTime.Now;
        public static bool IsFsdBoosted = false;
        public static double LastJumpDistance = 0.0;
        public static double BaseJumpRange = 0.0;
        public static double BoostValue = 1.0;
        // Neutron jet-cone supercharge factor for THIS ship (Spansh exact-plotter supercharge_multiplier).
        // Almost all ships boost 4x; the Caspian Explorer's SCO Mk II drive boosts 6x. Seeded from a
        // ship-type default on ship change, then refined to the exact game-reported value by JetConeBoost.
        public static double NeutronBoostMultiplier = 4.0;
        public static double UnladenMass = 0.0;
        public static double FSDOptimalMass = 0.0;
        public static double FSDMaxFuelPerJump = 0.0;
        public static double FSDLinearConstant = 0.0;
        public static double FSDPowerConstant = 2.45;
        public static double GuardianFSDBonus = 0.0;
        public static double FuelCapacityMain = 0.0;     // main fuel tank size (Loadout) — exact-plotter tank_size
        public static double FuelCapacityReserve = 0.0;  // reservoir size (Loadout) — exact-plotter internal_tank_size
        public static string ShipType { get; set; } = string.Empty;  // internal ship name from Loadout (e.g. "explorer_nx")
        public static string FsdTargetName { get; set; }
        public static long FsdTargetAddress { get; set; }   // id64 of FSD-targeted system (FSDTarget event)
        public static long StarSystemAddress { get; set; }  // id64 of current system (Location/FSDJump/etc.)
        public static int RemainingJumpsInRoute { get; set; }
        public static string StarClass { get; set; }
        public static string StarSystem { get; set; }

        public static List<RouteItem> RouteList = new List<RouteItem>();


        public static int LimpetCount { get; set; }

        // Cache of planet data per body name, populated from Scan journal events
        public static Dictionary<string, (double SurfaceGravity, double PlanetRadius, string Atmosphere, double SurfaceTemperature, string PlanetClass, string TerraformState, bool Landable)> GravityCache
            = new Dictionary<string, (double, double, string, double, string, string, bool)>(StringComparer.OrdinalIgnoreCase);

        // Cache of bio/geo signal counts per body name, populated from FSSBodySignals and SAASignalsFound
        public static Dictionary<string, (int BiologyCount, int GeologyCount)> SignalCache
            = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        // ── Exobiology scan state ─────────────────────────────────────────────────
        // Shared across all ExoBiology button instances and written by Program.BackfillExoBiologyState
        // on startup so the button is populated immediately without waiting for the next scan event.
        //
        // ScanCount:  0 = no active scan
        //             1 = after first Log
        //             2 = after Sample
        //             3 = Analyse complete (sequence done, awaiting reset)

        public static string ExoBioGenus              = null;
        public static string ExoBioSpecies            = null;
        public static int    ExoBioScanCount          = 0;

        // Position of scan 1 (Log) — retained through the whole sequence so the button
        // can measure distance from both scan points simultaneously and use the closest.
        public static double ExoBioLogLat             = double.NaN;
        public static double ExoBioLogLon             = double.NaN;

        // Position of scan 2 (Sample) — only populated after the Sample event fires.
        public static double ExoBioSampleLat          = double.NaN;
        public static double ExoBioSampleLon          = double.NaN;

        // Planet radius — shared, same body for both scan points.
        public static double ExoBioSamplePlanetRadius = 0;

        // Body name at the time of the last sample — used to resolve planet radius from GravityCache
        public static string ExoBioSampleBodyName     = null;

        public static void ResetExoBioState()
        {
            ExoBioGenus              = null;
            ExoBioSpecies            = null;
            ExoBioScanCount          = 0;
            ExoBioLogLat             = double.NaN;
            ExoBioLogLon             = double.NaN;
            ExoBioSampleLat          = double.NaN;
            ExoBioSampleLon          = double.NaN;
            ExoBioSamplePlanetRadius = 0;
            ExoBioSampleBodyName     = null;
        }

        public class Status
        {
            public bool Docked { get; set; }
            public bool Landed { get; set; }
            public bool LandingGearDown { get; set; }
            public bool ShieldsUp { get; set; }
            public bool Supercruise { get; set; }
            public bool FlightAssistOff { get; set; }
            public bool HardpointsDeployed { get; set; }
            public bool InWing { get; set; }
            public bool LightsOn { get; set; }
            public bool CargoScoopDeployed { get; set; }
            public bool SilentRunning { get; set; }
            public bool ScoopingFuel { get; set; }
            public bool SrvHandbrake { get; set; }
            public bool SrvTurret { get; set; }
            public bool SrvUnderShip { get; set; }
            public bool SrvDriveAssist { get; set; }
            public bool FsdMassLocked { get; set; }
            public bool FsdCharging { get; set; }
            public bool FsdCooldown { get; set; }
            public bool LowFuel { get; set; }
            public bool Overheating { get; set; }
            public bool HasLatLong { get; set; }
            public bool IsInDanger { get; set; }
            public bool BeingInterdicted { get; set; }
            public bool InMainShip { get; set; }
            public bool InFighter { get; set; }
            public bool InSRV { get; set; }
            public bool HudInAnalysisMode { get; set; }
            public bool NightVision { get; set; }
            public bool AltitudeFromAverageRadius { get; set; }
            public bool FsdJump { get; set; }
            public bool SrvHighBeam { get; set; }

            public StatusFuel Fuel { get; set; } = new StatusFuel();

            public double FuelCapacity { get; set; }

            public double Cargo { get; set; }
            public string LegalState { get; set; }
            public double JumpRange { get; set; }

            public int Firegroup { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double Altitude { get; set; }
            public double Heading { get; set; }
            public string BodyName { get; set; }
            public double PlanetRadius { get; set; }
            public StatusGuiFocus GuiFocus { get; set; }

            public int[] Pips { get; set; } = new int[3];

            public bool OnFoot { get; set; }
            public bool InTaxi { get; set; }
            public bool InMulticrew { get; set; }
            public bool OnFootInStation { get; set; }
            public bool OnFootOnPlanet { get; set; }
            public bool AimDownSight { get; set; }
            public bool LowOxygen { get; set; }
            public bool LowHealth { get; set; }
            public bool Cold { get; set; }
            public bool Hot { get; set; }
            public bool VeryCold { get; set; }
            public bool VeryHot { get; set; }
            public bool GlideMode { get; set; }
            public bool OnFootInHangar { get; set; }
            public bool OnFootSocialSpace { get; set; }
            public bool OnFootExterior { get; set; }
            public bool BreathableAtmosphere { get; set; }
            public bool TelepresenceMulticrew { get; set; }
            public bool PhysicalMulticrew { get; set; }
            public bool Fsdhyperdrivecharging { get; set; }
            public string SelectedWeapon { get; set; }
            public double Temperature { get; set; }
            public string DestinationName { get; set; }
        }

        public static Status StatusData = new Status();

        public static void HandleNavRouteEvents(object sender, NavRouteEvent.NavRouteEventArgs info)
        {
            if (info?.Route == null || info.Route.Length < 2)
            {
                RouteList = new List<RouteItem>();
            }
            else
            {
                RouteList = info.Route.Select(
                    x => new RouteItem
                    {
                        StarClass = x.StarClass,
                        StarPos = x.StarPos,
                        StarSystem = x.StarSystem,
                        SystemAddress = x.SystemAddress,
                    }).Skip(1).ToList();

            }
        }

        public static void HandleCargoEvents(object sender, CargoEvent.CargoEventArgs evt)
        {
            if (evt?.Inventory != null && evt?.Vessel == "Ship")
            {
                EliteData.LimpetCount =
                  evt.Inventory.Where(x => x.Name.ToLower().Contains("drones")).Sum(x => x.Count);
            }
        }

        public static void HandleStatusEvents(object sender, StatusFileEvent evt)
        {
            StatusData.ShieldsUp = (evt.Flags & StatusFlags.ShieldsUp) != 0;
            StatusData.FlightAssistOff = (evt.Flags & StatusFlags.FlightAssistOff) != 0;
            StatusData.InWing = (evt.Flags & StatusFlags.InWing) != 0;
            StatusData.LightsOn = (evt.Flags & StatusFlags.LightsOn) != 0;
            StatusData.NightVision = (evt.Flags & StatusFlags.NightVision) != 0;
            StatusData.AltitudeFromAverageRadius = (evt.Flags & StatusFlags.AltitudeFromAverageRadius) != 0;
            StatusData.LowFuel = (evt.Flags & StatusFlags.LowFuel) != 0;
            StatusData.Overheating = (evt.Flags & StatusFlags.Overheating) != 0;
            StatusData.HasLatLong = (evt.Flags & StatusFlags.HasLatLong) != 0;
            StatusData.InMainShip = (evt.Flags & StatusFlags.InMainShip) != 0;
            StatusData.InFighter = (evt.Flags & StatusFlags.InFighter) != 0;
            StatusData.InSRV = (evt.Flags & StatusFlags.InSRV) != 0;
            StatusData.SrvDriveAssist = (evt.Flags & StatusFlags.SrvDriveAssist) != 0 && StatusData.InSRV;
            StatusData.SrvUnderShip = (evt.Flags & StatusFlags.SrvUnderShip) != 0 && StatusData.InSRV;
            StatusData.SrvTurret = (evt.Flags & StatusFlags.SrvTurret) != 0 && StatusData.InSRV;
            StatusData.SrvHandbrake = (evt.Flags & StatusFlags.SrvHandbrake) != 0 && StatusData.InSRV;
            StatusData.SrvHighBeam = (evt.Flags & StatusFlags.SrvHighBeam) != 0 && StatusData.InSRV;

            StatusData.Docked = (evt.Flags & StatusFlags.Docked) != 0;
            StatusData.Landed = (evt.Flags & StatusFlags.Landed) != 0;
            StatusData.LandingGearDown = (evt.Flags & StatusFlags.LandingGearDown) != 0;
            StatusData.CargoScoopDeployed = (evt.Flags & StatusFlags.CargoScoopDeployed) != 0;
            StatusData.SilentRunning = (evt.Flags & StatusFlags.SilentRunning) != 0;
            StatusData.ScoopingFuel = (evt.Flags & StatusFlags.ScoopingFuel) != 0;
            StatusData.IsInDanger = (evt.Flags & StatusFlags.IsInDanger) != 0;
            StatusData.BeingInterdicted = (evt.Flags & StatusFlags.BeingInterdicted) != 0;
            StatusData.HudInAnalysisMode = (evt.Flags & StatusFlags.HudInAnalysisMode) != 0;

            StatusData.FsdMassLocked = (evt.Flags & StatusFlags.FsdMassLocked) != 0;
            StatusData.FsdCharging = (evt.Flags & StatusFlags.FsdCharging) != 0;
            StatusData.FsdCooldown = (evt.Flags & StatusFlags.FsdCooldown) != 0;

            StatusData.Supercruise = (evt.Flags & StatusFlags.Supercruise) != 0;
            StatusData.FsdJump = (evt.Flags & StatusFlags.FsdJump) != 0;
            StatusData.HardpointsDeployed = (evt.Flags & StatusFlags.HardpointsDeployed) != 0 && !StatusData.Supercruise && !StatusData.FsdJump;

            StatusData.Fuel = evt.Fuel ?? new StatusFuel();

            StatusData.Cargo = evt.Cargo;
            StatusData.JumpRange = evt.MaxJumpRange;

            StatusData.LegalState = evt.LegalState;

            StatusData.Firegroup = evt.Firegroup;
            StatusData.GuiFocus = evt.GuiFocus;

            StatusData.Latitude = evt.Latitude;
            StatusData.Longitude = evt.Longitude;
            StatusData.Altitude = evt.Altitude;
            StatusData.Heading = evt.Heading;
            StatusData.BodyName = evt.BodyName;
            StatusData.PlanetRadius = evt.PlanetRadius;

            StatusData.Pips[0] = evt.Pips.System;
            StatusData.Pips[1] = evt.Pips.Engine;
            StatusData.Pips[2] = evt.Pips.Weapons;

            StatusData.OnFoot = (evt.Flags2 & MoreStatusFlags.OnFoot) != 0;
            StatusData.InTaxi = (evt.Flags2 & MoreStatusFlags.InTaxi) != 0;
            StatusData.InMulticrew = (evt.Flags2 & MoreStatusFlags.InMulticrew) != 0;
            StatusData.OnFootInStation = (evt.Flags2 & MoreStatusFlags.OnFootInStation) != 0;
            StatusData.OnFootOnPlanet = (evt.Flags2 & MoreStatusFlags.OnFootOnPlanet) != 0;
            StatusData.AimDownSight = (evt.Flags2 & MoreStatusFlags.AimDownSight) != 0;
            StatusData.LowOxygen = (evt.Flags2 & MoreStatusFlags.LowOxygen) != 0;
            StatusData.LowHealth = (evt.Flags2 & MoreStatusFlags.LowHealth) != 0;
            StatusData.Cold = (evt.Flags2 & MoreStatusFlags.Cold) != 0;
            StatusData.Hot = (evt.Flags2 & MoreStatusFlags.Hot) != 0;
            StatusData.VeryCold = (evt.Flags2 & MoreStatusFlags.VeryCold) != 0;
            StatusData.VeryHot = (evt.Flags2 & MoreStatusFlags.VeryHot) != 0;

            StatusData.GlideMode = (evt.Flags2 & MoreStatusFlags.GlideMode) != 0;
            StatusData.OnFootInHangar = (evt.Flags2 & MoreStatusFlags.OnFootInHangar) != 0;
            StatusData.OnFootSocialSpace = (evt.Flags2 & MoreStatusFlags.OnFootSocialSpace) != 0;
            StatusData.OnFootExterior = (evt.Flags2 & MoreStatusFlags.OnFootExterior) != 0;
            StatusData.BreathableAtmosphere = (evt.Flags2 & MoreStatusFlags.BreathableAtmosphere) != 0;

            StatusData.TelepresenceMulticrew = (evt.Flags2 & MoreStatusFlags.TelepresenceMulticrew) != 0;
            StatusData.PhysicalMulticrew = (evt.Flags2 & MoreStatusFlags.PhysicalMulticrew) != 0;

            StatusData.Fsdhyperdrivecharging = (evt.Flags2 & MoreStatusFlags.Fsdhyperdrivecharging) != 0;
            StatusData.SelectedWeapon = evt.SelectedWeapon;
            StatusData.Temperature = evt.Temperature;
            StatusData.DestinationName = evt.Destination.Name ?? string.Empty;
        }


        // Known neutron jet-cone boost factor by ship, used as a cold-start default before the game
        // reports the real value via JetConeBoost. Currently only the Caspian Explorer differs (6x).
        private static double DefaultNeutronBoost(string ship)
        {
            return string.Equals(ship, "explorer_nx", StringComparison.OrdinalIgnoreCase) ? 6.0 : 4.0;
        }

        private static void ParseFSDData(LoadoutEvent.LoadoutEventArgs loadout)
        {
            if (loadout.Modules == null) return;

            // Guardian FSD Booster — flat LY bonus, also multiplied by neutron boost
            var guardian = loadout.Modules.FirstOrDefault(m => m.Item != null && m.Item.IndexOf("guardianfsdbooster", StringComparison.OrdinalIgnoreCase) >= 0);
            if (guardian != null)
            {
                var gm = System.Text.RegularExpressions.Regex.Match(guardian.Item, @"size(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                EliteData.GuardianFSDBonus = gm.Success && int.TryParse(gm.Groups[1].Value, out int gs)
                    ? gs switch { 1 => 4.0, 2 => 6.0, 3 => 7.75, 4 => 9.25, 5 => 10.5, _ => 0.0 }
                    : 0.0;
            }
            else
            {
                EliteData.GuardianFSDBonus = 0.0;
            }

            var fsd = loadout.Modules.FirstOrDefault(m => string.Equals(m.Slot, "FrameShiftDrive", StringComparison.OrdinalIgnoreCase));
            if (fsd == null)
            {
                // No FSD in this loadout — clear drive-derived fields so a previously flown ship's
                // numbers can't linger and get sent to Spansh. StartSpanshPlot will then show NO SHIP.
                EliteData.FSDOptimalMass = 0.0;
                EliteData.FSDMaxFuelPerJump = 0.0;
                return;
            }

            // FSDOptimalMass from engineering modifier
            var optMass = fsd.Engineering?.Modifiers?.FirstOrDefault(m => m.Label == ModuleAttribute.FSDOptimalMass);
            EliteData.FSDOptimalMass = optMass?.Value > 0 ? optMass.Value.Value : 0.0;

            // MaxFuelPerJump from engineering modifier (present when engineered)
            var maxFuel = fsd.Engineering?.Modifiers?.FirstOrDefault(m => m.Label == ModuleAttribute.MaxFuelPerJump);
            double explicitMaxFuel = maxFuel?.Value > 0 ? maxFuel.Value.Value : 0.0;

            // FSD size and rating from item name (e.g. int_hyperdrive_overcharge_size5_class5 or int_hyperdrive_size5_classa)
            var item = fsd.Item ?? string.Empty;
            var sizeMatch  = System.Text.RegularExpressions.Regex.Match(item, @"size(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var classNum   = System.Text.RegularExpressions.Regex.Match(item, @"class([1-5])(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var classLetter = System.Text.RegularExpressions.Regex.Match(item, @"class([a-e])(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            int fsdSize = sizeMatch.Success && int.TryParse(sizeMatch.Groups[1].Value, out int sz) ? sz : 5;
            char fsdRating = 'A';
            if (classLetter.Success)
                fsdRating = char.ToUpper(classLetter.Groups[1].Value[0]);
            else if (classNum.Success && int.TryParse(classNum.Groups[1].Value, out int cn))
                fsdRating = (char)('A' + (5 - cn)); // 5→A, 4→B, 3→C, 2→D, 1→E

            EliteData.FSDPowerConstant = 2.0 + (fsdSize - 2) * 0.15;
            EliteData.FSDLinearConstant = fsdRating switch { 'A' => 0.012, 'B' => 0.010, 'C' => 0.008, 'D' => 0.010, 'E' => 0.011, _ => 0.012 };

            // MaxFuelPerJump: use modifier if available, otherwise back-calculate from MaxJumpRange
            if (explicitMaxFuel > 0)
            {
                EliteData.FSDMaxFuelPerJump = explicitMaxFuel;
            }
            else if (EliteData.FSDOptimalMass > 0 && EliteData.BaseJumpRange > 0 && EliteData.UnladenMass > 0)
            {
                var fsdRange = EliteData.BaseJumpRange - EliteData.GuardianFSDBonus;
                EliteData.FSDMaxFuelPerJump = BackCalculateMaxFuelPerJump(fsdRange, EliteData.FSDOptimalMass, EliteData.UnladenMass, EliteData.FSDLinearConstant, EliteData.FSDPowerConstant);
            }
            else
            {
                // Couldn't determine it from this ship — don't retain a prior ship's value.
                EliteData.FSDMaxFuelPerJump = 0.0;
            }
        }

        private static double BackCalculateMaxFuelPerJump(double fsdRange, double optimalMass, double unladenMass, double linearConstant, double powerConstant)
        {
            // Fixed-point iteration: f = linearConstant × (fsdRange × (unladenMass + f) / optimalMass)^powerConstant
            var f = fsdRange * unladenMass / optimalMass; // initial estimate
            for (var i = 0; i < 100; i++)
            {
                var fNew = linearConstant * Math.Pow(fsdRange * (unladenMass + f) / optimalMass, powerConstant);
                if (Math.Abs(fNew - f) < 0.0001) return fNew;
                f = fNew;
            }
            return f;
        }

        public static void HandleEliteEvents(object sender, JournalEventArgs e)
        {
            var evt = ((JournalEventArgs)e).OriginalEvent.Value<string>("event");

            if (string.IsNullOrWhiteSpace(evt))
            {
                return;
            }
            if (evt == "FSSBodySignals" || evt == "SAASignalsFound")
                Logger.Instance.LogMessage(TracingLevel.INFO, $"JournalEvent: {evt}");
            switch (evt)
            {
                case "Location":
                    //When written: at startup, or when being resurrected at a station

                    var locationInfo = (LocationEvent.LocationEventArgs)e;

                    EliteData.StarSystem = locationInfo.StarSystem;
                    EliteData.StarSystemAddress = locationInfo.SystemAddress;
                    var locTs = ((JournalEventArgs)e).OriginalEvent?.Value<DateTime>("timestamp") ?? DateTime.MinValue;
                    if ((DateTime.UtcNow - locTs).TotalHours < 24)
                        NeutronPlotRoute.SetSystemCurrent(locationInfo.StarSystem);
                    break;

                case "ApproachBody":
                    //    When written: when in Supercruise, and distance from planet drops to within the 'Orbital Cruise' zone
                    var approachBodyInfo = (ApproachBodyEvent.ApproachBodyEventArgs)e;

                    EliteData.StarSystem = approachBodyInfo.StarSystem;
                    EliteData.StarSystemAddress = approachBodyInfo.SystemAddress;
                    break;

                case "LeaveBody":
                    //When written: when flying away from a planet, and distance increases above the 'Orbital Cruise' altitude
                    var leaveBodyInfo = (LeaveBodyEvent.LeaveBodyEventArgs)e;

                    EliteData.StarSystem = leaveBodyInfo.StarSystem;
                    EliteData.StarSystemAddress = leaveBodyInfo.SystemAddress;
                    break;

                case "Docked":
                    //    When written: when landing at landing pad in a space station, outpost, or surface settlement
                    var dockedInfo = (DockedEvent.DockedEventArgs)e;

                    EliteData.StarSystem = dockedInfo.StarSystem;
                    EliteData.StarSystemAddress = dockedInfo.SystemAddress;
                    break;

                case "Loadout":
                    var loadoutInfo = (LoadoutEvent.LoadoutEventArgs)e;
                    var newShip = loadoutInfo.Ship ?? string.Empty;
                    if (!string.Equals(newShip, EliteData.ShipType, StringComparison.OrdinalIgnoreCase))
                    {
                        // Ship changed — seed the neutron boost with this ship's known default until the
                        // game reports the real factor via JetConeBoost. Avoids carrying a prior ship's 6x.
                        EliteData.NeutronBoostMultiplier = DefaultNeutronBoost(newShip);
                    }
                    EliteData.ShipType = newShip;
                    if (loadoutInfo.MaxJumpRange > 0)
                        EliteData.BaseJumpRange = loadoutInfo.MaxJumpRange;
                    EliteData.UnladenMass = loadoutInfo.UnladenMass;
                    EliteData.FuelCapacityMain = loadoutInfo.FuelCapacity.Main;
                    EliteData.FuelCapacityReserve = loadoutInfo.FuelCapacity.Reserve;
                    ParseFSDData(loadoutInfo);
                    break;

                case "JetConeBoost":
                    var jetConeInfo = (JetConeBoostEvent.JetConeBoostEventArgs)e;
                    EliteData.BoostValue = jetConeInfo.BoostValue > 0 ? jetConeInfo.BoostValue : 1.0;
                    EliteData.IsFsdBoosted = true;
                    // Authoritative per-ship neutron boost factor, straight from the game (6x Caspian, 4x others).
                    if (jetConeInfo.BoostValue > 0)
                        EliteData.NeutronBoostMultiplier = jetConeInfo.BoostValue;
                    break;

                case "FSDJump":
                    //When written: when jumping from one star system to another
                    var fsdJumpInfo = (FSDJumpEvent.FSDJumpEventArgs)e;

                    EliteData.StarSystem = fsdJumpInfo.StarSystem;
                    EliteData.StarSystemAddress = fsdJumpInfo.SystemAddress;
                    EliteData.LastJumpDistance = fsdJumpInfo.JumpDist;
                    EliteData.IsFsdBoosted = false;
                    EliteData.BoostValue = 1.0;
                    var fsdTs = ((JournalEventArgs)e).OriginalEvent?.Value<DateTime>("timestamp") ?? DateTime.MinValue;
                    if ((DateTime.UtcNow - fsdTs).TotalHours < 1)
                        NeutronPlotRoute.RouteAutoAdvance(fsdJumpInfo.StarSystem);
                    NavRouteService.AutoAdvance(fsdJumpInfo.StarSystem);
                    break;

                case "CarrierJump":

                    var carrierJumpInfo = (CarrierJumpEvent.CarrierJumpEventArgs)e;

                    EliteData.StarSystem = carrierJumpInfo.StarSystem;
                    EliteData.StarSystemAddress = carrierJumpInfo.SystemAddress;
                    break;

                case "SupercruiseExit":
                    //When written: leaving supercruise for normal space
                    var supercruiseExitInfo = (SupercruiseExitEvent.SupercruiseExitEventArgs)e;

                    EliteData.StarSystem = supercruiseExitInfo.StarSystem;
                    EliteData.StarSystemAddress = supercruiseExitInfo.SystemAddress;
                    break;

                case "FSDTarget":
                    //When written: when selecting a star system to jump to
                    var fsdTargetInfo = (FSDTargetEvent.FSDTargetEventArgs)e;

                    EliteData.FsdTargetName = fsdTargetInfo.Name;
                    EliteData.FsdTargetAddress = fsdTargetInfo.SystemAddress;

                    EliteData.RemainingJumpsInRoute = fsdTargetInfo.RemainingJumpsInRoute;

                    EliteData.StarClass = fsdTargetInfo.StarClass;

                    break;

                case "UnderAttack":
                    //    When written: when under fire(same time as the Under Attack voice message)

                    var underAttackInfo = (UnderAttackEvent.UnderAttackEventArgs)e;

                    if (underAttackInfo.Target == "You")
                    {
                        EliteData.UnderAttack = true;
                        EliteData.LastUnderAttackEvent = DateTime.Now;
                    }

                    break;

                case "Cargo":

                    //var cargoInfo = (CargoEvent.CargoEventArgs)e;
                    /*
                    if (cargoInfo.Vessel == "Ship")
                    {
                        if (cargoInfo.Inventory == null)
                        {
                            cargoInfo = Program.JournalWatcher.ReadCargoJson();
                        }

                        if (cargoInfo.Inventory != null)
                        {
                            EliteData.LimpetCount =
                              cargoInfo.Inventory.Where(x => x.Name.ToLower().Contains("drones")).Sum(x => x.Count);
                        }
                    }
                    */
                    break;

                case "Died":

                    //var diedInfo = (DiedEvent.DiedEventArgs)e;

                    EliteData.LimpetCount = 0;

                    break;

                case "NavRouteClear":

                    EliteData.RouteList = new List<RouteItem>();
                    EliteData.RemainingJumpsInRoute = 0;

                    break;

                case "Scan":
                    // Cache planet data for gravity, planet info, and nav target buttons
                    var scanEvent = ((JournalEventArgs)e).OriginalEvent;
                    var bodyName = scanEvent.Value<string>("BodyName");
                    var surfaceGravity = scanEvent.Value<double?>("SurfaceGravity");
                    var planetRadius = scanEvent.Value<double?>("Radius");
                    var atmosphere = scanEvent.Value<string>("Atmosphere") ?? scanEvent.Value<string>("AtmosphereType") ?? "";
                    var surfaceTemperature = scanEvent.Value<double?>("SurfaceTemperature") ?? 0;
                    var planetClass = scanEvent.Value<string>("PlanetClass") ?? "";
                    var terraformState = scanEvent.Value<string>("TerraformState") ?? "";
                    var landable = scanEvent.Value<bool?>("Landable") ?? false;

                    if (!string.IsNullOrEmpty(bodyName) && surfaceGravity.HasValue && planetRadius.HasValue && surfaceGravity.Value > 0)
                    {
                        // SurfaceGravity in journal is in m/s², divide by 9.81 to get g
                        EliteData.GravityCache[bodyName] = (surfaceGravity.Value / 9.81, planetRadius.Value, atmosphere, surfaceTemperature, planetClass, terraformState, landable);
                    }
                    break;

                case "FSSBodySignals":
                    {
                        var fssScan = ((JournalEventArgs)e).OriginalEvent;
                        var fssBody = fssScan.Value<string>("BodyName");
                        var signals = fssScan["Signals"];
                        if (!string.IsNullOrEmpty(fssBody) && signals != null)
                        {
                            EliteData.SignalCache.TryGetValue(fssBody, out var existing);
                            int bio = existing.BiologyCount, geo = existing.GeologyCount;
                            foreach (var sig in signals)
                            {
                                var sigType = sig.Value<string>("Type") ?? "";
                                var sigCount = sig.Value<int>("Count");
                                if (sigType.Contains("Biological")) bio = sigCount;
                                else if (sigType.Contains("Geological")) geo = sigCount;
                            }
                            EliteData.SignalCache[fssBody] = (bio, geo);
                        }
                        break;
                    }

                case "SAASignalsFound":
                    {
                        var saaScan = ((JournalEventArgs)e).OriginalEvent;
                        var saaBody = saaScan.Value<string>("BodyName");
                        var saaSignals = saaScan["Signals"];
                        if (!string.IsNullOrEmpty(saaBody) && saaSignals != null)
                        {
                            EliteData.SignalCache.TryGetValue(saaBody, out var existing);
                            int bio = existing.BiologyCount, geo = existing.GeologyCount;
                            foreach (var sig in saaSignals)
                            {
                                var sigType = sig.Value<string>("Type") ?? "";
                                var sigCount = sig.Value<int>("Count");
                                if (sigType.Contains("Biological")) bio = sigCount;
                                else if (sigType.Contains("Geological")) geo = sigCount;
                            }
                            EliteData.SignalCache[saaBody] = (bio, geo);
                        }
                        break;
                    }

            }
        }

    }
}
