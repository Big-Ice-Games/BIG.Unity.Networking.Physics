using System;
using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>Tuning shared by both hands. Lives on <see cref="PlayerHands"/> so there is one place to tweak.</summary>
    [Serializable]
    public class HandSettings
    {
        [Header("Shooting")]
        public float shootSpeed = 45f;
        [Tooltip("How far the hand flies. Keep it at or below maxLength, otherwise the arm yanks back right after sticking.")]
        public float shootRange = 2.5f;
        public float handRadius = 0.08f;
        public float retractSpeed = 30f;
        [Tooltip("What the hand can stick to. A hand always skips its own player's body, whatever this says.")]
        public LayerMask grabMask = ~(1 << 2);

        [Header("Input")]
        [Tooltip("After a shot sticks, letting go within this time leaves the hand attached and slack. Holding longer counts as a pull, and letting go then releases.")]
        public float tapTime = 0.25f;

        [Header("Tension (button held)")]
        [Tooltip("Max pull of ONE hand in newtons. ~10 N lifts 1 kg, so 250 N lifts about 25 kg.")]
        public float maxForce = 250f;
        [Tooltip("Spring frequency in rad/s. Higher = snappier, lower = more rubbery.")]
        public float frequency = 8f;
        [Tooltip("1 = no bounce, lower = wobbly jelly.")]
        public float damping = 0.6f;
        [Tooltip("How much of the hand force pushes back on the player (1 = physically correct).")]
        [Range(0f, 1f)] public float playerReaction = 1f;
        public float holdSideOffset = 0.3f;

        [Header("Arm length (applies whether the button is held or not)")]
        [Tooltip("Up to this length the arm hangs loose and applies no force.")]
        public float slackLength = 2f;
        [Tooltip("Between slackLength and this the arm stretches like rubber. At this length it goes taut like a rope.")]
        public float maxLength = 2.5f;
        [Tooltip("Rubber pull in newtons when stretched all the way to maxLength.")]
        public float elasticForce = 150f;
        [Tooltip("Shape of the rubber zone: 1 = force grows evenly, 2+ = soft at first and firm near the end.")]
        public float elasticCurve = 1.5f;
        [Tooltip("Strongest pull of the taut rope in newtons. Higher = harder limit.")]
        public float ropeMaxForce = 1500f;
        [Tooltip("Stiffness of the taut rope in rad/s. Higher = less give past maxLength.")]
        public float ropeFrequency = 25f;
        [Header("Glue (tug of war)")]
        [Tooltip("Pull in newtons the glue holds forever. Only pulling harder than this starts to peel the hand off.")]
        public float stickForce = 600f;
        [Tooltip("How much over-pull the glue soaks up before letting go, in newton-seconds. E.g. 300 = pulling 600 N too hard for half a second. Higher = longer struggle.")]
        public float stickImpulse = 300f;
        [Tooltip("How fast the glue recovers when you ease off, in newton-seconds per second.")]
        public float stickRecovery = 400f;

        [Header("Wrist")]
        [Tooltip("The arm leaves the sticky hand straight out of the surface for this distance before heading to the shoulder.")]
        public float wristLength = 0.12f;

        [Header("Wrapping around corners")]
        public bool wrapAroundCorners = true;
        [Tooltip("How far off the surface a bend point sits. Too small and the arm clips into edges.")]
        public float wrapOffset = 0.03f;
        [Tooltip("Most corners one arm can be wrapped around at once.")]
        public int maxBends = 8;

        [Header("Pulling yourself up")]
        [Tooltip("How far above its own hand the shoulder can be pulled (0 = up to hand height, negative = hangs lower).")]
        public float liftAboveHand = 0f;
        [Tooltip("Distance over which the upward pull fades out near that height.")]
        public float liftFade = 0.25f;

        [Tooltip("Safety: arm snaps off if something still drags it past this length.")]
        public float breakLength = 4f;

        [Header("Visuals")]
        public float slackSag = 0.8f;
        public float thickWidth = 0.09f;
        public float thinWidth = 0.03f;
        public Color relaxedColor = new Color(0.35f, 0.9f, 0.2f);
        public Color strainedColor = new Color(1f, 0.55f, 0.1f);
        [Tooltip("Colour the arm turns as the glue is about to give way.")]
        public Color tearingColor = new Color(1f, 0.1f, 0.1f);
    }

    /// <summary>
    /// Owns the two hands of one avatar: feeds them the hand buttons from the avatar's <see cref="PlayerInputState"/>
    /// and holds their shared tuning. The on-screen readout is a separate, optional <see cref="HandsDebugHud"/>.
    /// </summary>
    public class PlayerHands : MonoBehaviour
    {
        public Rigidbody body;
        [Tooltip("Where the hands aim from: the eyes / camera pivot.")]
        public Transform aim;
        public StretchyHand left;
        public StretchyHand right;
        public HandSettings settings = new HandSettings();

        [Header("Hold point (scroll wheel)")]
        public float holdDistance = 1.5f;
        public float minHoldDistance = 0.8f;
        public float maxHoldDistance = 2f;
        public float scrollStep = 0.15f;

        PhysicalPlayer _controller;

        /// <summary>False on machines that only display this avatar: its hands then just mirror <see cref="StretchyHand.ApplyNetState"/>.</summary>
        public bool Simulate => _controller == null || _controller.simulate;
        /// <summary>The avatar is played on this machine (camera, HUD).</summary>
        public bool IsLocal => _controller == null || _controller.isLocal;

        void Awake() => _controller = GetComponent<PhysicalPlayer>();

        /// <summary>World point a tensed hand tries to bring its sticky end to.</summary>
        public Vector3 HoldPoint(float sideSign)
        {
            return aim.position + aim.forward * holdDistance + aim.right * (sideSign * settings.holdSideOffset);
        }

        void Update()
        {
            if (_controller == null || !Simulate) return;
            // Same path for the local player and for avatars driven by the network.
            PlayerInputState input = _controller.CurrentInput;
            holdDistance = Mathf.Clamp(input.holdDistance, minHoldDistance, maxHoldDistance);
            left.SetInput(input.leftHandHeld);
            right.SetInput(input.rightHandHeld);
        }
    }
}
