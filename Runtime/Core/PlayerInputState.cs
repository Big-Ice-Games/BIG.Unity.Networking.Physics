using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Everything one player's controls say at one moment. This is the ONLY thing a client has to send
    /// to whoever simulates its avatar: the simulation reads this struct and never touches input devices.
    ///
    /// All fields are absolute states, not deltas or one-frame events, so a lost or repeated packet
    /// cannot desync anything; edges (jump pressed, hand clicked) are detected by the consumer.
    /// </summary>
    public struct PlayerInputState
    {
        /// <summary>Walk direction in the player's own frame: x = strafe, y = forward. Length 0..1.</summary>
        public Vector2 move;
        public bool sprint;
        public bool jumpHeld;

        /// <summary>View angles in degrees. Owned by the player's own machine so looking around never lags.</summary>
        public float yaw;
        public float pitch;

        public bool leftHandHeld;
        public bool rightHandHeld;

        /// <summary>How far in front of the eyes a tensed hand holds its object (metres).</summary>
        public float holdDistance;
    }
}
