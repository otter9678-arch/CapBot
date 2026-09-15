using UnityEngine;
using System.Collections.Generic;

namespace CapBot.AI
{
    public static class RaceBehavior
    {
        private static List<Transform> _checkpoints = new List<Transform>();
        private static int _currentIndex = 0;
        private static bool _initialized = false;

        public static void Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;
            if (ship == null) return;

            // Initialize checkpoint list once
            if (!_initialized)
                InitializeCheckpoints();

            if (_checkpoints.Count == 0)
                return;

            Transform target = _checkpoints[_currentIndex];

            // Fly toward checkpoint
            NavigateToCheckpoint(ship, target);

            // Check if reached
            if (ReachedCheckpoint(ship, target))
            {
                _currentIndex++;

                // Race finished
                if (_currentIndex >= _checkpoints.Count)
                {
                    FinishRace(ship);
                    ResetRace();
                    return;
                }
            }
        }

        // -----------------------------
        // INITIALIZE CHECKPOINTS
        // -----------------------------
        private static void InitializeCheckpoints()
        {
            _checkpoints.Clear();

            foreach (GameObject obj in GameObject.FindObjectsOfType<GameObject>())
            {
                if (obj.name.Contains("RaceCheckpoint"))
                {
                    _checkpoints.Add(obj.transform);
                }
            }

            _checkpoints.Sort((a, b) =>
                a.name.CompareTo(b.name)); // Ensure correct order

            _currentIndex = 0;
            _initialized = true;
        }

        // -----------------------------
        // NAVIGATION LOGIC
        // -----------------------------
        private static void NavigateToCheckpoint(PLShipInfo ship, Transform target)
        {
            Vector3 targetPos = target.position;

            // Face the checkpoint
            ship.MyFlightAI.RequestLookTarget(targetPos);

            // Thrust forward
            ship.MyFlightAI.RequestThrust(1f);

            // Use boost if available
            if (ship.MyStats.EngineBoostCharge >= 0.9f)
            {
                ship.MyFlightAI.RequestBoost();
            }

            // Avoid obstacles
            AvoidObstacles(ship);
        }

        // -----------------------------
        // OBSTACLE AVOIDANCE
        // -----------------------------
        private static void AvoidObstacles(PLShipInfo ship)
        {
            RaycastHit hit;
            Vector3 forward = ship.Exterior.transform.forward;

            if (Physics.Raycast(ship.Exterior.transform.position, forward, out hit, 200f))
            {
                // Turn slightly left or right
                float direction = Random.value > 0.5f ? 1f : -1f;
                ship.MyFlightAI.RequestYaw(direction * 0.5f);
            }
        }

        // -----------------------------
        // CHECKPOINT REACHED?
        // -----------------------------
        private static bool ReachedCheckpoint(PLShipInfo ship, Transform checkpoint)
        {
            float dist = (checkpoint.position - ship.Exterior.transform.position).magnitude;
            return dist < 150f; // Close enough to count
        }

        // -----------------------------
        // FINISH RACE
        // -----------------------------
        private static void FinishRace(PLShipInfo ship)
        {
            // Stop thrust
            ship.MyFlightAI.RequestThrust(0f);

            // Celebrate (optional)
            PLServer.Instance.photonView.RPC("AddCrewCredits", PhotonTargets.All, new object[]
            {
                ship.ShipID,
                5000 // reward
            });
        }

        // -----------------------------
        // RESET RACE STATE
        // -----------------------------
        private static void ResetRace()
        {
            _initialized = false;
            _currentIndex = 0;
            _checkpoints.Clear();
        }
    }
}