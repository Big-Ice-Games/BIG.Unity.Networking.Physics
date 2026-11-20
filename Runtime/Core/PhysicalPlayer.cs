using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// First-person capsule driven purely by forces, so external forces (hands pulling,
    /// heavy objects dragging) blend naturally with walking.
    ///
    /// The body is a free rigidbody that keeps itself upright with a limited balancing torque,
    /// the way a person does. Anything that out-muscles that torque (a shove to the head, a teammate
    /// yanking it sideways, tripping at speed) tips it over for real; past <see cref="fallAngle"/> it
    /// has fallen, lies there like a prop for a moment, then pushes itself back up. (Only with <see cref="canFallOver"/>;
    /// off by default — then the body is always upright and hits don't move it.)
    /// Yaw and pitch of the view live on the camera pivot, not on the body.
    ///
    /// It never reads input devices: it acts on <see cref="CurrentInput"/>, which is written by
    /// <see cref="LocalInputReader"/> on the owner's machine or by the network layer everywhere else.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public class PhysicalPlayer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The eyes: rotated by the view angles. Also the aim for the hands, so it exists on every avatar.")]
        public Transform cameraPivot;

        [Header("Multiplayer roles")]
        [Tooltip("True on the avatar controlled from this machine: gets the camera, the HUD and the input reader.")]
        public bool isLocal = true;
        [Tooltip("True where this avatar's physics is computed (single player, or its owner in multiplayer). False on machines that only display it.")]
        public bool simulate = true;

        [Header("Movement")]
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 7f;
        [Tooltip("Max horizontal acceleration on the ground (m/s^2). Times mass = how hard the legs can push.")]
        public float groundAcceleration = 40f;
        [Tooltip("Max horizontal acceleration in the air (m/s^2).")]
        public float airAcceleration = 8f;
        public float jumpSpeed = 5f;
        [Tooltip("How hard the body alone can lean into something, in newtons. ~6 N slides 1 kg, so 90 N moves about 15 kg; heavier things need the hands.")]
        public float pushForce = 90f;
        public LayerMask groundMask = ~(1 << 2);

        [Header("Falling over")]
        [Tooltip("Off: the player is always upright — no tipping, staggering or knockdowns, and hits (zombie swats, Shove) do nothing. On: the full balance system below.")]
        public bool canFallOver = false;

        [Header("Balance")]
        [Tooltip("How eagerly the body rights itself when tilted.")]
        public float balanceSpring = 90f;
        [Tooltip("How quickly wobbling dies out.")]
        public float balanceDamping = 14f;
        [Tooltip("The most the body can do to stay upright (rad/s^2). Lower = easier to tip over. For scale: one hand pulling at head height is about 28, two hands about 56.")]
        public float balanceStrength = 45f;
        [Tooltip("Tilted further than this (degrees) the player has fallen over.")]
        public float fallAngle = 55f;

        [Header("Getting hit")]
        [Tooltip("A moving object that hits with at least this impulse (newton-seconds = kg x m/s) makes the player lose their footing for a moment. 50 is roughly a 15 kg crate thrown at 5 m/s.")]
        public float hitStaggerImpulse = 50f;
        [Tooltip("Slamming into the ground or a wall above this impulse does the same. 270 is roughly a 4 m fall.")]
        public float slamStaggerImpulse = 270f;
        [Tooltip("Seconds without footing after such a hit: no leg force and weakened balance, so the hit really carries the body.")]
        public float staggerTime = 0.4f;
        [Tooltip("Balance left while staggered (0 = none, 1 = full).")]
        [Range(0f, 1f)] public float staggerBalance = 0.3f;

        [Header("Fallen")]
        [Tooltip("Seconds spent lying there before trying to get up.")]
        public float knockdownTime = 1.5f;
        [Tooltip("Strength and damping of the get-up push.")]
        public float getUpSpring = 70f;
        public float getUpDamping = 12f;

        /// <summary>The controls this avatar is acting on right now.</summary>
        public PlayerInputState CurrentInput;

        public bool Grounded { get; private set; }
        /// <summary>True while the player has fallen over and is lying or rolling like a prop.</summary>
        public bool KnockedDown { get; private set; }

        const int IgnoreRaycastLayer = 2;

        Rigidbody _rb;
        CapsuleCollider _capsule;
        PhysicsMaterial _slippery, _grippy;
        float _knockTimer, _staggerTimer;
        bool _gettingUp, _jumpWasHeld;
        Vector3 _velocityBeforeStep;
        readonly Vector3[] _pushNormals = new Vector3[8];
        int _pushCount;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _capsule = GetComponent<CapsuleCollider>();
            _rb.freezeRotation = !canFallOver; // On: upright by balance torque, not by decree. Off: always upright.
            _rb.angularDamping = 0.5f;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            // Players used to hide on the Ignore Raycast layer so their own hands would not hit them.
            // Queries now skip the owner's body explicitly, so move to a normal layer: other players
            // can then grab this one, wrap arms around it and stand on it.
            if (gameObject.layer == IgnoreRaycastLayer)
                foreach (Transform t in GetComponentsInChildren<Transform>(true))
                    if (t.gameObject.layer == IgnoreRaycastLayer) t.gameObject.layer = 0;

            // Frictionless while standing: stopping is done by the movement force, not by friction,
            // so the capsule does not stick to walls or to carried objects.
            _slippery = new PhysicsMaterial("PlayerSlippery")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
            };
            // Once fallen the body is just a prop and should grip the floor like one.
            _grippy = new PhysicsMaterial("PlayerFallen") { dynamicFriction = 0.5f, staticFriction = 0.5f, bounciness = 0.1f };
            _capsule.sharedMaterial = _slippery;

            if (cameraPivot != null) CurrentInput.yaw = cameraPivot.eulerAngles.y;
            var hands = GetComponent<PlayerHands>();
            if (hands != null) CurrentInput.holdDistance = hands.holdDistance;
        }

        void Start() => ApplyRole();

        /// <summary>Call after changing <see cref="isLocal"/> (e.g. when the network layer assigns ownership).</summary>
        public void ApplyRole()
        {
            var reader = GetComponent<LocalInputReader>();
            if (isLocal && reader == null) reader = gameObject.AddComponent<LocalInputReader>();
            if (reader != null) reader.enabled = isLocal;

            if (cameraPivot != null)
            {
                if (cameraPivot.TryGetComponent(out Camera viewCamera)) viewCamera.enabled = isLocal;
                if (cameraPivot.TryGetComponent(out AudioListener listener)) listener.enabled = isLocal;
            }
        }

        void Update()
        {
            // Eyes follow the view angles on every machine, so hands aim the same way everywhere.
            // Set in world space: when the body tips, the eyes move with it but keep looking where the player looks.
            if (cameraPivot != null) cameraPivot.rotation = Quaternion.Euler(CurrentInput.pitch, CurrentInput.yaw, 0f);
        }

        void FixedUpdate()
        {
            if (!simulate) return;

            _velocityBeforeStep = _rb.linearVelocity;
            Grounded = CheckGrounded();
            if (_staggerTimer > 0f) _staggerTimer -= Time.fixedDeltaTime;
            bool staggered = _staggerTimer > 0f;

            if (canFallOver && !KnockedDown && Vector3.Angle(transform.up, Vector3.up) > fallAngle) Fall();

            if (KnockedDown)
            {
                StepFallen();
                _pushCount = 0;
                _jumpWasHeld = CurrentInput.jumpHeld;
                return;
            }

            // Balance: a spring towards upright, but only as strong as balanceStrength allows.
            float balance = staggered ? staggerBalance : 1f;
            TurnUpright(balanceSpring * balance, balanceDamping, balanceStrength * balance);

            Vector2 move = Vector2.ClampMagnitude(CurrentInput.move, 1f);
            Quaternion yawRot = Quaternion.Euler(0f, CurrentInput.yaw, 0f);
            Vector3 wishDir = yawRot * new Vector3(move.x, 0f, move.y);
            Vector3 velocity = _rb.linearVelocity;
            Vector3 horizontal = new Vector3(velocity.x, 0f, velocity.z);

            bool hasInput = wishDir.sqrMagnitude > 0.0001f;
            // In the air with no input we apply nothing, so swings and pulls are not damped.
            // Staggered: the legs are not under the body, so they neither drive nor brake.
            if (!staggered && (Grounded || hasInput))
            {
                Vector3 target = wishDir * (CurrentInput.sprint ? sprintSpeed : walkSpeed);
                Vector3 neededAccel = (target - horizontal) / Time.fixedDeltaTime;
                float maxAccel = Grounded ? groundAcceleration : airAcceleration;
                Vector3 accel = Vector3.ClampMagnitude(neededAccel, maxAccel);

                // The legs are strong enough to stop and turn on a dime, but a small alien cannot bulldoze:
                // whatever part of the drive goes straight into an obstacle is capped at pushForce.
                // (Against walls and ledges this also stops the player from tripping themselves over.)
                float maxPushAccel = pushForce / _rb.mass;
                for (int i = 0; i < _pushCount; i++)
                {
                    float into = -Vector3.Dot(accel, _pushNormals[i]);
                    if (into > maxPushAccel) accel += _pushNormals[i] * (into - maxPushAccel);
                }

                _rb.AddForce(accel, ForceMode.Acceleration);
            }
            _pushCount = 0;

            // Jump on the press, not while held.
            bool jumpHeld = CurrentInput.jumpHeld;
            if (jumpHeld && !_jumpWasHeld && Grounded && !staggered)
            {
                velocity = _rb.linearVelocity;
                velocity.y = jumpSpeed;
                _rb.linearVelocity = velocity;
            }
            _jumpWasHeld = jumpHeld;
        }

        // -------------------------------------------------------------- balance

        /// <summary>Spring torque towards upright. Returns how far from upright the body is, in degrees.</summary>
        float TurnUpright(float spring, float damping, float maxAcceleration)
        {
            Quaternion.FromToRotation(transform.up, Vector3.up).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (float.IsNaN(axis.x) || float.IsInfinity(axis.x)) axis = Vector3.zero;

            Vector3 torque = axis * (angle * Mathf.Deg2Rad * spring) - _rb.angularVelocity * damping;
            _rb.AddTorque(Vector3.ClampMagnitude(torque, maxAcceleration), ForceMode.Acceleration);
            return Mathf.Abs(angle);
        }

        /// <summary>
        /// A physical shove: an impulse (newton-seconds) applied at a point on the body. Hitting high
        /// tips the player over, hitting at the middle just pushes. Also costs them their footing for a moment.
        /// </summary>
        public void Shove(Vector3 impulse, Vector3 worldPoint)
        {
            if (!simulate || !canFallOver) return; // Off: hits (zombie swats) don't move the player at all.
            _rb.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
            Stagger();
        }

        /// <summary>World position of the top of the body, the natural place to shove it over.</summary>
        public Vector3 HeadPoint => transform.TransformPoint(_capsule.center + Vector3.up * (_capsule.height * 0.4f));

        /// <summary>Lose footing briefly: no leg force and weak balance, so whatever hit the player carries them.</summary>
        public void Stagger()
        {
            if (simulate && canFallOver) _staggerTimer = Mathf.Max(_staggerTimer, staggerTime);
        }

        void Fall()
        {
            KnockedDown = true;
            _gettingUp = false;
            _knockTimer = knockdownTime;
            _rb.angularDamping = 1f;
            _capsule.sharedMaterial = _grippy;
        }

        void StepFallen()
        {
            _knockTimer -= Time.fixedDeltaTime;

            if (!_gettingUp)
            {
                // Lie there for a moment, then wait until the body has more or less come to rest (2.5 s more at most).
                bool settled = _rb.linearVelocity.sqrMagnitude < 4f || _knockTimer < -2.5f;
                if (_knockTimer <= 0f && settled) _gettingUp = true;
                return;
            }

            // Getting up: push upright with everything, then hand back to normal balance.
            float tilt = TurnUpright(getUpSpring, getUpDamping, float.MaxValue);
            if (tilt > 12f) return;

            _rb.angularDamping = 0.5f;
            _capsule.sharedMaterial = _slippery;
            KnockedDown = false;
        }

        void OnCollisionEnter(Collision collision)
        {
            if (!simulate || KnockedDown || !canFallOver) return;

            float impulse = collision.impulse.magnitude;
            Rigidbody other = collision.rigidbody;

            if (other != null && !other.isKinematic)
            {
                // Only count the part of the crash the other object brought: running into a parked crate
                // should not stagger you, a crate flying into you should.
                float closingSpeed = collision.relativeVelocity.magnitude;
                float theirShare = closingSpeed > 0.1f ? Mathf.Clamp01(1f - _velocityBeforeStep.magnitude / closingSpeed) : 0f;
                if (impulse * theirShare > hitStaggerImpulse) Stagger();
            }
            else if (impulse > slamStaggerImpulse)
            {
                Stagger();
            }
        }

        // Remember what the body is leaning on, for the push cap in the next physics step.
        void OnCollisionStay(Collision collision)
        {
            if (!simulate) return;
            Rigidbody other = collision.rigidbody;
            if (other != null && other.isKinematic) return;

            for (int i = 0; i < collision.contactCount && _pushCount < _pushNormals.Length; i++)
            {
                Vector3 normal = collision.GetContact(i).normal; // points from the obstacle towards the player
                if (Mathf.Abs(normal.y) > 0.7f) continue;        // standing on it (or it sits on our head), not pushing
                normal.y = 0f;
                _pushNormals[_pushCount++] = normal.normalized;
            }
        }

        bool CheckGrounded()
        {
            float radius = _capsule.radius * 0.9f;
            float halfHeight = Mathf.Max(_capsule.height * 0.5f, _capsule.radius);
            Vector3 origin = transform.TransformPoint(_capsule.center);
            float distance = halfHeight - radius + 0.08f;
            return PhysicsQuery.SphereCast(origin, radius, Vector3.down, out _, distance, groundMask, _rb);
        }
    }
}
