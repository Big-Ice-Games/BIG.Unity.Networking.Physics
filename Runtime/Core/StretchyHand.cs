using System.Collections.Generic;
using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// One stretchy sticky hand. This component sits on the arm root (the "shoulder").
    ///
    /// Press while idle      -> shoot the hand where the camera looks; it sticks to what it hits.
    /// Quick click           -> the hand stays stuck but slack: no force until stretched past slackLength.
    /// Hold while attached   -> arm is tense: a clamped spring pulls the sticky end to the hold point.
    /// Let go of a hold      -> release, keeping the object's momentum (swing + let go = throw).
    ///
    /// The spring is capped at maxForce per hand, so weight matters: forces from several
    /// hands (or several players) on one object simply add up.
    /// </summary>
    public class StretchyHand : MonoBehaviour
    {
        public enum State { Idle, Flying, Attached, Retracting }

        public PlayerHands owner;
        [Tooltip("-1 for the left hand, +1 for the right hand.")]
        public float sideSign = 1f;
        [Tooltip("Where the hand rests when idle (child of the camera).")]
        public Transform rest;
        [Tooltip("The sticky blob. Must not be parented to the player.")]
        public Transform handVisual;
        public LineRenderer line;

        public State CurrentState { get; private set; } = State.Idle;
        public bool Tense { get; private set; }
        /// <summary>Last applied force as a fraction of maxForce (0..1).</summary>
        public float Load { get; private set; }
        /// <summary>How close the glue is to giving way (0..1).</summary>
        public float Strain { get; private set; }
        public string AttachedName => _anchorTransform != null ? _anchorTransform.name : "";

        // How many hands hold each rigidbody, so spring gains can be shared and stay stable.
        static readonly Dictionary<Rigidbody, int> GrabCounts = new Dictionary<Rigidbody, int>();
        // Bodies held by hands simulated on OTHER machines (the network says so), with when it last said it.
        static readonly Dictionary<Rigidbody, float> RemoteHolds = new Dictionary<Rigidbody, float>();
        const float RemoteHoldTimeout = 0.3f;

        /// <summary>True while at least one hand (of any player, here or remote) is stuck to this body.</summary>
        public static bool IsHeld(Rigidbody body) =>
            body != null && (GrabCounts.ContainsKey(body) ||
                             (RemoteHolds.TryGetValue(body, out float time) && Time.time - time < RemoteHoldTimeout));

        // ------------------------------------------------- multiplayer hooks (set by the network layer)

        /// <summary>
        /// True for a body that is kinematic HERE only because another machine simulates it. A hand stuck to it pulls it as
        /// if it were dynamic, but the force goes through <see cref="RemoteForce"/> instead of AddForce. Null = single player.
        /// </summary>
        public static System.Func<Rigidbody, bool> IsRemoteBody;

        /// <summary> Force (newtons, world) at a world point on a remote body, from a hand simulated here. Every physics step. </summary>
        public static System.Action<Rigidbody, Vector3, Vector3> RemoteForce;

        /// <summary> A hand simulated here stuck to / let go of a body (null = static geometry). </summary>
        public static event System.Action<StretchyHand, Rigidbody> Attached;
        public static event System.Action<StretchyHand, Rigidbody> Released;

        /// <summary> Network: a hand on another machine holds this body (refresh it while it does). </summary>
        public static void MarkHeldRemotely(Rigidbody body) => RemoteHolds[body] = Time.time;

        /// <summary> The body the hand is stuck to (null when not attached, or attached to static geometry). </summary>
        public Rigidbody AnchorBody => CurrentState == State.Attached ? _anchorBody : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            GrabCounts.Clear();
            RemoteHolds.Clear();
            IsRemoteBody = null;
            RemoteForce = null;
            Attached = null;
            Released = null;
        }

        const int LinePoints = 16;

        HandSettings S => owner.settings;

        bool _held, _pressedWhileAttached;
        float _attachTime, _strain, _gripMass;
        Vector3 _localWrist;

        Vector3 _handPos, _flyDir;
        Vector3 _prevAnchor, _remoteVelocity;   // Remote anchors: last step's sticky point and a smoothed velocity estimate.
        bool _hasPrevAnchor;
        float _flown;

        Transform _anchorTransform;
        Rigidbody _anchorBody;
        Vector3 _localAnchor, _localNormal;

        float _sag, _shownLoad;
        readonly List<Vector3> _corners = new List<Vector3>();
        MaterialPropertyBlock _mpb;
        Renderer _handRenderer;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            if (handVisual != null) _handRenderer = handVisual.GetComponentInChildren<Renderer>();
            if (line != null)
            {
                line.useWorldSpace = true;
                line.positionCount = LinePoints;
            }
            _handPos = rest.position;

            // The blob has to stay stuck to things while the player moves, so it cannot be the player's child.
            // Detaching it here lets the whole avatar, blobs included, live in one prefab for network spawning.
            if (handVisual != null && owner != null && handVisual.IsChildOf(owner.transform)) handVisual.SetParent(null, true);
        }

        void OnDisable() => Release(false);

        void OnDestroy()
        {
            if (handVisual != null) Destroy(handVisual.gameObject);
        }

        // -------------------------------------------------------------- network

        /// <summary>
        /// What other machines need in order to draw this hand. The simulating machine captures it,
        /// the network layer carries it (turning <see cref="anchor"/> into whatever object id it uses),
        /// and display-only machines apply it. Bends around corners are not sent: each machine
        /// recomputes them from the same geometry.
        /// </summary>
        public struct NetState
        {
            public State state;
            /// <summary>Object the hand is stuck to (the rigidbody's transform, or a static collider's). Null unless Attached.</summary>
            public Transform anchor;
            /// <summary>Sticky point and surface normal, in the anchor's local space.</summary>
            public Vector3 localAnchor, localNormal;
            /// <summary>World position of the hand while it is flying or retracting.</summary>
            public Vector3 handPosition;
            public bool tense;
            public float load, strain;
        }

        public NetState CaptureNetState()
        {
            return new NetState
            {
                state = CurrentState,
                anchor = CurrentState == State.Attached ? _anchorTransform : null,
                localAnchor = _localAnchor,
                localNormal = _localNormal,
                handPosition = _handPos,
                tense = Tense,
                load = Load,
                strain = Strain,
            };
        }

        /// <summary>Display-only machines: make this hand show the given state. Does nothing where the hand is simulated.</summary>
        public void ApplyNetState(in NetState net)
        {
            if (owner.Simulate) return;

            State target = net.state == State.Attached && net.anchor == null ? State.Retracting : net.state;

            if (target == State.Attached)
            {
                bool changed = CurrentState != State.Attached || net.anchor != _anchorTransform || net.localAnchor != _localAnchor;
                if (changed)
                {
                    _anchorTransform = net.anchor;
                    _anchorBody = net.anchor.GetComponent<Rigidbody>();
                    _localAnchor = net.localAnchor;
                    _localNormal = net.localNormal;
                    Vector3 point = _anchorTransform.TransformPoint(_localAnchor);
                    Vector3 normal = _anchorTransform.TransformDirection(_localNormal).normalized;
                    _localWrist = _anchorTransform.InverseTransformPoint(point + normal * S.wristLength);
                    _bends.Clear();
                    _prevShoulderFrame = null;
                    _prevNearBend = null;
                }
            }
            else
            {
                if (CurrentState == State.Attached)
                {
                    if (_anchorTransform != null) _handPos = _anchorTransform.TransformPoint(_localAnchor);
                    _bends.Clear();
                    _anchorTransform = null;
                    _anchorBody = null;
                }
                if (target == State.Flying)
                {
                    if (CurrentState == State.Idle) _handPos = rest.position;
                    Vector3 travelled = net.handPosition - _handPos;
                    if (travelled.sqrMagnitude > 0.000001f) _flyDir = travelled.normalized;
                    _handPos = net.handPosition;
                }
            }

            CurrentState = target;
            Tense = net.tense;
            Load = net.load;
            Strain = net.strain;
        }

        // ---------------------------------------------------------------- input

        /// <summary>Feed the current state of this hand's button. Call every frame.</summary>
        public void SetInput(bool held)
        {
            if (!owner.Simulate) return;
            if (held && !_held)
            {
                _pressedWhileAttached = CurrentState == State.Attached;
                if (CurrentState == State.Idle || CurrentState == State.Retracting) Shoot();
            }
            else if (!held && _held)
            {
                // Letting go of a pull drops/throws the object. The one exception is the shot itself:
                // a quick click that sticks the hand leaves it attached and slack.
                bool quickStick = !_pressedWhileAttached && Time.time - _attachTime < S.tapTime;
                if (CurrentState == State.Attached && !quickStick) Release(true);
            }
            _held = held;
        }

        void Shoot()
        {
            Transform aim = owner.aim;
            Vector3 target = aim.position + aim.forward * S.shootRange;
            if (PhysicsQuery.Raycast(aim.position, aim.forward, out RaycastHit hit, S.shootRange, S.grabMask, owner.body))
                target = hit.point;

            _handPos = rest.position;
            _flyDir = (target - _handPos).normalized;
            _flown = 0f;
            CurrentState = State.Flying;
        }

        void Attach(RaycastHit hit)
        {
            _anchorBody = hit.rigidbody;
            _anchorTransform = _anchorBody != null ? _anchorBody.transform : hit.collider.transform;
            _localAnchor = _anchorTransform.InverseTransformPoint(hit.point);
            _localNormal = _anchorTransform.InverseTransformDirection(hit.normal);
            if (_anchorBody != null)
            {
                GrabCounts.TryGetValue(_anchorBody, out int count);
                GrabCounts[_anchorBody] = count + 1;

                // A chain link weighs little on its own but drags the whole lamp behind it, so size the pull
                // for everything jointed to it. Capped so a light link is not shaken apart by a strong spring.
                _gripMass = Mathf.Min(JointedMass(_anchorBody), _anchorBody.mass * 3f);
            }
            // The wrist: the arm always leaves the sticky hand straight out of the surface.
            _localWrist = _anchorTransform.InverseTransformPoint(hit.point + hit.normal * S.wristLength);
            _attachTime = Time.time;
            _strain = 0f;
            _bends.Clear();
            _prevShoulderFrame = null;
            _prevNearBend = null;
            _hasPrevAnchor = false;
            CurrentState = State.Attached;
            Attached?.Invoke(this, _anchorBody);
        }

        /// <summary>Total mass of a body plus every movable body connected to it through joints.</summary>
        static float JointedMass(Rigidbody start)
        {
            Joint[] joints = FindObjectsByType<Joint>();
            if (joints.Length == 0) return start.mass;

            var cluster = new HashSet<Rigidbody> { start };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (Joint joint in joints)
                {
                    Rigidbody a = joint.GetComponent<Rigidbody>();
                    Rigidbody b = joint.connectedBody;
                    if (a == null || b == null || cluster.Contains(a) == cluster.Contains(b)) continue;
                    cluster.Add(a);
                    cluster.Add(b);
                    grew = true;
                }
            }

            // A remote object is kinematic here (another machine simulates it) — count it as the mass it really is.
            float mass = 0f;
            foreach (Rigidbody body in cluster)
                if (!body.isKinematic || start.isKinematic) mass += body.mass;
            return Mathf.Max(mass, start.mass);
        }

        public void Release(bool retract)
        {
            if (CurrentState == State.Attached)
            {
                if (owner.Simulate) Released?.Invoke(this, _anchorBody);
                if (_anchorTransform != null) _handPos = _anchorTransform.TransformPoint(_localAnchor);
                if (!ReferenceEquals(_anchorBody, null) && GrabCounts.TryGetValue(_anchorBody, out int count))
                {
                    if (count <= 1) GrabCounts.Remove(_anchorBody);
                    else GrabCounts[_anchorBody] = count - 1;
                }
            }
            _bends.Clear();
            _anchorTransform = null;
            _anchorBody = null;
            Tense = false;
            Load = 0f;
            _strain = 0f;
            Strain = 0f;
            CurrentState = retract && CurrentState != State.Idle ? State.Retracting : State.Idle;
        }

        // -------------------------------------------------------------- physics

        void FixedUpdate()
        {
            if (!owner.Simulate)
            {
                // Display only: no forces, just keep the arm wrapped around corners the same way the simulation does.
                if (CurrentState == State.Attached && _anchorTransform != null)
                    UpdateWrap(transform.position, _anchorTransform.TransformPoint(_localWrist));
                return;
            }

            if (CurrentState == State.Flying) StepFlight();
            else if (CurrentState == State.Attached) StepAttached();
        }

        void StepFlight()
        {
            float step = S.shootSpeed * Time.fixedDeltaTime;
            if (PhysicsQuery.SphereCast(_handPos, S.handRadius, _flyDir, out RaycastHit hit, step, S.grabMask, owner.body))
            {
                Attach(hit);
                return;
            }
            _handPos += _flyDir * step;
            _flown += step;
            if (_flown >= S.shootRange) CurrentState = State.Retracting;
        }

        void StepAttached()
        {
            if (_anchorTransform == null) // the thing we held was destroyed
            {
                Release(true);
                return;
            }

            Rigidbody player = owner.body;
            // Remote: kinematic here because another machine simulates it — pull it as a dynamic body, send the force there.
            bool remote = _anchorBody != null && _anchorBody.isKinematic && IsRemoteBody != null && IsRemoteBody(_anchorBody);
            bool dynamic = _anchorBody != null && (!_anchorBody.isKinematic || remote);
            Vector3 anchor = _anchorTransform.TransformPoint(_localAnchor);
            Vector3 wrist = _anchorTransform.TransformPoint(_localWrist);
            Vector3 shoulder = transform.position;

            // A remote body has no velocity here (moved by its synced transform) — estimate it from how the point moves.
            // (Its transform moves in Update, not per physics step, so raw deltas are uneven — smooth them a little.)
            if (remote && _hasPrevAnchor) _remoteVelocity = Vector3.Lerp(_remoteVelocity, (anchor - _prevAnchor) / Time.fixedDeltaTime, 0.3f);
            else _remoteVelocity = Vector3.zero;
            Vector3 remoteVelocity = _remoteVelocity;
            _prevAnchor = anchor;
            _hasPrevAnchor = true;

            UpdateWrap(shoulder, wrist);

            // The arm is a polyline: shoulder -> bends (last to first) -> wrist -> sticky hand.
            // Each end is pulled along its own first segment, like a rope running over corners.
            // Walk it once to get its length, how fast that length is changing, and the two end directions.
            int bendCount = _bends.Count;
            bool wrappedOnOther = false; // bent around something other than the movable object being held
            float length = Vector3.Distance(wrist, anchor);
            float lengthRate = 0f; // > 0 while the arm is getting longer
            Vector3 nearShoulder = wrist, nearWrist = shoulder;
            Vector3 previousPoint = shoulder, previousVelocity = player.linearVelocity;
            for (int i = bendCount - 1; i >= -1; i--)
            {
                Vector3 point, velocity;
                if (i >= 0)
                {
                    Bend bend = _bends[i];
                    point = bend.World;
                    velocity = bend.body != null && !bend.body.isKinematic ? bend.body.GetPointVelocity(point) : Vector3.zero;
                    if (!dynamic || bend.body != _anchorBody) wrappedOnOther = true;
                }
                else
                {
                    point = wrist;
                    velocity = remote ? remoteVelocity : dynamic ? _anchorBody.GetPointVelocity(wrist) : Vector3.zero;
                }

                Vector3 segment = point - previousPoint;
                float segmentLength = segment.magnitude;
                length += segmentLength;
                if (segmentLength > 0.0001f) lengthRate += Vector3.Dot(velocity - previousVelocity, segment / segmentLength);
                if (i == bendCount - 1) nearShoulder = point;
                if (i == -1) nearWrist = previousPoint;
                previousPoint = point;
                previousVelocity = velocity;
            }
            Vector3 pullPlayerDir = (nearShoulder - shoulder).normalized;
            Vector3 pullWristDir = (nearWrist - wrist).normalized;

            if (length > S.breakLength)
            {
                Release(true);
                return;
            }

            // Two bodies joined by a spring behave like one body of the "reduced" mass.
            // Scaling the spring by it keeps the same feel (and stability) for a 1 kg cube and a 200 kg crate.
            float mass = player.mass;
            if (dynamic)
            {
                GrabCounts.TryGetValue(_anchorBody, out int count);
                float objectMass = _gripMass / Mathf.Max(1, count);
                mass = objectMass * player.mass / (objectMass + player.mass);
            }

            Vector3 anchorVelocity = remote ? remoteVelocity : dynamic ? _anchorBody.GetPointVelocity(anchor) : Vector3.zero;
            Vector3 relativeVelocity = anchorVelocity - player.linearVelocity;
            float w = S.frequency;
            Tense = _held;

            // 1) Muscle: only while the button is held.
            Vector3 force = Vector3.zero;
            float tension = 0f; // pull that runs along the arm, around any bends
            if (Tense && wrappedOnOther)
            {
                // Wrapped around a wall or another object: the arm can only reel itself in along its path.
                float reel = mass * (w * w * (length - owner.holdDistance) + 2f * S.damping * w * lengthRate);
                tension = Mathf.Clamp(reel, 0f, S.maxForce);
            }
            else if (Tense)
            {
                // Free line of sight: pull the sticky end to the hold point in front of the camera.
                Vector3 error = owner.HoldPoint(sideSign) - anchor;
                force = mass * (w * w * error - 2f * S.damping * w * relativeVelocity);
                force = Vector3.ClampMagnitude(force, S.maxForce);
            }

            // The arm is jelly, not a pole: it can haul the player up towards the hand but cannot
            // push them up past it. Fade out the upward push on the player as the shoulder reaches hand height.
            Vector3 playerForce = -force;
            if (playerForce.y > 0f)
            {
                float room = anchor.y + S.liftAboveHand - shoulder.y; // how far the shoulder may still rise
                playerForce.y *= Mathf.Clamp01(room / Mathf.Max(0.01f, S.liftFade));
            }

            // 2) Arm length, always active: loose -> rubber -> rope. Acts along the arm and can only pull.
            float armTension = 0f;
            if (length > S.slackLength && length > 0.001f)
            {
                float separatingSpeed = lengthRate;
                float maxLength = Mathf.Max(S.maxLength, S.slackLength + 0.01f);

                // Rubber zone: force ramps from 0 at slackLength to elasticForce at maxLength.
                float stretch01 = Mathf.Clamp01((length - S.slackLength) / (maxLength - S.slackLength));
                float pull = S.elasticForce * Mathf.Pow(stretch01, Mathf.Max(0.1f, S.elasticCurve));
                pull += mass * 2f * S.damping * w * separatingSpeed * stretch01;

                // Rope zone: a stiff, strongly damped spring that stops the arm getting any longer.
                if (length > maxLength)
                {
                    float wr = S.ropeFrequency;
                    float rope = mass * (wr * wr * (length - maxLength) + 2f * wr * separatingSpeed);
                    pull += Mathf.Clamp(rope, 0f, S.ropeMaxForce);
                }

                if (pull > 0f) tension += pull;
                armTension = pull;
            }

            Vector3 wristForce = pullWristDir * tension;
            playerForce += pullPlayerDir * tension;
            Load = Mathf.Clamp01((force + wristForce).magnitude / S.maxForce);

            if (remote)
            {
                // Another machine simulates it: it applies the same pull there.
                RemoteForce?.Invoke(_anchorBody, force, anchor);
                RemoteForce?.Invoke(_anchorBody, wristForce, wrist);
            }
            else if (dynamic)
            {
                _anchorBody.AddForceAtPosition(force, anchor);
                _anchorBody.AddForceAtPosition(wristForce, wrist);
            }
            player.AddForce(playerForce * S.playerReaction);
            if (tension > 0f) PushBends(tension, shoulder, wrist);

            // Tug of war with the glue: below stickForce it holds forever. Pulling harder builds up strain
            // (extra force x time); the hand peels off once the strain passes stickImpulse, and recovers if you ease off.
            float excess = armTension - S.stickForce;
            if (excess > 0f) _strain += excess * Time.fixedDeltaTime;
            else _strain = Mathf.Max(0f, _strain - S.stickRecovery * Time.fixedDeltaTime);
            Strain = Mathf.Clamp01(_strain / Mathf.Max(1f, S.stickImpulse));
            if (_strain > S.stickImpulse) Release(true);
        }

        // ------------------------------------------------------------- wrapping

        /// <summary>A point where the arm bends around a corner, stored relative to the collider it rests on.</summary>
        class Bend
        {
            public Transform transform;
            public Rigidbody body;
            public Vector3 local;
            public Vector3 World => transform.TransformPoint(local);
        }

        // Ordered from the anchor towards the shoulder.
        readonly List<Bend> _bends = new List<Bend>();

        // Where each end segment started last step, remembered relative to the thing its far end sits on.
        // Working in that frame keeps the edge search exact even while a held object tumbles.
        Vector3 _prevShoulderLocal, _prevNearLocal;
        Transform _prevShoulderFrame;
        Bend _prevNearBend;

        const float CastMargin = 0.02f;

        /// <summary>Ray from one path point towards the next, stopping just short of it.</summary>
        bool Blocked(Vector3 from, Vector3 to, out RaycastHit hit)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude - CastMargin;
            if (distance <= 0.01f)
            {
                hit = default;
                return false;
            }
            // The arm never wraps around its own player's body.
            return PhysicsQuery.Raycast(from, delta / (distance + CastMargin), out hit, distance, S.grabMask, owner.body);
        }

        void UpdateWrap(Vector3 shoulder, Vector3 wrist)
        {
            if (!S.wrapAroundCorners)
            {
                _bends.Clear();
                return;
            }

            for (int i = _bends.Count - 1; i >= 0; i--)
                if (_bends[i].transform == null) _bends.RemoveAt(i); // collider was destroyed

            // Unwrap: drop the bend nearest an end as soon as that end can see past it.
            while (_bends.Count > 0)
            {
                Vector3 beyond = _bends.Count > 1 ? _bends[_bends.Count - 2].World : wrist;
                if (Blocked(shoulder, beyond, out _)) break;
                _bends.RemoveAt(_bends.Count - 1);
            }
            while (_bends.Count > 1)
            {
                if (Blocked(_bends[1].World, wrist, out _)) break;
                _bends.RemoveAt(0);
            }

            // Wrap: if an end segment is now cut by something, add a bend on the edge that cut it.
            if (_bends.Count < S.maxBends)
            {
                Bend last = _bends.Count > 0 ? _bends[_bends.Count - 1] : null;
                Transform frame = last != null ? last.transform : _anchorTransform;
                bool hasPrev = _prevShoulderFrame == frame;
                Vector3 prevShoulder = hasPrev ? frame.TransformPoint(_prevShoulderLocal) : shoulder;
                if (FindBend(prevShoulder, shoulder, last != null ? last.World : wrist, hasPrev, out Bend bend))
                    _bends.Add(bend);
            }
            if (_bends.Count > 0 && _bends.Count < S.maxBends)
            {
                // Same test from the other end, for when the held object swings behind something.
                Bend near = _bends[0];
                Vector3 from = near.World;
                bool hasPrev = ReferenceEquals(_prevNearBend, near);
                Vector3 prevFrom = hasPrev ? _anchorTransform.TransformPoint(_prevNearLocal) : from;
                if (FindBend(prevFrom, from, wrist, hasPrev, out Bend bend))
                    _bends.Insert(0, bend);
            }

            _prevShoulderFrame = _bends.Count > 0 ? _bends[_bends.Count - 1].transform : _anchorTransform;
            _prevShoulderLocal = _prevShoulderFrame.InverseTransformPoint(shoulder);
            _prevNearBend = _bends.Count > 0 ? _bends[0] : null;
            if (_prevNearBend != null) _prevNearLocal = _anchorTransform.InverseTransformPoint(_prevNearBend.World);
        }

        /// <summary>
        /// The segment from-to was clear last step and is blocked now. Bisect between the two steps
        /// to find the moment it first touched: at that moment the ray grazes the edge, so its hit point is the corner.
        /// </summary>
        bool FindBend(Vector3 prevFrom, Vector3 from, Vector3 to, bool hasPrev, out Bend bend)
        {
            bend = null;
            if (!Blocked(from, to, out RaycastHit hit)) return false;

            Vector3 outward = hit.normal;
            if (hasPrev && !Blocked(prevFrom, to, out _))
            {
                Vector3 clearFrom = prevFrom, clearTo = to;
                float lo = 0f, hi = 1f;
                for (int i = 0; i < 7; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 a = Vector3.Lerp(prevFrom, from, mid);
                    if (Blocked(a, to, out RaycastHit midHit))
                    {
                        hi = mid;
                        hit = midHit;
                    }
                    else
                    {
                        lo = mid;
                        clearFrom = a;
                    }
                }

                // The last clear line passes just outside the corner, so it tells us which way is "out".
                Vector3 along = clearTo - clearFrom;
                float t = along.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(hit.point - clearFrom, along) / along.sqrMagnitude) : 0f;
                Vector3 away = clearFrom + along * t - hit.point;
                if (away.sqrMagnitude > 0.000001f)
                {
                    Vector3 blended = away.normalized + hit.normal;
                    if (blended.sqrMagnitude > 0.01f) outward = blended.normalized;
                }
            }

            Vector3 position = hit.point + outward * S.wrapOffset;
            if ((position - to).sqrMagnitude < 0.0025f || (position - from).sqrMagnitude < 0.0025f) return false;

            Transform owningTransform = hit.rigidbody != null ? hit.rigidbody.transform : hit.collider.transform;
            bend = new Bend
            {
                transform = owningTransform,
                body = hit.rigidbody,
                local = owningTransform.InverseTransformPoint(position),
            };
            return true;
        }

        /// <summary>A taut arm presses on every corner it runs over; push dynamic objects it is wrapped around.</summary>
        void PushBends(float tension, Vector3 shoulder, Vector3 anchor)
        {
            for (int i = 0; i < _bends.Count; i++)
            {
                Bend bend = _bends[i];
                if (bend.body == null || bend.body.isKinematic) continue;
                Vector3 point = bend.World;
                Vector3 towardAnchor = (i > 0 ? _bends[i - 1].World : anchor) - point;
                Vector3 towardShoulder = (i < _bends.Count - 1 ? _bends[i + 1].World : shoulder) - point;
                bend.body.AddForceAtPosition((towardAnchor.normalized + towardShoulder.normalized) * tension, point);
            }
        }

        // -------------------------------------------------------------- visuals

        void LateUpdate()
        {
            Vector3 end;
            Vector3 up;
            switch (CurrentState)
            {
                case State.Attached when _anchorTransform != null:
                    end = _anchorTransform.TransformPoint(_localAnchor);
                    up = _anchorTransform.TransformDirection(_localNormal);
                    break;
                case State.Flying:
                    end = _handPos;
                    up = -_flyDir;
                    break;
                case State.Retracting:
                    _handPos = Vector3.MoveTowards(_handPos, rest.position, S.retractSpeed * Time.deltaTime);
                    if ((_handPos - rest.position).sqrMagnitude < 0.01f) CurrentState = State.Idle;
                    end = _handPos;
                    up = rest.up;
                    break;
                default:
                    end = rest.position;
                    up = rest.up;
                    break;
            }

            Vector3 start = transform.position;
            bool attached = CurrentState == State.Attached && _anchorTransform != null;
            bool wrapped = attached && _bends.Count > 0;

            // Corner points the arm passes through between the shoulder and the sticky hand.
            _corners.Clear();
            if (attached)
            {
                for (int i = _bends.Count - 1; i >= 0; i--)
                    if (_bends[i].transform != null) _corners.Add(_bends[i].World);
                _corners.Add(_anchorTransform.TransformPoint(_localWrist));
            }

            float length = 0f;
            Vector3 walk = start;
            for (int i = 0; i < _corners.Count; i++)
            {
                length += Vector3.Distance(walk, _corners[i]);
                walk = _corners[i];
            }
            length += Vector3.Distance(walk, end);

            // A loose arm droops by however much spare length it has; it straightens as it nears slackLength.
            bool slack = attached && !Tense && !wrapped && length < S.slackLength;
            float spare = slack ? Mathf.Sqrt(S.slackLength * S.slackLength - length * length) : 0f;
            float sagTarget = Mathf.Min(S.slackSag, spare * 0.4f);
            _sag = Mathf.Lerp(_sag, sagTarget, 1f - Mathf.Exp(-12f * Time.deltaTime));
            _shownLoad = Mathf.Lerp(_shownLoad, Tense ? Load : 0f, 1f - Mathf.Exp(-10f * Time.deltaTime));
            Color color = Color.Lerp(S.relaxedColor, S.strainedColor, _shownLoad);
            color = Color.Lerp(color, S.tearingColor, Strain); // glue about to give way

            if (line != null)
            {
                // First stretch (shoulder to the first corner) is a curve so it can droop; the rest is straight.
                Vector3 firstStop = _corners.Count > 0 ? _corners[0] : end;
                line.positionCount = LinePoints + _corners.Count; // remaining corners + the hand itself
                Vector3 control = (start + firstStop) * 0.5f + Vector3.down * (_sag * 2f);
                for (int i = 0; i < LinePoints; i++)
                {
                    float t = i / (float)(LinePoints - 1);
                    float u = 1f - t;
                    line.SetPosition(i, u * u * start + 2f * u * t * control + t * t * firstStop);
                }
                for (int i = 1; i < _corners.Count; i++) line.SetPosition(LinePoints + i - 1, _corners[i]);
                if (_corners.Count > 0) line.SetPosition(LinePoints + _corners.Count - 1, end);
                float width = Mathf.Lerp(S.thickWidth, S.thinWidth, Mathf.Clamp01(length / S.maxLength));
                line.startWidth = S.thickWidth;
                line.endWidth = width;
                _mpb.SetColor(BaseColorId, color);
                line.SetPropertyBlock(_mpb);
            }

            if (handVisual != null)
            {
                handVisual.position = end;
                if (up.sqrMagnitude > 0.0001f) handVisual.up = up;
                if (_handRenderer != null)
                {
                    _mpb.SetColor(BaseColorId, color);
                    _handRenderer.SetPropertyBlock(_mpb);
                }
            }
        }
    }
}
