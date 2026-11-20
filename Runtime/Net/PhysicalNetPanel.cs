using BIG.Unity.Physical;
using BIG.Unity.Networking;
using BIG.Unity.Networking.DevTools;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// Overlay module "Physics net": what THIS machine simulates (owner model), what your hands hold and who simulates it
    /// (you = no lag, else your pull goes there as a remote force), and for other players' hands: the anchor id, what it
    /// resolved to here and how far off that is (red = wrong object, drawn at the owner's point instead).
    /// </summary>
    public sealed class PhysicalNetPanel : DevPanel
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => DevOverlay.Register(new PhysicalNetPanel());

        public override string Title => "Physics net";
        public override int Order => 150;
        public override bool IsAvailable => Session.IsInGame;

        public override string Badge
        {
            get
            {
                int simulated = 0;
                foreach (var body in Object.FindObjectsByType<NetworkBody>())
                    if (body.IsSimulating && body.Rigidbody != null && !body.Rigidbody.isKinematic) simulated++;
                return $"simulating {simulated}";
            }
        }

        public override void Build(DevUI ui)
        {
            ui.Heading("YOUR HANDS");
            ui.Text(() => Holding(Local(), left: true));
            ui.Text(() => Holding(Local(), left: false));

            ui.Heading("OTHER PLAYERS' HANDS (as resolved here)");
            ui.List(() => Object.FindObjectsByType<NetworkPhysicalPlayer>(), player => player.ObjectId.ToString(), (row, player) =>
            {
                if (player.IsOwner) return;
                row.Text(player.GetComponent<NetworkPlayer>().DisplayName, DevUI.MUTED);
                row.Text(() => Line("L", player.LeftCheck), () => Style(player.LeftCheck));
                row.Text(() => Line("R", player.RightCheck), () => Style(player.RightCheck));
            }, "No players.");
        }

        PlayerHands Local() => Session.Players.Local != null ? Session.Players.Local.GetComponent<PlayerHands>() : null;

        string Holding(PlayerHands hands, bool left)
        {
            if (hands == null) return "-";
            var hand = left ? hands.left : hands.right;
            string side = left ? "L" : "R";
            if (hand.CurrentState != StretchyHand.State.Attached) return $"{side}: {hand.CurrentState}";
            var body = hand.AnchorBody;
            if (body == null) return $"{side}: {hand.AttachedName} (static)";
            if (!body.TryGetComponent<NetworkBody>(out var networkBody)) return $"{side}: {body.name} (not networked)";
            string who = networkBody.IsSimulating ? "YOU simulate it — no lag"
                : networkBody.IsTaken ? "another player simulates it — your pull is sent there"
                : "the host simulates it — your pull is sent there (taking it over...)";
            return $"{side}: {body.name} — {who}";
        }

        static string Line(string side, NetworkPhysicalPlayer.HandCheck check)
        {
            if (check.State != StretchyHand.State.Attached) return $"{side}: {check.State}";
            return $"{side}: {check.AnchorId} → {check.ResolvedTo}, {check.Mismatch:0.00} m{(check.UsedFallback ? "  (WRONG — drawn at owner point)" : "")}";
        }

        static string Style(NetworkPhysicalPlayer.HandCheck check) =>
            check.State != StretchyHand.State.Attached ? DevUI.MUTED : check.UsedFallback ? DevUI.BAD : DevUI.GOOD;
    }
}
