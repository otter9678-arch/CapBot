using CapBot.Core.Logging;

namespace CapBot.Core.Learning
{
    // ---- Phase 25: adaptive learning logging bridge -----------------------------
    // Attaches CapBotLog (LEARNING subsystem) as the learning director's
    // decision listener at mod boot — same pattern as ExperienceLogBridge and
    // PersonalityLogBridge. The domain itself stays pure C#.
    public static class LearningLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            AdaptiveLearningDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.LEARNING, line));
            m_Attached = true;
        }
    }
}