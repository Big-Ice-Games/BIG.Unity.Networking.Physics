using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Prototype readout for the avatar played on this machine: a crosshair, what you aim at (name + mass), each hand's
    /// state and pull, and the controls. IMGUI on purpose — a dev tool; a real game draws its own HUD from
    /// <see cref="StretchyHand"/> (CurrentState, Tense, Load, Strain, AttachedName). Remove the component to hide it.
    /// </summary>
    [RequireComponent(typeof(PlayerHands))]
    public class HandsDebugHud : MonoBehaviour
    {
        public bool showControls = true;

        PlayerHands _hands;
        string _lookingAt = "";
        static GUIStyle _centered;

        void Awake() => _hands = GetComponent<PlayerHands>();

        void Update()
        {
            _lookingAt = "";
            if (!_hands.IsLocal) return;
            var aim = _hands.aim;
            if (PhysicsQuery.Raycast(aim.position, aim.forward, out RaycastHit hit, _hands.settings.shootRange, _hands.settings.grabMask, _hands.body))
            {
                Rigidbody rb = hit.rigidbody;
                _lookingAt = rb != null ? $"{rb.name}  {rb.mass:0.#} kg" : $"{hit.collider.name}  (static)";
            }
        }

        void OnGUI()
        {
            if (!_hands.IsLocal) return; // one HUD per screen: only for the avatar played on this machine
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(cx - 2, cy - 2, 4, 4), Texture2D.whiteTexture);

            _centered ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(cx - 150, cy + 14, 300, 22), _lookingAt, _centered);
            GUI.Label(new Rect(12, Screen.height - 74, 420, 22), HandLine("LMB", _hands.left));
            GUI.Label(new Rect(12, Screen.height - 52, 420, 22), HandLine("RMB", _hands.right));
            if (showControls)
                GUI.Label(new Rect(12, Screen.height - 30, 620, 22),
                    $"hold distance {_hands.holdDistance:0.0} m (scroll)   |   WASD, Shift, Space   |   click = stick, hold = pull, let go = release");
        }

        string HandLine(string label, StretchyHand hand)
        {
            if (hand.CurrentState != StretchyHand.State.Attached) return $"{label}: {hand.CurrentState}";
            string mode = hand.Tense ? "PULL" : "slack";
            string glue = hand.Strain > 0.01f ? $"   glue peeling {hand.Strain * 100f:0}%" : "";
            return $"{label}: {hand.AttachedName}  {mode}  {hand.Load * _hands.settings.maxForce:0} / {_hands.settings.maxForce:0} N{glue}";
        }
    }
}
