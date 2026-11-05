using BepInEx;
using UnityEngine;

namespace Probe
{
    /// <summary>Does nothing except measure frame pacing. Used to separate engine stutter from trainer code.</summary>
    [BepInPlugin("roger.crossfirelegion.probe", "Probe", "1.0")]
    public class ProbePlugin : BaseUnityPlugin
    {
        private float _acc;
        private int _frames;

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;

            if (dt > 0.15f)
                Logger.LogWarning($"[PROBE HITCH] {dt * 1000f:0} ms @ {Time.unscaledTime:0.0}s frame={Time.frameCount}");

            _acc += dt;
            _frames++;
            if (_acc >= 5f)
            {
                Logger.LogInfo($"[PROBE] fps={_frames / _acc:0.0}");
                _acc = 0f;
                _frames = 0;
            }
        }
    }
}
