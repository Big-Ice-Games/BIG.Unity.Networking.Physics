using UnityEngine;
using UnityEngine.Events;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Gives any joint (or group of joints) a strength: a lamp's ceiling hook, a door hinge, a shelf bracket.
    ///
    /// Works like the hands' glue. Loads up to holdForce are carried forever, so the object can be
    /// bumped, swung and hung from. Pulling harder builds up strain (extra force x time); when the strain
    /// passes breakImpulse the joints are destroyed and the object comes free. Ease off and it recovers.
    /// </summary>
    public class BreakableMount : MonoBehaviour
    {
        [Tooltip("Joints that together hold this object up. Left empty = every joint on this GameObject.")]
        public Joint[] joints;

        [Tooltip("Load in newtons the mount carries forever (this includes the object's own weight: ~10 N per kg).")]
        public float holdForce = 800f;
        [Tooltip("How much over-pull it soaks up before tearing out, in newton-seconds. Higher = longer struggle.")]
        public float breakImpulse = 300f;
        [Tooltip("How fast it recovers when the load drops, in newton-seconds per second.")]
        public float recovery = 300f;

        [Tooltip("True where this object is simulated right now (always in single player; in multiplayer NetworkBreakableMount sets it). Display-only machines call Break() when told to.")]
        public bool simulate = true;

        public UnityEvent onBroken;

        /// <summary>Current load on the mount in newtons.</summary>
        public float Load { get; private set; }
        /// <summary>How close the mount is to tearing out (0..1).</summary>
        public float Strain { get; private set; }
        public bool Broken { get; private set; }

        float _strain, _settleTime;

        void Reset() => joints = GetComponents<Joint>();

        void Awake()
        {
            if (joints == null || joints.Length == 0) joints = GetComponents<Joint>();
        }

        void OnEnable() => _settleTime = 0.5f; // joints report junk forces while the scene settles

        void FixedUpdate()
        {
            if (Broken || !simulate) return;
            if (_settleTime > 0f)
            {
                _settleTime -= Time.fixedDeltaTime;
                return;
            }

            Vector3 total = Vector3.zero;
            bool anyAlive = false;
            foreach (Joint joint in joints)
            {
                if (joint == null) continue;
                total += joint.currentForce;
                anyAlive = true;
            }
            if (!anyAlive)
            {
                Broken = true;
                return;
            }

            Load = total.magnitude;
            float excess = Load - holdForce;
            if (excess > 0f) _strain += excess * Time.fixedDeltaTime;
            else _strain = Mathf.Max(0f, _strain - recovery * Time.fixedDeltaTime);
            Strain = Mathf.Clamp01(_strain / Mathf.Max(1f, breakImpulse));

            if (_strain > breakImpulse) Break();
        }

        public void Break()
        {
            if (Broken) return;
            Broken = true;
            Strain = 1f;
            foreach (Joint joint in joints)
                if (joint != null) Destroy(joint);
            onBroken?.Invoke();
        }
    }
}
