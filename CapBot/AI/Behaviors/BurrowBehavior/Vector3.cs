using UnityEngine;
using System.Collections.Generic;

namespace CapBot.AI
{
    public static class Vector3
    {
        private static Vector3 _devicePos = Vector3.zero;
        public CapBot.AI.Vector3 DevicePos
        {
            get { return _devicePos; }
            set { _devicePos = value; }
        }
        private static bool _deviceLocated = false;

        private static Vector3 _fragmentPos = Vector3.zero;
        private static bool _fragmentLocated = false;
        private static PLPlayer PLPlayer;
        public static void Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLPawn pawn = player.GetPawn();
            PLBot ai = player.MyBot;

            if (pawn == null)
                return;

            // Step 1: Locate the Burrow device
            if (!_deviceLocated)
                LocateBurrowDevice();

            // Step 2: Locate the fragment
            if (!_fragmentLocated)
                LocateFragment();

            // Step 3: Fight enemies if present
            if (EnemiesNearby(pawn))
            {
                CombatBehavior.Run(bot);
                return;
            }

            // Step 4: Move to device and activate it
            if (!_fragmentLocated)
            {
                MoveToDevice(bot, pawn, ai);
                TryActivateDevice(bot, pawn);
                return;
            }

            // Step 5: Move to fragment and collect it
            MoveToFragment(bot, pawn, ai);
            TryCollectFragment(bot, pawn);
        }

        // -----------------------------
        // LOCATE BURROW DEVICE
        // -----------------------------
        private static void LocateBurrowDevice()
        {
            foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
            {
                if (obj.name.Contains("BurrowDevice"))
                {
                    DevicePos = obj.transform.position;
                    _deviceLocated = true;
                    return;
                }
            }
        }

        // -----------------------------
        // LOCATE FRAGMENT
        // -----------------------------
        private static void LocateFragment()
        {
            foreach (PLPickupObject pickup in Object.FindObjectsOfType<PLPickupObject>())
            {
                if (pickup.MyItem != null &&
                    pickup.MyItem.Name.Contains("Fragment"))
                {
                    _fragmentPos = pickup.transform.position;
                    _fragmentLocated = true;
                    return;
                }
            }
        }

        // -----------------------------
        // MOVE TO DEVICE
        // -----------------------------
        private static void MoveToDevice(CapBot bot, PLPawn pawn, PLBot ai)
        {
            ai.AI_TargetPos = DevicePos;
            ai.AI_TargetPos_Raw = DevicePos;

            if ((DevicePos - pawn.transform.position).sqrMagnitude > 4f)
            {
                ai.EnablePathing = true;
            }
        }

        // -----------------------------
        // ACTIVATE DEVICE
        // -----------------------------
        private static void TryActivateDevice(CapBot bot, PLPawn pawn)
        {
            if ((DevicePos - pawn.transform.position).sqrMagnitude <= 4f)
            {
                foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (obj.name.Contains("BurrowDevice"))
                    {
                        PhotonView view = obj.GetComponent<PhotonView>();
                        if (view != null)
                        {
                            view.RPC("ActivateDevice", PhotonTargets.MasterClient, new object[] { });
                            bot.LastActionTime = Time.time;
                        }
                        return;
                    }
                }
            }
        }

        // -----------------------------
        // MOVE TO FRAGMENT
        // -----------------------------
        private static void MoveToFragment(CapBot bot, PLPawn pawn, PLBot ai)
        {
            ai.AI_TargetPos = _fragmentPos;
            ai.AI_TargetPos_Raw = _fragmentPos;

            if ((_fragmentPos - pawn.transform.position).sqrMagnitude > 4f)
            {
                ai.EnablePathing = true;
            }
        }

        // -----------------------------
        // COLLECT FRAGMENT
        // -----------------------------
        private static void TryCollectFragment(CapBot bot, PLPawn pawn)
        {
            if ((_fragmentPos - pawn.transform.position).sqrMagnitude <= 4f)
            {
                foreach (PLPickupObject pickup in Object.FindObjectsOfType<PLPickupObject>())
                {
                    if (pickup.MyItem != null &&
                        pickup.MyItem.Name.Contains("Fragment"))
                    {
                        pickup.photonView.RPC("AttemptPickup", PhotonTargets.MasterClient,
                            new object[] { bot.Player.GetPlayerID() });

                        bot.LastActionTime = Time.time;
                        return;
                    }
                }
            }
        }

        // -----------------------------
        // ENEMY DETECTION
        // -----------------------------
        private static bool EnemiesNearby(PLPawn pawn)
        {
            foreach (PLPawn enemy in Object.FindObjectsOfType<PLPawn>())
            {
                if (enemy != null &&
                    enemy.TeamID != 0 &&
                    (enemy.transform.position - pawn.transform.position).sqrMagnitude < 400f)
                {
                    return true;
                }
            }
            return false;
        }
    }
}