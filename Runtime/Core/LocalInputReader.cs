using UnityEngine;
using UnityEngine.InputSystem;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Turns this machine's keyboard and mouse into a <see cref="PlayerInputState"/> and hands it to the avatar.
    /// Only the avatar controlled from this machine has one of these enabled. In multiplayer the same state
    /// is also what gets sent to the host; remote avatars get their state from the network instead.
    /// </summary>
    [RequireComponent(typeof(PhysicalPlayer))]
    public class LocalInputReader : MonoBehaviour
    {
        public float mouseSensitivity = 0.12f;
        public float maxPitch = 85f;

        PhysicalPlayer _controller;
        PlayerHands _hands;
        PlayerInputState _state;

        void Awake()
        {
            _controller = GetComponent<PhysicalPlayer>();
            _hands = GetComponent<PlayerHands>();
        }

        void OnEnable()
        {
            // Start from wherever the avatar is already looking and holding.
            _state = _controller.CurrentInput;
            SetCursorLocked(true);
        }

        void OnDisable() => SetCursorLocked(false);

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            else if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame) SetCursorLocked(true);

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                // Menu / alt-tab: stand still and let go of the buttons, but keep looking the same way.
                _state.move = Vector2.zero;
                _state.sprint = _state.jumpHeld = false;
                _state.leftHandHeld = _state.rightHandHeld = false;
                _controller.CurrentInput = _state;
                return;
            }

            Vector2 look = mouse.delta.ReadValue() * mouseSensitivity;
            _state.yaw += look.x;
            _state.pitch = Mathf.Clamp(_state.pitch - look.y, -maxPitch, maxPitch);

            float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            float y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            _state.move = Vector2.ClampMagnitude(new Vector2(x, y), 1f);
            _state.sprint = kb.leftShiftKey.isPressed;
            _state.jumpHeld = kb.spaceKey.isPressed;

            _state.leftHandHeld = mouse.leftButton.isPressed;
            _state.rightHandHeld = mouse.rightButton.isPressed;

            if (_hands != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) _state.holdDistance += Mathf.Sign(scroll) * _hands.scrollStep;
                _state.holdDistance = Mathf.Clamp(_state.holdDistance, _hands.minHoldDistance, _hands.maxHoldDistance);
            }

            _controller.CurrentInput = _state;
        }
    }
}
