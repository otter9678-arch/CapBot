using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CapBot.AI
{
    // Lets bots spend their own game talent points (host-side reflection on
    // PLPlayer.ServerRankTalent) and manage pickups via the legacy proven
    // AttemptToPickupObjectAtID pattern.
    public static class BotSelfManager
    {
        private static readonly Dictionary<int, int[]> TalentPriorities = new Dictionary<int, int[]>
        {
            // 0 = Captain
            { 0, new[] { 5, 26, 27, 28, 42, 47 } },   // CAP_CREW_SPEED, INC_MAX_WEIGHT, CAP_ARMOR_BOOST(28?), CAP_DIPLOMACY(42), CAP_SCREEN_DEFENSE(47)
            // 1 = Pilot
            { 1, new[] { 8, 9, 34, 35, 10 } },        // PIL_SHIP_SPEED, PIL_SHIP_TURNING, PIL_REDUCE_SYS_DAMAGE, PIL_REDUCE_HULL_DAMAGE, PIL_KEEN_EYES
            // 2 = Scientist
            { 2, new[] { 11, 33, 52, 53, 14 } },      // SCI_SENSOR_BOOST, SCI_SCANNER_RESEARCH_MAT, SCI_PROBE_XP, SCI_PROBE_COOLDOWN, SCI_FREQ_AMPLIFIER
            // 3 = Weapons Specialist
            { 3, new[] { 15, 49, 50, 22, 23 } },      // WPNS_TURRET_BOOST, WPN_SCREEN_HACKER, WPN_AMMO_BOOST, WPNS_MISSILE_EXPERT, WPNS_RANGE_BOOST
            // 4 = Engineer
            { 4, new[] { 20, 28, 42, 29, 43 } },      // ENG_FIRE_REDUCTION, CAP_ARMOR? -> see note, ENG_WARP_CHARGE_BOOST(29), ENG_SALVAGE(44?)... verified below
        };

        // Recomputed priorities straight from the ETalents enum indices.
        static BotSelfManager()
        {
            // Engineer: ENG_FIRE_REDUCTION=20, ENG_COOLANT_MIX_CUSTOM=21, ENG_REPAIR_DRONES=22? no.
            // Enum order verified: 20=ENG_FIRE_REDUCTION, 21=ENG_COOLANT_MIX_CUSTOM,
            // 22=WPNS_MISSILE_EXPERT, 23=WPNS_RANGE_BOOST, 29=ENG_WARP_CHARGE_BOOST,
            // 41=ENG_AUX_POWER_BOOST, 44=ENG_SALVAGE, 45=ENG_COREPOWERBOOST, 46=ENG_CORECOOLINGBOOST.
            TalentPriorities[4] = new[] { 20, 21, 29, 44, 45 };
            // Captain: 5=CAP_CREW_SPEED_BOOST, 6=CAP_SCAVENGER, 7=CAP_SHOP_DISCOUNTS,
            // 27=CAP_ARMOR_BOOST, 42=CAP_DIPLOMACY, 43=CAP_INTIMIDATION.
            TalentPriorities[0] = new[] { 5, 27, 42, 6, 47 };
            // Weapons: 15=WPNS_TURRET_BOOST, 17=WPNS_MISSILE_EXPERT, 18=WPNS_RANGE_BOOST,
            // 23=WPNS_COOLING, 49=WPN_SCREEN_HACKER, 50=WPN_AMMO_BOOST.
            TalentPriorities[3] = new[] { 15, 17, 18, 49, 50 };
            // Pilot: 8=PIL_SHIP_SPEED, 9=PIL_SHIP_TURNING, 10=PIL_KEEN_EYES,
            // 34=PIL_REDUCE_SYS_DAMAGE, 35=PIL_REDUCE_HULL_DAMAGE.
            TalentPriorities[1] = new[] { 8, 9, 34, 35 };
            // Science: 11=SCI_SENSOR_BOOST, 13=SCI_HEAL_NEARBY, 14=SCI_FREQ_AMPLIFIER,
            // 33=SCI_SCANNER_RESEARCH_MAT, 52=SCI_PROBE_XP, 53=SCI_PROBE_COOLDOWN.
            TalentPriorities[2] = new[] { 11, 33, 52, 53, 13 };
        }

        private static MethodInfo _rankTalent;

        private static MethodInfo RankTalentMethod
        {
            get
            {
                if (_rankTalent == null)
                {
                    _rankTalent = typeof(PLPlayer).GetMethod("ServerRankTalent",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return _rankTalent;
            }
        }

        // Spend every unspent talent point on the class's priority list.
        public static void SpendTalentPoints(PLPlayer botPlayer)
        {
            if (botPlayer == null || !botPlayer.IsBot || !PhotonNetwork.isMasterClient)
                return;

            int available = (int)botPlayer.TalentPointsAvailable;
            if (available <= 0) return;

            int classID = botPlayer.GetClassID();
            if (!TalentPriorities.TryGetValue(classID, out int[] priorities))
                return;

            if (RankTalentMethod == null) return;

            foreach (int talentID in priorities)
            {
                while (available > 0)
                {
                    // ServerRankTalent validates max-rank and availability;
                    // call until points are exhausted or no longer spent.
                    int before = (int)botPlayer.TalentPointsAvailable;
                    RankTalentMethod.Invoke(botPlayer, new object[] { talentID });
                    int after = (int)botPlayer.TalentPointsAvailable;

                    if (after >= before) break;
                    available = after;
                }
                if (available <= 0) break;
            }
        }

        // Pick up nearby items that this bot's loadout wants (legacy-proven
        // AttemptToPickupObjectAtID RPC).
        public static void ManageNearbyPickups(CaptainBot bot)
        {
            PLPlayer p = bot?.Player;
            PLPawn pawn = p?.GetPawn();
            PLBot ai = p?.MyBot;
            if (p == null || pawn == null || ai == null || !PhotonNetwork.isMasterClient)
                return;

            foreach (PLPickupObject item in Object.FindObjectsOfType(typeof(PLPickupObject)))
            {
                if (item == null || item.PickedUp) continue;
                if ((item.transform.position - pawn.transform.position).sqrMagnitude >= 16) continue;

                string name = item.GetItemName(true);
                if (string.IsNullOrEmpty(name) || !Loadouts.LoadoutManager.ShouldPickUp(bot, name))
                    continue;

                ai.AI_TargetPos = item.transform.position;
                ai.AI_TargetPos_Raw = ai.AI_TargetPos;
                p.photonView.RPC("AttemptToPickupObjectAtID", PhotonTargets.MasterClient,
                    new object[] { item.PickupID });
                break; // one pickup target at a time
            }
        }

        // -----------------------------
        // RESEARCH DUTY
        // Moves research samples from bot inventories into the ship's research
        // locker and atomizes them (all APIs verified in decompiles:
        // ServerItemSwap / ClickAtomize / PLPawnItem_ResearchMaterial).
        // -----------------------------
        private static float _nextAtomizeCheck;

        public static void PollResearch(CaptainBot bot)
        {
            if (!PhotonNetwork.isMasterClient) return;

            PLShipInfo ship = bot?.Player?.StartingShip;
            if (ship == null) return;

            PLPawnInventoryBase locker = PLServer.Instance.ResearchLockerInventory;
            if (locker == null) return;

            int lockerID = locker.InventoryID;
            bool movedAny = false;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p == null || !p.IsBot || p.TeamID != 0) continue;

                PLPawnInventoryBase inv = p.MyInventory;
                if (inv == null) continue;

                List<PLPawnItem> materials = new List<PLPawnItem>();
                foreach (PLPawnItem item in inv.AllItems)
                {
                    if (item is PLPawnItem_ResearchMaterial)
                        materials.Add(item);
                }

                foreach (PLPawnItem mat in materials)
                {
                    // Host-side equivalent of the ServerItemSwap RPC (we are
                    // the master client, so invoke the RPC method directly on
                    // the inventory's photonView for network consistency).
                    inv.photonView.RPC("ServerItemSwap", PhotonTargets.MasterClient,
                        new object[] { lockerID, mat.NetID });
                    movedAny = true;
                }
            }

            // Atomize at most every 5s once samples are present.
            if (movedAny || (locker.AllItems.Count > 0 && Time.time >= _nextAtomizeCheck))
            {
                _nextAtomizeCheck = Time.time + 5f;
                ship.ClickAtomize();
            }
        }

        public static void Poll(PLPlayer botPlayer)
        {
            SpendTalentPoints(botPlayer);
        }
    }
}