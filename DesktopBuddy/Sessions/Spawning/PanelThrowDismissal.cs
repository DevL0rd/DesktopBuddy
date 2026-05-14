using Elements.Core;
using FrooxEngine;

namespace DesktopBuddy;

public partial class DesktopBuddyMod
{
    private static void StartThrowDismissalTracker(Slot root, Grabbable grabbable, User localUser)
    {
        const int HISTORY_SIZE = 5;
        float3[] posHistory = new float3[HISTORY_SIZE];
        floatQ[] rotHistory = new floatQ[HISTORY_SIZE];
        double[] timeHistory = new double[HISTORY_SIZE];
        int histIdx = 0;
        bool wasGrabbed = false;
        bool thrown = false;

        void ThrowTrackLoop()
        {
            if (root.IsDestroyed || thrown) return;
            bool isGrabbed = grabbable.IsGrabbed;

            if (isGrabbed)
            {
                int idx = histIdx % HISTORY_SIZE;
                posHistory[idx] = root.GlobalPosition;
                rotHistory[idx] = root.GlobalRotation;
                timeHistory[idx] = root.World.Time.WorldTime;
                histIdx++;
            }
            else if (wasGrabbed && histIdx >= 2)
            {
                int newest = (histIdx - 1) % HISTORY_SIZE;
                int oldest = (histIdx >= HISTORY_SIZE) ? (histIdx % HISTORY_SIZE) : 0;
                double dt = timeHistory[newest] - timeHistory[oldest];
                if (dt > 0.001)
                {
                    float3 velocity = (posHistory[newest] - posHistory[oldest]) / (float)dt;
                    float speed = velocity.Magnitude;

                    if (speed > 3f)
                    {
                        thrown = true;

                        var cc = root.AttachComponent<CharacterController>();
                        cc.SimulatingUser.Target = localUser;
                        cc.Gravity.Value = new float3(0f, -9.81f, 0f);
                        cc.LinearDamping.Value = 0.3f;
                        cc.LinearVelocity = velocity;

                        int prev = (histIdx - 2 + HISTORY_SIZE) % HISTORY_SIZE;
                        double frameDt = timeHistory[newest] - timeHistory[prev];
                        floatQ perFrameRot = floatQ.Identity;
                        if (frameDt > 0.001)
                        {
                            floatQ rotDelta = rotHistory[newest] * rotHistory[prev].Conjugated;
                            float dtRatio = (1f / 60f) / (float)frameDt;
                            var identity = floatQ.Identity;
                            perFrameRot = MathX.Slerp(in identity, rotDelta, dtRatio);
                        }

                        StartThrowFade(root, perFrameRot);
                        return;
                    }
                }
                histIdx = 0;
            }

            wasGrabbed = isGrabbed;
            root.World.RunInUpdates(isGrabbed ? 1 : 10, ThrowTrackLoop);
        }

        root.World.RunInUpdates(1, ThrowTrackLoop);
    }

    private static void StartThrowFade(Slot root, floatQ perFrameRot)
    {
        const float fadeSeconds = 1f;
        double startTime = root.World.Time.WorldTime;
        float3 lastPos = root.GlobalPosition;
        int frameCount = 0;

        void FadeAndCollisionLoop()
        {
            if (root.IsDestroyed) return;
            frameCount++;
            double elapsed = root.World.Time.WorldTime - startTime;
            float t = MathX.Clamp01((float)(elapsed / fadeSeconds));

            float scale = MathX.Lerp(1f, 0f, t * t);
            root.LocalScale = float3.One * MathX.Max(0.01f, scale);

            root.LocalRotation = root.LocalRotation * perFrameRot;

            float3 curPos = root.GlobalPosition;
            if (frameCount > 5)
            {
                float delta = (curPos - lastPos).Magnitude;
                if (delta < 0.001f)
                {
                    root.Destroy();
                    return;
                }
            }
            lastPos = curPos;

            if (t >= 1f)
            {
                root.Destroy();
                return;
            }
            root.World.RunInUpdates(1, FadeAndCollisionLoop);
        }

        root.World.RunInUpdates(1, FadeAndCollisionLoop);
    }
}
