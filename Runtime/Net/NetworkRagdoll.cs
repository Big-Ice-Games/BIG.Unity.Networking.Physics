using BIG.Unity.Networking;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// Network side of a <see cref="Ragdoll"/> (NPC / creature made of jointed bodies). The NetworkObject sits on the
    /// ragdoll root, every body part has NetworkTransform + NetworkBody + NetworkGrabbable (GameObject > BIG > Networking >
    /// Physics > Ragdoll NPC sets it all up).
    /// <list type="bullet">
    /// <item><see cref="Ragdoll.simulate"/> follows who simulates the core body: the host while it roams, the grabbing
    /// player while it's held — your brain component (AI forces) runs there and only there.</item>
    /// <item>Death is decided where it's simulated and sent to everyone through the host (buffered: late joiners see it dead).</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(Ragdoll))]
    public sealed class NetworkRagdoll : NetworkBaseBehaviour
    {
        Ragdoll _ragdoll;
        NetworkBody _core;
        bool _sent;

        protected override void Awake()
        {
            base.Awake();
            _ragdoll = GetComponent<Ragdoll>();
            _core = _ragdoll.core.GetComponent<NetworkBody>();
            _ragdoll.simulate = false; // Until the network says who simulates.
        }

        void Update()
        {
            if (!IsClientInitialized && !IsServerInitialized) return;
            _ragdoll.simulate = _core.IsSimulating;

            if (!_ragdoll.simulate || !_ragdoll.Dead || _sent) return;
            _sent = true;
            if (IsServerInitialized) ObserversDied();
            else ServerDied();
        }

        [FishNet.Object.ServerRpc(RequireOwnership = false)]
        void ServerDied() => ObserversDied();

        [FishNet.Object.ObserversRpc(BufferLast = true)]
        void ObserversDied()
        {
            _sent = true;
            _ragdoll.Die(); // Ignores repeats.
        }
    }
}
