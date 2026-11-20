using BIG.Unity.Physical;
using System.Collections.Generic;
using BIG.Unity.Networking;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary> One hand's <see cref="StretchyHand.NetState"/> in a form that travels: the anchor as a <see cref="NetAnchors"/> id. </summary>
    public struct HandWire
    {
        public byte State;
        public string Anchor;
        public Vector3 LocalAnchor;
        public Vector3 LocalNormal;
        public Vector3 HandPosition;
        public bool Tense;
        public byte Load;   // 0..1 → 0..255 (arm colour only)
        public byte Strain;
        /// <summary> Owner: where the hand IS in the world (sticky point when attached) — others check their anchor against it. </summary>
        public Vector3 WorldPoint;
    }

    /// <summary>
    /// The player avatar on the network — "everyone simulates themselves and what they hold":
    /// <list type="bullet">
    /// <item>The OWNER simulates its body and both hands (no lag for your own movement and pulls). Body sync: its
    /// NetworkBody (owner authority, owner physics). Others see it interpolated.</item>
    /// <item>Owner → host → others, every tick: both hands' state + the view (yaw, pitch, hold distance), so remote arms,
    /// eyes and hand rest poses look right.</item>
    /// <item>A hand sticking to a free networked body asks for its ownership (NetworkGrabbable) — then you simulate it too.</item>
    /// <item>A hand on a body someone else simulates: its pull goes to them (<see cref="RemoteForces"/>, routed by the host).</item>
    /// </list>
    /// </summary>
    [RequireComponent(typeof(PhysicalPlayer), typeof(PlayerHands))]
    public sealed class NetworkPhysicalPlayer : NetworkBaseBehaviour
    {
        PhysicalPlayer _controller;
        PlayerHands _hands;
        readonly List<RemoteForces.Outgoing> _outgoing = new List<RemoteForces.Outgoing>();
        readonly List<NetworkBody> _keep = new List<NetworkBody>();

        #region Hooks into the physics (installed once)

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallHooks()
        {
            // Kinematic here only because another machine simulates it → pull it, send the force there.
            StretchyHand.IsRemoteBody = body => body.TryGetComponent<NetworkBody>(out var networkBody) &&
                                                (networkBody.IsClientInitialized || networkBody.IsServerInitialized) &&
                                                !networkBody.IsSimulating;
            StretchyHand.RemoteForce = RemoteForces.Add;

            // Grabbed something free that the host simulates → ask to simulate it ourselves (no lag on what we hold).
            StretchyHand.Attached += (hand, body) =>
            {
                if (body == null || !body.TryGetComponent<NetworkBody>(out var networkBody)) return;
                if (!networkBody.IsSimulating && !networkBody.IsTaken && networkBody.CanBeTaken) networkBody.RequestOwnership();
            };
        }

        #endregion

        #region Lifecycle and roles

        protected override void Awake()
        {
            base.Awake();
            _controller = GetComponent<PhysicalPlayer>();
            _hands = GetComponent<PlayerHands>();
        }

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            TimeManager.OnTick += OnTick;
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();
            TimeManager.OnTick -= OnTick;
        }

        protected override void OnNetworkReady(NetworkRole role) => ApplyRole(role);
        protected override void OnOwnershipChanged(NetworkRole role) => ApplyRole(role);

        void ApplyRole(NetworkRole role)
        {
            _controller.isLocal = role.IsOwner;   // camera, audio listener, input reader, HUD
            _controller.simulate = role.IsOwner;  // forces, hands, balance — the owner
            _controller.ApplyRole();
        }

        #endregion

        #region Owner: send

        void FixedUpdate()
        {
            if (!IsOwner) return;
            // Holding something we simulate: keep it (every body of its object — a lamp, a zombie) until we let go.
            KeepHeld(_hands.left);
            KeepHeld(_hands.right);
        }

        void KeepHeld(StretchyHand hand)
        {
            var body = hand.AnchorBody;
            if (body == null || !body.TryGetComponent<NetworkBody>(out var networkBody) || !networkBody.IsOwner) return;
            networkBody.NetworkObject.GetComponentsInChildren(_keep);
            foreach (var part in _keep) part.KeepOwnership();
        }

        void OnTick()
        {
            if (!IsOwner) return;

            var input = _controller.CurrentInput;
            var left = Wire(_hands.left);
            var right = Wire(_hands.right);
            if (IsServerInitialized) ObserversHands(left, right, input.yaw, input.pitch, input.holdDistance);
            else ServerHands(left, right, input.yaw, input.pitch, input.holdDistance);

            RemoteForces.TakeOutgoing(_outgoing);
            foreach (var force in _outgoing)
            {
                if (IsServerInitialized) Deliver(force.Body, force.Force, force.LocalPoint);
                else ServerForce(force.Body, force.Force, force.LocalPoint);
            }
        }

        #endregion

        #region Host: relay

        [FishNet.Object.ServerRpc]
        void ServerHands(HandWire left, HandWire right, float yaw, float pitch, float holdDistance, Channel channel = Channel.Unreliable)
        {
            Show(left, right, yaw, pitch, holdDistance); // The host's own screen.
            ObserversHands(left, right, yaw, pitch, holdDistance);
        }

        [FishNet.Object.ServerRpc]
        void ServerForce(NetworkBody body, Vector3 force, Vector3 localPoint, Channel channel = Channel.Unreliable) =>
            Deliver(body, force, localPoint);

        /// <summary> Host: a pull from this player's hands on <paramref name="body"/> — to whoever simulates it. </summary>
        void Deliver(NetworkBody body, Vector3 force, Vector3 localPoint)
        {
            if (body == null) return;
            if (body.IsSimulating) RemoteForces.Receive(ObjectId, body, force, localPoint); // The host has it.
            else if (body.Owner.IsValid) TargetForce(body.Owner, body, force, localPoint);
        }

        [FishNet.Object.TargetRpc]
        void TargetForce(NetworkConnection target, NetworkBody body, Vector3 force, Vector3 localPoint, Channel channel = Channel.Unreliable) =>
            RemoteForces.Receive(ObjectId, body, force, localPoint);

        #endregion

        #region Others: show

        [FishNet.Object.ObserversRpc(ExcludeOwner = true, ExcludeServer = true)]
        void ObserversHands(HandWire left, HandWire right, float yaw, float pitch, float holdDistance, Channel channel = Channel.Unreliable) =>
            Show(left, right, yaw, pitch, holdDistance);

        void Show(HandWire left, HandWire right, float yaw, float pitch, float holdDistance)
        {
            if (IsOwner) return;
            var input = _controller.CurrentInput;
            input.yaw = yaw;
            input.pitch = pitch;
            input.holdDistance = holdDistance;
            _controller.CurrentInput = input;

            LeftCheck = Apply(_hands.left, left);
            RightCheck = Apply(_hands.right, right);
        }

        #endregion

        #region Hand state ⇄ wire (with diagnostics)

        /// <summary> Diagnostics of one hand on a display-only machine (shown by the overlay's "Physics net" module). </summary>
        public struct HandCheck
        {
            public StretchyHand.State State;
            public string AnchorId;
            public string ResolvedTo;
            /// <summary> Metres between the owner's sticky point and where this machine's anchor puts it (-1 = not attached). </summary>
            public float Mismatch;
            public bool UsedFallback;
        }

        /// <summary>
        /// Further than this from the owner's point, a resolved anchor is the WRONG object — draw the hand at the owner's point.
        /// (Lag alone puts a moving object's interpolated point behind the owner's current one, so this must be generous.)
        /// </summary>
        const float AnchorTolerance = 3f;
        static readonly HashSet<string> Warned = new HashSet<string>();
        static readonly HashSet<string> Trusted = new HashSet<string>();

        public HandCheck LeftCheck { get; private set; }
        public HandCheck RightCheck { get; private set; }

        static HandWire Wire(StretchyHand hand)
        {
            var state = hand.CaptureNetState();
            bool attached = state.state == StretchyHand.State.Attached && state.anchor != null;
            return new HandWire
            {
                State = (byte)state.state,
                Anchor = attached ? NetAnchors.IdOf(state.anchor) : "",
                LocalAnchor = state.localAnchor,
                LocalNormal = state.localNormal,
                HandPosition = state.handPosition,
                Tense = state.tense,
                Load = (byte)(Mathf.Clamp01(state.load) * 255f),
                Strain = (byte)(Mathf.Clamp01(state.strain) * 255f),
                WorldPoint = attached ? state.anchor.TransformPoint(state.localAnchor) : state.handPosition
            };
        }

        HandCheck Apply(StretchyHand hand, HandWire wire)
        {
            var check = new HandCheck { State = (StretchyHand.State)wire.State, AnchorId = wire.Anchor, Mismatch = -1f };
            var state = new StretchyHand.NetState
            {
                state = (StretchyHand.State)wire.State,
                localAnchor = wire.LocalAnchor,
                localNormal = wire.LocalNormal,
                handPosition = wire.HandPosition,
                tense = wire.Tense,
                load = wire.Load / 255f,
                strain = wire.Strain / 255f
            };

            if (state.state == StretchyHand.State.Attached)
            {
                var anchor = NetAnchors.Resolve(wire.Anchor);
                check.ResolvedTo = anchor != null ? anchor.name : "(nothing)";
                check.Mismatch = anchor != null ? Vector3.Distance(anchor.TransformPoint(wire.LocalAnchor), wire.WorldPoint) : float.PositiveInfinity;

                // An id that once matched closely stays trusted — no flapping between attached and fallback while it swings.
                bool trusted = anchor != null && (Trusted.Contains(wire.Anchor) || check.Mismatch <= AnchorTolerance);
                if (anchor != null && check.Mismatch <= 1f) Trusted.Add(wire.Anchor);
                if (trusted)
                {
                    state.anchor = anchor;
                }
                else
                {
                    // Wrong / unknown object here: show the hand where the owner has it instead of on the wrong thing.
                    check.UsedFallback = true;
                    state.state = StretchyHand.State.Flying;
                    state.handPosition = wire.WorldPoint;
                    if (Warned.Add(wire.Anchor))
                        Debug.LogWarning($"[NetworkPhysicalPlayer] {name}: hand anchor '{wire.Anchor}' resolved to '{check.ResolvedTo}', " +
                                         $"{check.Mismatch:0.00} m from the owner's point {wire.WorldPoint}.");
                }
            }

            hand.ApplyNetState(in state);
            return check;
        }

        #endregion
    }
}
