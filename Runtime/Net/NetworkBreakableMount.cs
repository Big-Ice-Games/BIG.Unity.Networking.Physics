using BIG.Unity.Physical;
using BIG.Unity.Networking;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// Network side of a <see cref="BreakableMount"/> (lamp hook, door hinge). Whoever simulates the object right now (the
    /// player holding it, or the host) measures the load and decides when it tears; the tear goes to everyone through the
    /// host (buffered — a player joining later sees it already broken).
    /// </summary>
    [RequireComponent(typeof(BreakableMount))]
    public sealed class NetworkBreakableMount : NetworkBaseBehaviour
    {
        /// <summary> After taking over the simulation, joints report junk forces for a moment — don't let them tear it. </summary>
        const float SettleAfterTakeover = 0.5f;

        BreakableMount _mount;
        NetworkBody _body;
        bool _sent;
        float _simulatingSince = -1f;

        protected override void Awake()
        {
            base.Awake();
            _mount = GetComponent<BreakableMount>();
            _body = GetComponent<NetworkBody>();
            _mount.simulate = false; // Until the network says who simulates.
        }

        void Update()
        {
            if (!IsClientInitialized && !IsServerInitialized) return;

            bool simulating = _body.IsSimulating;
            if (!simulating) _simulatingSince = -1f;
            else if (_simulatingSince < 0f) _simulatingSince = Time.time;
            _mount.simulate = simulating && Time.time - _simulatingSince > SettleAfterTakeover;

            if (!_mount.Broken || _sent) return;
            _sent = true;
            if (IsServerInitialized) ObserversBroken();
            else ServerBroken();
        }

        [FishNet.Object.ServerRpc(RequireOwnership = false)]
        void ServerBroken() => ObserversBroken();

        [FishNet.Object.ObserversRpc(BufferLast = true)]
        void ObserversBroken()
        {
            _sent = true;
            _mount.Break(); // Already broken where it tore — Break() ignores repeats.
        }
    }
}
