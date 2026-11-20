using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// A body made of several jointed rigidbodies (an NPC, a creature, a sack) that hands can grab by any part.
    /// It holds no AI: your brain component (e.g. a zombie) reads this and pushes the bodies around —
    /// <list type="bullet">
    /// <item><see cref="simulate"/> — run your forces only while true. Single player: always. Multiplayer:
    /// NetworkRagdoll sets it on the machine that simulates the body right now (the host while it roams, the grabbing
    /// player while it's held).</item>
    /// <item><see cref="IsHeld"/> / <see cref="SecondsSinceHeld"/> — any hand (here or remote) on any part.</item>
    /// <item><see cref="PartHit"/> — collisions of any part with something that isn't itself (damage, stun).</item>
    /// <item><see cref="Die"/> — final; decided where it's simulated, NetworkRagdoll tells everyone.</item>
    /// </list>
    /// </summary>
    [DisallowMultipleComponent]
    public class Ragdoll : MonoBehaviour
    {
        [Tooltip("The main body (torso / pelvis). Brains apply standing and walking forces here; the network asks it who simulates.")]
        public Rigidbody core;
        [Tooltip("True where this body is simulated right now. Single player: leave on. Multiplayer: set by NetworkRagdoll.")]
        public bool simulate = true;
        public UnityEvent onDied = new UnityEvent();

        /// <summary> A part hit something outside this ragdoll (only reported where it's simulated). </summary>
        public event Action<Collision, Rigidbody> PartHit;

        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        float _lastHeld = float.NegativeInfinity;

        public IReadOnlyList<Rigidbody> Bodies => _bodies;
        public float TotalMass { get; private set; }
        public bool Dead { get; private set; }
        /// <summary> At least one hand (of any player) holds a part right now. </summary>
        public bool IsHeld { get; private set; }
        public float SecondsSinceHeld => IsHeld ? 0f : Time.time - _lastHeld;

        void Reset() => core = GetComponentInChildren<Rigidbody>();

        void Awake()
        {
            GetComponentsInChildren(_bodies);
            TotalMass = 0f;
            foreach (Rigidbody body in _bodies)
            {
                TotalMass += body.mass;
                if (!body.TryGetComponent<RagdollPart>(out var part)) part = body.gameObject.AddComponent<RagdollPart>();
                part.ragdoll = this;
            }
            if (core == null && _bodies.Count > 0) core = _bodies[0];
        }

        void FixedUpdate()
        {
            IsHeld = false;
            foreach (Rigidbody body in _bodies)
                if (StretchyHand.IsHeld(body)) { IsHeld = true; break; }
            if (IsHeld) _lastHeld = Time.time;
        }

        /// <summary> Final. Display-only machines get it from the network (NetworkRagdoll). Repeats are ignored. </summary>
        public void Die()
        {
            if (Dead) return;
            Dead = true;
            onDied?.Invoke();
        }

        internal void ReportHit(Collision collision, Rigidbody part)
        {
            if (!simulate || collision.transform.IsChildOf(transform)) return; // own limbs knocking together
            PartHit?.Invoke(collision, part);
        }
    }
}
