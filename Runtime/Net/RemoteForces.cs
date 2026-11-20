using BIG.Unity.Physical;
using System.Collections.Generic;
using BIG.Unity.Networking;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// Hand forces on bodies that ANOTHER machine simulates ("I hold your crate, you simulate it").
    /// <list type="bullet">
    /// <item>Outgoing (this machine's hands): every physics step <see cref="Add"/> accumulates impulse per body; once per
    /// network tick <see cref="TakeOutgoing"/> hands out the average force + where it acts, for NetworkPhysicalPlayer to send.</item>
    /// <item>Incoming (this machine simulates the body): <see cref="Receive"/> keeps the latest force per (sender, body) and
    /// applies it every physics step until it goes stale — the pull feels continuous between ticks, stops if the sender
    /// goes quiet. It also tells the body it's held (zombies go limp).</item>
    /// </list>
    /// </summary>
    public sealed class RemoteForces : MonoBehaviour
    {
        /// <summary> Incoming forces older than this stop being applied (sender let go / lost packets). </summary>
        const float Stale = 0.12f;

        public struct Outgoing
        {
            public NetworkBody Body;
            public Vector3 Force;
            public Vector3 LocalPoint;
        }

        struct Accumulated
        {
            public Vector3 Impulse;      // Σ F·dt
            public Vector3 WeightedPoint; // Σ |F|·dt · point (local)
            public float Weight;          // Σ |F|·dt
            public float Time;            // Σ dt
        }

        struct Incoming
        {
            public NetworkBody Body;
            public Vector3 Force;
            public Vector3 LocalPoint;
            public float Received;
        }

        static RemoteForces _instance;
        static readonly Dictionary<NetworkBody, Accumulated> Pending = new Dictionary<NetworkBody, Accumulated>();
        static readonly Dictionary<(int sender, NetworkBody body), Incoming> Active = new Dictionary<(int, NetworkBody), Incoming>();
        static readonly List<(int, NetworkBody)> Expired = new List<(int, NetworkBody)>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Pending.Clear();
            Active.Clear();
        }

        // ------------------------------------------------------------- outgoing

        /// <summary> A local hand pulls a remote body with <paramref name="force"/> at <paramref name="point"/> (one physics step). </summary>
        public static void Add(Rigidbody body, Vector3 force, Vector3 point)
        {
            if (!body.TryGetComponent<NetworkBody>(out var networkBody)) return;
            float dt = Time.fixedDeltaTime;
            float weight = force.magnitude * dt;
            Pending.TryGetValue(networkBody, out var sum);
            sum.Impulse += force * dt;
            sum.WeightedPoint += body.transform.InverseTransformPoint(point) * weight;
            sum.Weight += weight;
            sum.Time += dt;
            Pending[networkBody] = sum;
        }

        /// <summary> Average force per body since the last call (one network tick), then reset. </summary>
        public static void TakeOutgoing(List<Outgoing> into)
        {
            into.Clear();
            foreach (var pair in Pending)
            {
                var sum = pair.Value;
                if (pair.Key == null || sum.Time <= 0f) continue;
                into.Add(new Outgoing
                {
                    Body = pair.Key,
                    Force = sum.Impulse / sum.Time,
                    LocalPoint = sum.Weight > 0f ? sum.WeightedPoint / sum.Weight : Vector3.zero
                });
            }
            Pending.Clear();
        }

        // ------------------------------------------------------------- incoming

        /// <summary> This machine simulates <paramref name="body"/>: apply this sender's pull from now on (until stale). </summary>
        public static void Receive(int sender, NetworkBody body, Vector3 force, Vector3 localPoint)
        {
            if (body == null) return;
            Active[(sender, body)] = new Incoming { Body = body, Force = force, LocalPoint = localPoint, Received = Time.time };
            if (_instance == null)
            {
                var go = new GameObject("RemoteForces") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RemoteForces>();
            }
        }

        void FixedUpdate()
        {
            Expired.Clear();
            foreach (var pair in Active)
            {
                var entry = pair.Value;
                if (entry.Body == null || Time.time - entry.Received > Stale)
                {
                    Expired.Add(pair.Key);
                    continue;
                }

                var rigidbody = entry.Body.Rigidbody;
                if (!entry.Body.IsSimulating || rigidbody == null || rigidbody.isKinematic) continue; // Moved on — owner changed.
                rigidbody.AddForceAtPosition(entry.Force, entry.Body.transform.TransformPoint(entry.LocalPoint));
                StretchyHand.MarkHeldRemotely(rigidbody);
                entry.Body.KeepOwnership(); // Someone is pulling it — don't hand it back yet.
            }
            foreach (var key in Expired) Active.Remove(key);
        }
    }
}
