using HarmonyLib;
using PulsarModLoader.SaveData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CapBot
{
    // Alpha 1.2.0 bot autonomy: learning, talents, research, economy, upgrades,
    // component install/swap, campaign auto-detection, mission auto-detection,
    // extraction, inventory management, stuck watchdog, smart item use.
    // All mutation happens server-side (host) using the game's own entry points so
    // other mods (Talents, BetterAI, ExpandedGalaxy, Exotic Components, ...) keep working.
    internal static class Autonomy
    {
        internal static float LastTick = 0f;
        internal static float LastSlowTick = 0f;
        internal static float LastExtract = 0f;

        // Called from the UpdateAIPriorities postfix for every crew bot.
        internal static void OnTick(PLPlayer bot)
        {
            if (bot == null || !PhotonNetwork.isMasterClient || PLServer.Instance == null) return;
            if (bot.GetPawn() == null || !bot.IsBot || bot.TeamID != 0) return;
            if (bot.StartingShip == null) return;
            if (Time.unscaledTime - LastTick < 0.5f) return;
            LastTick = Time.unscaledTime;

            bool capbotActive = SpawnBot.capisbot && Config.CaptainBotEnabled;

            // Universal (all crew bots, every class): smart item use, stuck watchdog,
            // auto talent ranking. Previously these only ran for the class-0 bot.
            if (Config.SmartAIEnabled) SmartItemUse(bot);
            StuckWatchdog.Tick(bot);
            if (Config.CaptainBotEnabled) BotTalents.Tick(bot);

            // Ship-wide systems must run once per cycle, not once per bot. Run them
            // from the captain bot; if no captain bot is spawned, the lowest-player-ID
            // bot acts as the executor so mission auto-detection still works.
            bool isExecutor = bot.GetClassID() == 0 || IsLowestBotID(bot);
            if (!isExecutor) return;

            if (capbotActive) BotInventory.Tick(bot);
            if (Time.unscaledTime - LastSlowTick > 4f)
            {
                LastSlowTick = Time.unscaledTime;
                // Crew research is ship-wide (not per-bot): run it whenever the
                // talent system is enabled, even without the captain bot spawned.
                if (Config.CaptainBotEnabled) BotResearch.Tick();
                if (Config.MissionAutoDetectEnabled)
                {
                    BotEconomy.Tick();
                    BotCampaign.Tick();
                    BotMissions.TickDialogueWork(bot);
                    BotMissions.TickObjectiveWork(bot);
                }
                BotInstall.Tick();
                BotExtractor.Tick();
                BotUpgrades.Tick();
                Learning.PollMissions();
            }
        }

        // True for the team-0 bot with the smallest player ID (stable executor
        // pick so exactly one bot drives ship-wide systems each tick).
        private static bool IsLowestBotID(PLPlayer bot)
        {
            try
            {
                int myID = bot.GetPlayerID();
                foreach (PLPlayer p in PLServer.Instance.AllPlayers)
                {
                    if (p == null || !p.IsBot || p.TeamID != 0 || p.GetPawn() == null) continue;
                    if (p.GetPlayerID() < myID) return false;
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool SafeHasTalent(PLPlayer player, int id, int minLevel = 1)
        {
            if (player == null || player.Talents == null || id < 0 || id >= player.Talents.Length) return false;
            return (int)player.Talents[id] >= minLevel;
        }

        // ---- Smart item use (gap-filler; the game's own AI handles most cases) --
        private static void SmartItemUse(PLPlayer bot)
        {
            try
            {
                PLPawn pawn = bot.GetPawn();
                PLBot ai = bot.MyBot;
                if (pawn == null || ai == null || ai.MyBotController == null || bot.StartingShip == null) return;

                PLBotController ctrl = ai.MyBotController;
                if (ctrl.AI_ShouldUseActiveItem) return; // game AI already driving item use this tick

                float health = (float)pawn.MaxHealth > 0f ? (float)pawn.Health / (float)pawn.MaxHealth : 1f;

                // Fire aboard our ship -> extinguish (game's TickExtinguishFires targets these)
                bool shipOnFire = false;
                foreach (PLFire fire in UnityEngine.Object.FindObjectsOfType<PLFire>())
                {
                    if (fire != null && fire.MyShip == bot.StartingShip && fire.GetFlamesActive())
                    {
                        shipOnFire = true;
                        break;
                    }
                }
                if (shipOnFire && !bot.OnPlanet)
                {
                    ctrl.AI_ItemUtilityRequest = EItemUtilityType.E_ANTIFIRE;
                    ctrl.AI_ShouldUseActiveItem = true;
                }
                else if (health < 0.45f)
                {
                    ctrl.AI_ItemUtilityRequest = EItemUtilityType.E_HEALING;
                    ctrl.AI_ShouldUseActiveItem = true;
                }
                else if (bot.GetClassID() == 4 && bot.StartingShip.MyStats != null
                    && bot.StartingShip.MyStats.HullCurrent / Mathf.Max(1f, bot.StartingShip.MyStats.HullMax) < 0.55f)
                {
                    ctrl.AI_ItemUtilityRequest = EItemUtilityType.E_REPAIR;
                    ctrl.AI_ShouldUseActiveItem = true;
                }
            }
            catch { }
        }
    }

    // ---- Adaptive learning ------------------------------------------------
    internal static class Learning
    {
        private const int CLASSES = 5;
        internal static float[,] XP = new float[CLASSES, 6]; // per class: Missions, Upgrades, Purchases, Sales, Jumps, Research
        internal static int[,] Counters = new int[CLASSES, 6];

        internal static float Level(int cls, int idx) => Mathf.Sqrt(Mathf.Max(0f, XP[cls, idx])) / 10f;
        internal static float CombatSkill(int cls) => Mathf.Clamp(1f + Level(cls, 0) * 0.25f, 0.5f, 1.75f);
        internal static float EconomySkill(int cls) => Mathf.Clamp(1f + (Level(cls, 2) + Level(cls, 3)) * 0.2f, 0.5f, 1.75f);
        internal static float CraftSkill(int cls) => Mathf.Clamp(1f + Level(cls, 1) * 0.25f, 0.5f, 1.75f);
        internal static float NavSkill(int cls) => Mathf.Clamp(1f + Level(cls, 4) * 0.15f, 0.5f, 1.5f);

        internal static void Add(int cls, int idx, float amount)
        {
            if (cls < 0 || cls >= CLASSES) return;
            XP[cls, idx] += amount;
            Counters[cls, idx]++;
        }

        internal static string Describe(PLPlayer bot)
        {
            int cls = Mathf.Clamp(bot.GetClassID(), 0, CLASSES - 1);
            int lvl = Mathf.FloorToInt(Level(cls, 0) + Level(cls, 1) + Level(cls, 2) + Level(cls, 3) + Level(cls, 4) + Level(cls, 5));
            return bot.GetPlayerName(false) + " (Experience " + lvl + ")";
        }

        // Track mission end transitions (complete vs abandoned/fail). XP goes to
        // all crew classes — missions are a crew effort, not just the captain's.
        private static Dictionary<int, bool> MissionEnded = new Dictionary<int, bool>();
        internal static void PollMissions()
        {
            try
            {
                if (PLServer.Instance == null) return;
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null) continue;
                    bool ended = m.Ended;
                    bool known = MissionEnded.TryGetValue(m.MissionTypeID, out bool wasEnded);
                    if (!known)
                    {
                        MissionEnded[m.MissionTypeID] = ended;
                        continue;
                    }
                    if (!wasEnded && ended)
                    {
                        for (int c = 0; c < CLASSES; c++)
                        {
                            if (m.Abandoned) Add(c, 0, -1f);
                            else Add(c, 0, 2f * CombatSkill(c));
                        }
                        MissionEnded[m.MissionTypeID] = true;
                    }
                    else if (wasEnded && !ended)
                    {
                        MissionEnded[m.MissionTypeID] = false;
                    }
                }
            }
            catch { }
        }

        internal static void RecordJump()
        {
            try { Add(0, 4, 1f); } catch { }
        }
    }

    internal class CapBotLearningSave : PMLSaveData
    {
        public override uint VersionID => 1u;
        public override string Identifier() => "CapBotLearning";

        public override byte[] SaveData()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                for (int c = 0; c < 5; c++)
                    for (int i = 0; i < 6; i++)
                        w.Write(Learning.XP[c, i]);
                return ms.ToArray();
            }
        }

        public override void LoadData(byte[] Data, uint VersionID)
        {
            if (Data == null || Data.Length < 5 * 6 * 4) return;
            using (MemoryStream ms = new MemoryStream(Data))
            using (BinaryReader r = new BinaryReader(ms))
            {
                for (int c = 0; c < 5; c++)
                    for (int i = 0; i < 6; i++)
                        Learning.XP[c, i] = r.ReadSingle();
            }
        }
    }

    // ---- Talent auto-spend -------------------------------------------------
    internal static class BotTalents
    {
        private static float LastSpend = 0f;

        private static readonly ETalents[] CaptainOrder = {
            ETalents.CAP_SHOP_DISCOUNTS, ETalents.COMPONENT_UPGRADER_OPERATOR, ETalents.ITEM_UPGRADER_OPERATOR,
            ETalents.CAP_DIPLOMACY, ETalents.CAP_SCAVENGER, ETalents.CAP_CREW_SPEED_BOOST,
            ETalents.CAP_SCREEN_DEFENSE, ETalents.CAP_SCREEN_SAFETY, ETalents.CAP_ARMOR_BOOST,
            ETalents.CAP_INTIMIDATION, ETalents.HEALTH_BOOST, ETalents.HEALTH_BOOST_2,
            ETalents.ARMOR_BOOST, ETalents.ARMOR_BOOST_2, ETalents.ADVANCED_OPERATOR
        };
        private static readonly ETalents[] PilotOrder = {
            ETalents.PIL_SHIP_SPEED, ETalents.PIL_SHIP_TURNING, ETalents.PIL_KEEN_EYES,
            ETalents.PIL_REDUCE_SYS_DAMAGE, ETalents.PIL_REDUCE_HULL_DAMAGE, ETalents.SENSOR_DISH_CERT,
            ETalents.INC_STAMINA, ETalents.INC_JETPACK, ETalents.INC_MAX_WEIGHT,
            ETalents.INC_ALLOW_ENCUMBERED_SPRINT, ETalents.INC_TURRET_ZOOM, ETalents.HEALTH_BOOST, ETalents.HEALTH_BOOST_2,
            ETalents.ARMOR_BOOST, ETalents.ARMOR_BOOST_2
        };
        private static readonly ETalents[] SciOrder = {
            ETalents.SCI_SENSOR_BOOST, ETalents.SCI_SCANNER_RESEARCH_MAT, ETalents.SCI_SCANNER_PICKUPS,
            ETalents.SCI_RESEARCH_SPECIALTY, ETalents.SCI_HEAL_NEARBY, ETalents.SCI_FREQ_AMPLIFIER,
            ETalents.SCI_PROBE_XP, ETalents.SCI_PROBE_COOLDOWN, ETalents.SCI_SENSOR_HIDE,
            ETalents.INC_HEALING_RATE, ETalents.INC_ENEMY_ATRIUM_HEAL, ETalents.HEALTH_BOOST, ETalents.HEALTH_BOOST_2,
            ETalents.ARMOR_BOOST, ETalents.ARMOR_BOOST_2
        };
        private static readonly ETalents[] WeapOrder = {
            ETalents.WPNS_TURRET_BOOST, ETalents.WPNS_COOLING, ETalents.WPNS_MISSILE_EXPERT,
            ETalents.WPNS_RANGE_BOOST, ETalents.WPNS_BOOST_CREW_TURRET_CHARGE, ETalents.WPNS_BOOST_CREW_TURRET_DAMAGE,
            ETalents.WPN_AMMO_BOOST, ETalents.WPN_SCREEN_HACKER,
            ETalents.WPNS_RELOAD_SPEED, ETalents.WPNS_RELOAD_SPEED_2, ETalents.E_TURRET_COOLING_CREW_WEAPONS,
            ETalents.HEALTH_BOOST, ETalents.HEALTH_BOOST_2, ETalents.ARMOR_BOOST, ETalents.ARMOR_BOOST_2
        };
        private static readonly ETalents[] EngOrder = {
            ETalents.ENG_REPAIR_DRONES, ETalents.ENG_FIRE_REDUCTION, ETalents.ENG_COOLANT_MIX_CUSTOM,
            ETalents.ENG_WARP_CHARGE_BOOST, ETalents.ENG_SALVAGE, ETalents.ENG_COREPOWERBOOST,
            ETalents.ENG_CORECOOLINGBOOST, ETalents.ENG_AUX_POWER_BOOST, ETalents.E_TURRET_COOLING_CREW_ENGINEER,
            ETalents.COMPONENT_UPGRADER_OPERATOR, ETalents.ITEM_UPGRADER_OPERATOR,
            ETalents.HEALTH_BOOST, ETalents.HEALTH_BOOST_2, ETalents.ARMOR_BOOST, ETalents.ARMOR_BOOST_2
        };

        internal static void Tick(PLPlayer bot)
        {
            try
            {
                if (bot.Talents == null) return;
                if (Time.unscaledTime - LastSpend < 0.6f) return;
                if ((int)bot.TalentPointsAvailable <= 0) return;

                int cls = Mathf.Clamp(bot.GetClassID(), 0, 4);
                ETalents[] order = cls == 0 ? CaptainOrder : cls == 1 ? PilotOrder : cls == 2 ? SciOrder : cls == 3 ? WeapOrder : EngOrder;
                List<ETalents> classTalents = PLGlobal.TalentsForClass(cls);

                foreach (ETalents t in order)
                {
                    TalentInfo info = PLGlobal.GetTalentInfoForTalentType(t);
                    if (info == null) continue;
                    int id = info.TalentID;
                    if (id < 0 || id >= bot.Talents.Length) continue;
                    if ((int)bot.Talents[id] >= info.MaxRank) continue;
                    if (classTalents != null && !classTalents.Contains((ETalents)id)) continue;
                    // Research-gated talents (special ones) need to be unlocked first.
                    if (info.WarpsToResearch > 0 && !PLServer.Instance.IsTalentUnlocked((ETalents)id)) continue;

                    bot.ServerRankTalent(id);
                    LastSpend = Time.unscaledTime;
                    return; // one point per tick
                }
            }
            catch { }
        }
    }

    // ---- Talent research ---------------------------------------------------
    internal static class BotResearch
    {
        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null) return;
                if (PLServer.Instance.TalentToResearch != ETalents.MAX) return; // already researching

                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null || ship.MyStats == null) return;

                PLPlayer sci = PLServer.Instance.GetCachedFriendlyPlayerOfClass(2);
                float researchSkill = sci != null ? Learning.CraftSkill(2) : 1f;

                // Upper bound: vanilla ETalents.MAX (63) plus modded talents the
                // .Talents framework appends (IDs 64+). bot.Talents covers both.
                PLPlayer boundRef = PLServer.Instance.GetCachedFriendlyPlayerOfClass(0);
                int bound = boundRef != null && boundRef.Talents != null ? boundRef.Talents.Length : (int)ETalents.MAX;

                ETalents best = ETalents.MAX;
                int bestCost = int.MaxValue;
                for (int i = 0; i < bound; i++)
                {
                    ETalents t = (ETalents)i;
                    if (PLServer.Instance.IsTalentUnlocked(t)) continue;
                    if (!PLGlobal.IsTalentVisibleForResearch(t)) continue;
                    TalentInfo info = PLGlobal.GetTalentInfoForTalentType(t);
                    if (info == null || info.ResearchCost == null) continue;

                    bool affordable = true;
                    int total = 0;
                    for (int k = 0; k < 6 && k < info.ResearchCost.Length; k++)
                    {
                        total += info.ResearchCost[k];
                        if ((int)PLServer.Instance.ResearchMaterials[k] < info.ResearchCost[k]) { affordable = false; break; }
                    }
                    if (!affordable) continue;
                    if (info.WarpsToResearch > 0 && (int)info.WarpsToResearch > Mathf.RoundToInt(12f * researchSkill)) continue;

                    if (total < bestCost) { bestCost = total; best = t; }
                }

                if (best != ETalents.MAX)
                {
                    TalentInfo info = PLGlobal.GetTalentInfoForTalentType(best);
                    PLServer.Instance.TalentToResearch = best;
                    PLServer.Instance.JumpsNeededToResearchTalent = ship.GetJumpsToResearchTalent_WithCurrentContext(info, ship);
                    for (int i = 0; i < 6 && info.ResearchCost != null && i < info.ResearchCost.Length; i++)
                    {
                        PLServer.Instance.ResearchMaterials[i] = (int)PLServer.Instance.ResearchMaterials[i] - info.ResearchCost[i];
                    }
                    Learning.Add(2, 5, 1.5f); // scientist runs research
                    try { PulsarModLoader.Utilities.Messaging.Notification("CapBot started research: " + info.Name); } catch { }
                }
            }
            catch { }
        }
    }

    // ---- Economy: buy/sell components --------------------------------------
    internal static class BotEconomy
    {
        private const int CREDIT_RESERVE = 2500;

        // Pending purchase RPCs: the buy is async (RemoveWare arrives later), so a
        // naive re-scan buys the same ware again on the next tick (the reported
        // "buys the same thing over and over"). These sets bridge that window.
        private static readonly HashSet<int> PendingBuyHashes = new HashSet<int>();
        private static readonly HashSet<int> PendingSellNetIDs = new HashSet<int>();
        private static float LastTransaction = -999f;
        private const float MIN_TX_INTERVAL = 2.5f;

        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null) return;
                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null || ship.InWarp || ship.MyStats == null) return;
                if (Time.unscaledTime - LastTransaction < MIN_TX_INTERVAL) return;

                // Clean out completed transactions once the inventory reflects them.
                // Build live NetID/hash sets from the current inventory.
                HashSet<int> liveNetIDs = new HashSet<int>();
                HashSet<int> liveHashes = new HashSet<int>();
                foreach (PLShipComponent c in ship.MyStats.AllComponents)
                {
                    if (c == null) continue;
                    liveNetIDs.Add(c.NetID);
                    liveHashes.Add((int)c.getHash());
                }
                PendingSellNetIDs.RemoveWhere(id => !liveNetIDs.Contains(id));
                PendingBuyHashes.RemoveWhere(h => liveHashes.Contains(h));

                PLTraderInfo trader = null;
                float bestDist = float.MaxValue;
                foreach (PLTraderInfo t in PLTraderInfo.All)
                {
                    if (t == null || t.MyPDE == null || t.MyPDE.Wares == null || t.MyPDE.Wares.Count == 0) continue;
                    if (!t.SpaceTrader && !t.ContrabandDealer) continue;
                    float d = (t.transform.position - ship.transform.position).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; trader = t; }
                }
                if (trader == null) return;

                int credits = (int)PLServer.Instance.CurrentCrewCredits;
                float econ = Learning.EconomySkill(0);

                // Installed best-of-slot map (keeper per slot).
                Dictionary<ESlotType, PLShipComponent> installed = new Dictionary<ESlotType, PLShipComponent>();
                foreach (PLShipComponent c in ship.MyStats.AllComponents)
                {
                    if (c == null) continue;
                    ESlotType st = c.ActualSlotType;
                    if (st == ESlotType.E_COMP_CARGO || st == ESlotType.E_COMP_NONE || st == ESlotType.E_COMP_VIRUS) continue;
                    if (!installed.TryGetValue(st, out PLShipComponent cur) || c.Level > cur.Level) installed[st] = c;
                }

                List<PLShipComponent> cargo = ship.MyStats.GetComponentsOfType(ESlotType.E_COMP_CARGO, true);

                // SELL: a cargo item is sellable when a same-slot component is
                // installed AND (this item is a duplicate of the best-of-slot, or
                // strictly worse). Pending items are skipped to avoid double-RPC.
                foreach (PLShipComponent c in cargo)
                {
                    if (c == null || PendingSellNetIDs.Contains(c.NetID)) continue;
                    if (!installed.TryGetValue(c.ActualSlotType, out PLShipComponent keeper)) continue;
                    bool sellable = c == keeper ? false : c.Level <= keeper.Level;
                    if (!sellable) continue;
                    int price = trader.GetWarePrice(true, c);
                    if (price <= 0) continue;
                    PendingSellNetIDs.Add(c.NetID);
                    trader.photonView.RPC("SellComponent", PhotonTargets.MasterClient, ship.ShipID, c.NetID, price, 0);
                    Learning.Add(3, 3, price / 1000f); // engineer handles cargo sales
                    LastTransaction = Time.unscaledTime;
                    return; // one transaction per tick
                }

                // No dup/worse cargo left? Sell pure-surplus duplicates even when
                // nothing of that slot is installed (cargo clutter cleanup).
                Dictionary<int, int> cargoByHash = new Dictionary<int, int>();
                foreach (PLShipComponent c in cargo)
                {
                    if (c == null || PendingSellNetIDs.Contains(c.NetID)) continue;
                    if (c.ActualSlotType == ESlotType.E_COMP_CARGO || c.ActualSlotType == ESlotType.E_COMP_NONE) continue;
                    int h = (int)c.getHash();
                    if (cargoByHash.ContainsKey(h))
                    {
                        int price = trader.GetWarePrice(true, c);
                        if (price <= 0) continue;
                        PendingSellNetIDs.Add(c.NetID);
                        trader.photonView.RPC("SellComponent", PhotonTargets.MasterClient, ship.ShipID, c.NetID, price, 0);
                        Learning.Add(3, 3, price / 1000f);
                        LastTransaction = Time.unscaledTime;
                        return;
                    }
                    cargoByHash[h] = c.NetID;
                }

                // BUY: only when cargo has room and the ware is not already
                // pending. Wares the trader no longer has (removed stock after our
                // last RPC) are filtered by checking the live Wares dict — the
                // ware object we scan IS that dict, so instead we guard on the
                // ship-side: never buy a hash the ship already owns or is buying.
                PLSlot cargoSlot = ship.MyStats.GetSlot(ESlotType.E_COMP_CARGO);
                if (cargoSlot == null || cargoSlot.Count >= cargoSlot.MaxItems) return;
                int reserve = Mathf.RoundToInt(CREDIT_RESERVE / Mathf.Max(0.5f, econ));
                foreach (KeyValuePair<int, PLWare> kv in trader.MyPDE.Wares)
                {
                    PLWare ware = kv.Value;
                    PLShipComponent comp = ware as PLShipComponent;
                    if (comp == null) continue;
                    ESlotType st = comp.ActualSlotType;
                    if (st == ESlotType.E_COMP_CARGO || st == ESlotType.E_COMP_NONE || st == ESlotType.E_COMP_VIRUS) continue;

                    int hash = (int)comp.getHash();
                    if (PendingBuyHashes.Contains(hash)) continue;
                    if (liveHashes.Contains(hash)) continue; // ship already owns identical comp

                    int price = trader.GetWarePrice(false, ware);
                    if (price <= 0 || credits - reserve < price) continue;

                    PLShipComponent current;
                    if (!installed.TryGetValue(st, out current))
                    {
                        current = ship.MyStats.GetShipComponent<PLShipComponent>(st);
                        installed[st] = current;
                    }
                    int currentLevel = current != null ? current.Level : -1;
                    if (comp.Level <= currentLevel) continue;
                    if (current != null && current.Level >= comp.Level - 1 && credits < 15000) continue;

                    PendingBuyHashes.Add(hash);
                    trader.photonView.RPC("BuyComponent", PhotonTargets.MasterClient, ship.ShipID, hash, price, kv.Key, 0);
                    Learning.Add(0, 2, price / 2000f); // captain does purchases (shop discounts apply)
                    LastTransaction = Time.unscaledTime;
                    return;
                }
            }
            catch { }
        }
    }

    // ---- Scrap upgrades (components + weapons) -----------------------------
    internal static class BotUpgrades
    {
        private static float LastUpgrade = 0f;

        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null) return;
                if (Time.unscaledTime - LastUpgrade < 2f) return;
                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null || ship.MyStats == null) return;
                if ((int)PLServer.Instance.CurrentUpgradeMats <= 1) return;

                int mats = (int)PLServer.Instance.CurrentUpgradeMats;

                // 1) Component upgrades (reactor, shields, warp, cpu, hull, turrets, thrusters, sensors).
                //    Exotic components (e.g. auto-turrets on custom slot types) are naturally
                //    excluded because GetUpgradableComponents only lists vanilla upgradable slots.
                List<PLShipComponent> upgradable = ship.GetUpgradableComponents();
                if (upgradable != null)
                {
                    PLShipComponent target = null;
                    int targetCost = int.MaxValue;
                    foreach (PLShipComponent c in upgradable)
                    {
                        if (c == null) continue;
                        if (c.Level >= ship.GetMaxCompUpgradeLevel()) continue;
                        int cost = ship.GetMatCostForComp(c);
                        if (cost <= mats && cost < targetCost) { targetCost = cost; target = c; }
                    }
                    if (target != null && targetCost <= mats)
                    {
                        PLServer.Instance.CurrentUpgradeMats = mats - targetCost;
                        target.Level++;
                        Learning.Add(4, 1, 1f); // engineer does component upgrades
                        try { PulsarModLoader.Utilities.Messaging.ShipLog("Upgraded " + target.Name + " to level " + (target.Level + 1), "CAP"); } catch { }
                        LastUpgrade = Time.unscaledTime;
                        return;
                    }
                }

                // 2) Pawn weapon upgrades for every crew bot (Level+1 recreation).
                int maxItemLvl = ship.GetMaxItemUpgradeLevel();
                foreach (PLPlayer p in PLServer.Instance.AllPlayers)
                {
                    if (p == null || !p.IsBot || p.TeamID != 0 || p.MyInventory == null) continue;
                    foreach (PLPawnItem item in p.MyInventory.AllItems.ToList())
                    {
                        if (item == null) continue;
                        if (!(item is PLPawnItem_Gun) && !(item is PLPawnItem_Armor)) continue;
                        if (item.Level >= maxItemLvl) continue;
                        int cost = ship.GetMatCostForItem(item);
                        if (cost > mats) continue;
                        p.MyInventory.UpdateItem(PLServer.Instance.PawnInvItemIDCounter++, (int)item.PawnItemType, item.SubType, item.Level + 1, item.EquipID);
                        PLServer.Instance.CurrentUpgradeMats = mats - cost;
                        Learning.Add(4, 1, 0.75f);
                        LastUpgrade = Time.unscaledTime;
                        return;
                    }
                }
            }
            catch { }
        }
    }

    // ---- Component install / swap (cargo <-> ship slots) -------------------
    // Works for any component type including modded ones: AddShipComponent routes
    // through ActualSlotType handling, so exotic/custom slot types behave the same
    // as vanilla (Exotic Components mod auto-turrets included).
    internal static class BotInstall
    {
        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null) return;
                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null || ship.MyStats == null) return;
                if (!ship.GetIsPlayerShip()) return;

                foreach (PLShipComponent spare in ship.MyStats.GetComponentsOfType(ESlotType.E_COMP_CARGO, true).ToList())
                {
                    if (spare == null) continue;
                    ESlotType st = spare.ActualSlotType;
                    if (st == ESlotType.E_COMP_CARGO || st == ESlotType.E_COMP_NONE) continue;

                    PLShipComponent current = ship.MyStats.GetShipComponent<PLShipComponent>(st);
                    bool replace = false;
                    if (current == null) replace = true;
                    else if (spare.Level > current.Level) replace = true;

                    if (!replace) continue;

                    ship.MyStats.RemoveShipComponent(spare); // out of cargo
                    if (current != null) ship.MyStats.RemoveShipComponent(current);
                    ship.MyStats.AddShipComponent(spare, -1, st);
                    Learning.Add(0, 1, 0.4f);
                    try { PulsarModLoader.Utilities.Messaging.ShipLog("Installed a " + spare.Name + " from cargo", "CAP"); } catch { }
                    return;
                }
            }
            catch { }
        }
    }

    // ---- Campaign auto-detection -------------------------------------------
    // Detects campaign/fragment state and auto-starts known mission chains,
    // mirroring the game's own UpdateFragmentsCollected chain rules:
    //   65521->F0, 67238->F13, 56273->F2, 81262->F11, 80132->F12, races->F10,
    //   FB contest->F6, plus direct fragment sectors handled by Patch.cs behaviours.
    internal static class BotCampaign
    {
        private static readonly Dictionary<int, float> AttemptTimes = new Dictionary<int, float>();
        private const float COOLDOWN = 120f;

        private static bool CanAttempt(int key)
        {
            float last;
            if (AttemptTimes.TryGetValue(key, out last) && Time.unscaledTime - last < COOLDOWN) return false;
            AttemptTimes[key] = Time.unscaledTime;
            return true;
        }

        private static void StartMission(int missionID)
        {
            PLServer.Instance.photonView.RPC("AttemptStartMissionOfTypeID", PhotonTargets.MasterClient, new object[]
            {
                missionID,
                false
            });
        }

        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null || PLGlobal.Instance == null || PLGlobal.Instance.Galaxy == null) return;
                if (PLServer.GetCurrentSector() == null) return;
                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null) return;

                // W.D. weapons testing chain (fragment 6 via FB contest is separate;
                // this is the WD demo mission 59682 -> patch handles the arena once inside).
                PLSectorInfo sector = PLServer.GetCurrentSector();
                if (sector.VisualIndication == ESectorVisualIndication.WD_MISSIONCHAIN_WEAPONS_DEMO
                    && !PLServer.Instance.HasCompletedMissionWithID(59682)
                    && !PLServer.Instance.HasMissionWithID(59682)
                    && CanAttempt(59682))
                {
                    StartMission(59682);
                    try { PulsarModLoader.Utilities.Messaging.Notification("CapBot: starting W.D. weapons demo"); } catch { }
                    return;
                }

                // Grey Huntsman bounty -> fragment 7 (Patch routes + collects once active).
                if (!PLServer.Instance.IsFragmentCollected(7)
                    && !PLServer.Instance.HasMissionWithID(104869)
                    && (int)PLServer.Instance.CurrentCrewCredits >= 25000
                    && CanAttempt(104869))
                {
                    StartMission(104869);
                    try { PulsarModLoader.Utilities.Messaging.Notification("CapBot: accepted a bounty contract"); } catch { }
                    return;
                }

                // High Rollers -> fragment 3. 102403 is the pickup-data mission Patch routes on;
                // 103216 is the buy-in, started once we are in the sector with enough credits.
                if (!PLServer.Instance.IsFragmentCollected(3))
                {
                    if (!PLServer.Instance.HasMissionWithID(102403)
                        && !PLServer.Instance.HasMissionWithID(103216)
                        && (int)PLServer.Instance.CurrentCrewCredits >= 25000
                        && CanAttempt(102403))
                    {
                        StartMission(102403);
                        try { PulsarModLoader.Utilities.Messaging.Notification("CapBot: heading to the High Rollers"); } catch { }
                        return;
                    }
                    if (sector.VisualIndication == ESectorVisualIndication.HIGHROLLERS_STATION
                        && !PLServer.Instance.HasMissionWithID(103216)
                        && (int)PLServer.Instance.CurrentCrewCredits >= 10000
                        && CanAttempt(103216))
                    {
                        StartMission(103216);
                        return;
                    }
                }
            }
            catch { }
        }
    }

    // ---- Extractor operation ------------------------------------------------
    // When the ship is targeting an abandoned ship and cargo space exists, pick the
    // most valuable salvageable component and run the extraction (host-side, same
    // RPC path the captain UI uses). Failure risk (target destroyed) is acceptable:
    // the target is already derelict.
    internal static class BotExtractor
    {
        internal static void Tick()
        {
            try
            {
                if (PLServer.Instance == null) return;
                if (Time.unscaledTime - Autonomy.LastExtract < 4f) return;
                PLShipInfo ship = PLEncounterManager.Instance.PlayerShip as PLShipInfo;
                if (ship == null || ship.MyStats == null || ship.TargetShip == null || ship.TargetShip == ship) return;
                if (!ship.TargetShip.IsAbandoned()) return;

                PLExtractor extractor = ship.MyStats.GetShipComponent<PLExtractor>(ESlotType.E_COMP_SALVAGE_SYSTEM);
                if (extractor == null) return;

                PLSlot cargoSlot = ship.MyStats.GetSlot(ESlotType.E_COMP_CARGO);
                if (cargoSlot == null || cargoSlot.Count >= cargoSlot.MaxItems) return;

                List<PLShipComponent> salvageable = ship.GetSalvageableComponents();
                if (salvageable == null || salvageable.Count == 0) return;

                int bestIdx = -1;
                int bestPrice = -1;
                for (int i = 0; i < salvageable.Count; i++)
                {
                    PLShipComponent c = salvageable[i];
                    if (c == null) continue;
                    int price = c.GetScaledMarketPrice(false);
                    if (price > bestPrice) { bestPrice = price; bestIdx = i; }
                }
                if (bestIdx < 0) return;

                ship.SalvageComp_ID = bestIdx;
                Autonomy.LastExtract = Time.unscaledTime;
                ship.photonView.RPC("AttemptExtraction", PhotonTargets.MasterClient, new object[0]);
            }
            catch { }
        }
    }

    // ---- Mission auto-detection + work (all missions, incl. side) ----------
    internal static class BotMissions
    {
        // True when a pending mission objective can still be worked in the current
        // sector (prevents SetNextDestiny from warping away mid-mission — issue #3).
        // Covers ReachSector targets, plus objectives that live wherever the crew
        // currently is: PickupComponent/PickupItem (cargo holds the item), TalkToNPC
        // (NPC is here), EnterVolumeOfName, and pickup missions generally — their
        // completion checks don't reference a sector, so treat "crew present in an
        // encounter" as workable ground. Only ReachSector/OfType are location-bound.
        internal static bool PendingMissionWorkInCurrentSector()
        {
            try
            {
                PLSectorInfo sector = PLServer.GetCurrentSector();
                if (sector == null || PLServer.Instance == null) return false;
                int hub = PLServer.Instance.GetCurrentHubID();
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null || m.Ended || m.Abandoned) continue;
                    foreach (PLMissionObjective obj in m.Objectives)
                    {
                        if (obj == null || obj.IsCompleted) continue;
                        if (obj is PLMissionObjective_ReachSector reach && reach.SectorToReach == hub) return true;
                        if (obj is PLMissionObjective_ReachSectorOfType) return true;
                        if (obj is PLMissionObjective_PickupComponent || obj is PLMissionObjective_PickupItem
                            || obj is PLMissionObjective_TalkToNPC || obj is PLMissionObjective_EnterVolumeOfName)
                            return true;
                        if (m.IsPickupMission) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // Any NPC with a mission to start or end, anywhere (fixes issue #2:
        // station NPCs were only handled in hardcoded hub sectors).
        private static readonly Dictionary<int, float> LastDialogue = new Dictionary<int, float>();

        // Walk to and interact with objects needed by OPEN objectives of ACTIVE
        // missions: pickup components (warp coils etc.), pickup items, planet
        // volumes, NPC report targets. Uses the same RPCs the vanilla talk/pickup
        // buttons use, so objectives complete exactly as if a player did them.
        private static readonly Dictionary<int, float> LastObjectiveWork = new Dictionary<int, float>();

        internal static void TickObjectiveWork(PLPlayer bot)
        {
            try
            {
                if (bot == null || bot.MyBot == null || bot.GetPawn() == null || bot.StartingShip == null || bot.StartingShip.InWarp) return;
                if (bot.GetClassID() != 0) return; // captain bot does the legwork
                int pid = bot.GetPlayerID();
                float last;
                if (LastObjectiveWork.TryGetValue(pid, out last) && Time.unscaledTime - last < 3f) return;
                LastObjectiveWork[pid] = Time.unscaledTime;

                PLPawn pawn = bot.GetPawn();

                // 1) Mission component pickups: find an un-picked PLPickupComponent
                //    whose type matches an open PickupComponent objective. Only pick
                //    up what the mission needs (never steal random planet loot).
                List<PLPickupComponent> comps = new List<PLPickupComponent>(UnityEngine.Object.FindObjectsOfType<PLPickupComponent>());
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null || m.Ended || m.Abandoned) continue;
                    foreach (PLMissionObjective obj in m.Objectives)
                    {
                        PLMissionObjective_PickupComponent pc = obj as PLMissionObjective_PickupComponent;
                        if (pc == null || pc.IsCompleted) continue;
                        // Match via the objective's internal comp type/subtype.
                        foreach (PLPickupComponent puc in comps)
                        {
                            if (puc == null || puc.PickedUp) continue;
                            if ((int)puc.ItemType != GetPickupCompSlot(pc)) continue;
                            if (puc.SubItemType != GetPickupCompSub(pc)) continue;
                            if (puc.MyInterior == null || pawn.MyInterior == null || puc.MyInterior != pawn.MyInterior) continue;

                            bot.MyBot.AI_TargetPos = puc.transform.position;
                            bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                            if ((bot.MyBot.AI_TargetPos - pawn.transform.position).sqrMagnitude > 16f)
                            {
                                bot.MyBot.EnablePathing = true;
                                return;
                            }
                            bot.photonView.RPC("AttemptToPickupComponentAtID", PhotonTargets.MasterClient, puc.PickupID);
                            pawn.photonView.RPC("Anim_Pickup", PhotonTargets.Others);
                            return;
                        }
                    }
                }

                // 2) Mission item pickups (pawn items on planets).
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null || m.Ended || m.Abandoned) continue;
                    foreach (PLMissionObjective obj in m.Objectives)
                    {
                        if (!(obj is PLMissionObjective_PickupItem) || obj.IsCompleted) continue;
                        foreach (PLPickupObject po in UnityEngine.Object.FindObjectsOfType<PLPickupObject>())
                        {
                            if (po == null || po.PickedUp) continue;
                            if (po.MyInterior == null || pawn.MyInterior == null || po.MyInterior != pawn.MyInterior) continue;
                            bot.MyBot.AI_TargetPos = po.transform.position;
                            bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                            if ((bot.MyBot.AI_TargetPos - pawn.transform.position).sqrMagnitude > 16f)
                            {
                                bot.MyBot.EnablePathing = true;
                                return;
                            }
                            bot.photonView.RPC("AttemptToPickupObjectAtID", PhotonTargets.MasterClient, po.PickupID);
                            pawn.photonView.RPC("Anim_Pickup", PhotonTargets.Others);
                            return;
                        }
                    }
                }

                // 3) Planet volumes (EnterVolumeOfName objectives).
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null || m.Ended || m.Abandoned) continue;
                    foreach (PLMissionObjective obj in m.Objectives)
                    {
                        if (!(obj is PLMissionObjective_EnterVolumeOfName) || obj.IsCompleted) continue;
                        string want = GetVolumeName(obj);
                        if (want == null) continue;
                        GameObject vol = GameObject.Find(want);
                        if (vol == null) continue;
                        bot.MyBot.AI_TargetPos = vol.transform.position;
                        bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                        if ((bot.MyBot.AI_TargetPos - pawn.transform.position).sqrMagnitude > 25f)
                        {
                            bot.MyBot.EnablePathing = true;
                        }
                        // Volume completion is driven by vanilla trigger overlap when
                        // the pawn physically enters — pathing there is enough.
                        return;
                    }
                }

                // 4) TalkToNPC objectives: find the NPC by actor name in this TLI and
                //    walk to it; TickDialogueWork handles the actual talk RPC once close.
                foreach (PLMissionBase m in PLServer.Instance.AllMissions)
                {
                    if (m == null || m.Ended || m.Abandoned) continue;
                    foreach (PLMissionObjective obj in m.Objectives)
                    {
                        PLMissionObjective_TalkToNPC tt = obj as PLMissionObjective_TalkToNPC;
                        if (tt == null || tt.IsCompleted) continue;
                        string actor = GetTalkActor(tt);
                        if (actor == null) continue;
                        foreach (PLDialogueActorInstance npc in UnityEngine.Object.FindObjectsOfType<PLDialogueActorInstance>())
                        {
                            if (npc == null || (npc.ActorName ?? "") != actor) continue;
                            if (bot.MyCurrentTLI == null || npc.TLIInParent == null || npc.TLIInParent != bot.MyCurrentTLI) continue;
                            bot.MyBot.AI_TargetPos = npc.transform.position;
                            bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                            if ((bot.MyBot.AI_TargetPos - pawn.transform.position).sqrMagnitude > 25f)
                            {
                                bot.MyBot.EnablePathing = true;
                                return;
                            }
                            // Close enough — the dialogue tick will fire the RPC.
                            return;
                        }
                        // NPC not in this TLI: the mission's ReachSector objective
                        // (or course planning) handles getting there; nothing to do here.
                    }
                }
            }
            catch { }
        }

        // Reflection readers for objective internals (private fields).
        private static System.Reflection.FieldInfo _pcCompType;
        private static System.Reflection.FieldInfo _pcSubType;
        private static System.Reflection.FieldInfo _volName;
        private static System.Reflection.FieldInfo _ttActor;

        private static int GetPickupCompSlot(PLMissionObjective pc)
        {
            if (_pcCompType == null) _pcCompType = AccessTools.Field(typeof(PLMissionObjective_PickupComponent), "CompType");
            return _pcCompType != null ? (int)_pcCompType.GetValue(pc) : -1;
        }

        private static int GetPickupCompSub(PLMissionObjective pc)
        {
            if (_pcSubType == null) _pcSubType = AccessTools.Field(typeof(PLMissionObjective_PickupComponent), "SubType");
            return _pcSubType != null ? (int)_pcSubType.GetValue(pc) : -1;
        }

        private static string GetVolumeName(PLMissionObjective obj)
        {
            if (_volName == null) _volName = AccessTools.Field(typeof(PLMissionObjective_EnterVolumeOfName), "VolumeName");
            return _volName != null ? (string)_volName.GetValue(obj) : null;
        }

        private static string GetTalkActor(PLMissionObjective obj)
        {
            if (_ttActor == null) _ttActor = AccessTools.Field(typeof(PLMissionObjective_TalkToNPC), "ActorTypeID");
            return _ttActor != null ? (string)_ttActor.GetValue(obj) : null;
        }

        internal static void TickDialogueWork(PLPlayer bot)
        {
            try
            {
                if (bot == null || bot.MyBot == null || bot.GetPawn() == null || bot.StartingShip == null || bot.StartingShip.InWarp) return;
                int pid = bot.GetPlayerID();
                float last;
                if (LastDialogue.TryGetValue(pid, out last) && Time.unscaledTime - last < 6f) return;

                List<PLDialogueActorInstance> npcs = new List<PLDialogueActorInstance>();
                foreach (PLDialogueActorInstance npc in UnityEngine.Object.FindObjectsOfType<PLDialogueActorInstance>())
                {
                    if (npc == null || npc.ShipDialogue) continue;
                    if (!npc.HasMissionStartAvailable && !npc.HasMissionEndAvailable) continue;
                    string name = (npc.DisplayName ?? "").ToLower();
                    if (name.Contains("eldon gatra") || name.Contains("baris") || name.Contains("zeng")) continue;
                    npcs.Add(npc);
                }
                if (npcs.Count == 0) return;

                // Only pursue NPCs in the same TLI as the bot — cross-TLI trips are
                // handled by course planning; the game teleports crew via SetNextDestiny.
                PLDialogueActorInstance target = null;
                float best = float.MaxValue;
                PLTeleportationLocationInstance botTLI = bot.MyCurrentTLI;
                foreach (PLDialogueActorInstance npc in npcs)
                {
                    if (botTLI == null || npc.TLIInParent == null || npc.TLIInParent != botTLI) continue;
                    float d = (npc.transform.position - bot.GetPawn().transform.position).sqrMagnitude;
                    if (d < best) { best = d; target = npc; }
                }
                if (target == null) return;

                bot.MyBot.AI_TargetPos = target.transform.position;
                bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                if (best > 25f)
                {
                    bot.MyBot.EnablePathing = true;
                    return;
                }

                LastDialogue[pid] = Time.unscaledTime;

                // The game only advances TalkToNPC objectives from the vanilla
                // talk button (PLGameStatic → RPC "TalkToNPCOfActorType"). Bots
                // must emit the same RPC after opening dialogue or "report to X"
                // objectives never complete.
                try { PLServer.Instance.photonView.RPC("TalkToNPCOfActorType", PhotonTargets.MasterClient, target.ActorName); } catch { }

                if (target.HasMissionStartAvailable && target.AllAvailableChoices() != null && target.AllAvailableChoices().Count > 0)
                {
                    LineData line = target.AllAvailableChoices()[0];
                    int guard = 0;
                    while (guard++ < 12
                        && line != null
                        && (line.TextOptions == null || line.TextOptions.Count <= 0 || line.TextOptions[0].ToLower() != "accept")
                        && line.ChildLines != null && line.ChildLines.Count > 0)
                    {
                        line = line.ChildLines[0];
                    }
                    if (line != null && line.TextOptions != null && line.TextOptions.Count > 0 && line.TextOptions[0].ToLower() == "accept")
                    {
                        target.SelectChoice(line, true, true);
                    }
                }
                else if (target.HasMissionEndAvailable)
                {
                    if (target.AllAvailableChoices() != null && target.AllAvailableChoices().Count > 0)
                    {
                        target.SelectChoice(target.AllAvailableChoices()[0], true, true);
                    }
                    try { target.BeginDialogue(); } catch { }
                }
            }
            catch { }
        }
    }

    // ---- Bot inventory management (equip best gear from class locker) ------
    internal static class BotInventory
    {
        private static readonly Dictionary<int, float> LastCheck = new Dictionary<int, float>();

        internal static void Tick(PLPlayer bot)
        {
            try
            {
                if (bot.MyInventory == null || bot.GetClassID() < 0) return;
                int pid = bot.GetPlayerID();
                float last;
                if (LastCheck.TryGetValue(pid, out last) && Time.unscaledTime - last < 5f) return;

                PLPawn pawn = bot.GetPawn();
                if (pawn == null) return;

                PLServerClassInfo info = PLServer.Instance.ClassInfos[bot.GetClassID()];
                PLPawnInventoryBase locker = info != null ? info.ClassLockerInventory : null;
                if (locker == null || locker.InventoryID == -1 || bot.MyInventory.InventoryID == -1) return;

                // Mirror PLPawn's default-gear move: pull the most expensive item of each
                // key type from the class locker if the bot's own inventory lacks it.
                int equipSlot = 1;
                bool swapped = false;
                swapped |= SwapIfMissing<PLPawnItem_PhasePistol>(bot, locker, ref equipSlot);
                swapped |= SwapIfMissing<PLPawnItem_Gun>(bot, locker, ref equipSlot);
                swapped |= SwapIfMissing<PLPawnItem_RepairGun>(bot, locker, ref equipSlot);
                swapped |= SwapIfMissing<PLPawnItem_FireGun>(bot, locker, ref equipSlot);
                swapped |= SwapIfMissing<PLPawnItem_Scanner>(bot, locker, ref equipSlot);
                swapped |= SwapIfMissing<PLPawnItem_Armor>(bot, locker, ref equipSlot);

                if (swapped) LastCheck[pid] = Time.unscaledTime + 20f;
                else LastCheck[pid] = Time.unscaledTime;
            }
            catch { }
        }

        private static bool SwapIfMissing<T>(PLPlayer bot, PLPawnInventoryBase locker, ref int equipSlot) where T : PLPawnItem
        {
            try
            {
                bool haveType = false;
                foreach (PLPawnItem it in bot.MyInventory.AllItems)
                {
                    if (it is T) { haveType = true; break; }
                }
                if (haveType) return false;

                PLPawnItem best = locker.GetPawnItemOfType_MostExpensive<T>();
                if (best == null) return false;

                locker.photonView.RPC("ServerItemSwap", PhotonTargets.All, bot.MyInventory.InventoryID, best.NetID);
                bot.MyInventory.photonView.RPC("ServerEquip", PhotonTargets.All, best.NetID, equipSlot++);
                return true;
            }
            catch { return false; }
        }
    }

    // ---- Movement stuck watchdog (all bots) --------------------------------
    internal static class StuckWatchdog
    {
        private class Entry
        {
            public Vector3 LastPos;
            public float LastMoveTime;
            public int Escalation;
            public float LastEscalationTime;
        }
        private static readonly Dictionary<int, Entry> Watched = new Dictionary<int, Entry>();

        internal static void Tick(PLPlayer bot)
        {
            try
            {
                PLPawn pawn = bot.GetPawn();
                if (pawn == null || bot.MyBot == null) return;
                if ((bool)pawn.IsDead || pawn.MyController == null) return;
                Entry e;
                if (!Watched.TryGetValue(bot.GetPlayerID(), out e))
                {
                    Watched[bot.GetPlayerID()] = new Entry { LastPos = pawn.transform.position, LastMoveTime = Time.unscaledTime, Escalation = 0, LastEscalationTime = Time.unscaledTime };
                    return;
                }

                // Moved since last tick? Then healthy.
                if ((pawn.transform.position - e.LastPos).sqrMagnitude > 0.09f)
                {
                    e.LastPos = pawn.transform.position;
                    e.LastMoveTime = Time.unscaledTime;
                    e.Escalation = 0;
                    return;
                }

                // Arrived at the goal (standing at target with pathing on is NOT stuck) —
                // screens/chairs/workstations hold the bot at AI_TargetPos legitimately.
                Vector3 target = bot.MyBot.AI_TargetPos;
                if (target == Vector3.zero || (target - pawn.transform.position).sqrMagnitude < 6.25f)
                {
                    e.LastMoveTime = Time.unscaledTime;
                    e.Escalation = 0;
                    return;
                }

                float stuckFor = Time.unscaledTime - e.LastMoveTime;
                if (!bot.MyBot.EnablePathing || stuckFor < 4f) return;

                // Rate-limit escalations to one attempt every 4s, cycling forever
                // instead of giving up after the first pass (old code stopped at
                // escalation 3 and left genuinely-stuck bots stuck for good).
                if (Time.unscaledTime - e.LastEscalationTime < 4f) return;
                e.LastEscalationTime = Time.unscaledTime;
                e.Escalation++;

                switch (e.Escalation % 4)
                {
                    case 1:
                        // Toggle pathing to force the controller to re-seek a path.
                        bot.MyBot.EnablePathing = false;
                        bot.MyBot.EnablePathing = true;
                        break;
                    case 2:
                        {
                            // Small push toward the target.
                            Vector3 dir = (target - pawn.transform.position).normalized;
                            pawn.transform.position += dir * 1.5f;
                            pawn.OnTeleport();
                            e.LastPos = pawn.transform.position;
                            break;
                        }
                    case 3:
                        {
                            // Detour around whatever is blocking: step sideways off the line.
                            Vector3 side = Vector3.Cross(Vector3.up, (target - pawn.transform.position).normalized) * 3f;
                            bot.MyBot.AI_TargetPos = pawn.transform.position + side;
                            bot.MyBot.AI_TargetPos_Raw = bot.MyBot.AI_TargetPos;
                            bot.MyBot.EnablePathing = true;
                            break;
                        }
                    default:
                        {
                            // Hard unstick: teleport onto the nav graph near the goal —
                            // the same trick the vanilla PLBotController uses for
                            // long-stuck bots when nobody is looking.
                            Pathfinding.GraphNode unused;
                            Vector3 spot = bot.MyBot.GetPositionOnGraphWithinRange(target, UnityEngine.Random.Range(3f, 8f), out unused);
                            if (spot != Vector3.zero)
                            {
                                pawn.transform.position = spot + Vector3.up * 0.5f;
                                pawn.OnTeleport();
                                e.LastPos = pawn.transform.position;
                                e.LastMoveTime = Time.unscaledTime;
                                e.Escalation = 0;
                            }
                            break;
                        }
                }
            }
            catch { }
        }
    }
}
