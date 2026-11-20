using UnityEngine;
using UnityEngine.Rendering;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Visual-only body of the alien (no colliders, no physics): a torso and legs that turn with the view's yaw, and a head
    /// on the camera pivot that also nods with the pitch. On the local avatar it only casts a shadow, so it never blocks
    /// the first-person view; everyone else sees it fully.
    /// </summary>
    public class PlayerBodyVisual : MonoBehaviour
    {
        public PhysicalPlayer controller;
        [Tooltip("Torso and legs; turned to the view's yaw.")]
        public Transform torso;
        [Tooltip("Every renderer of the body and head.")]
        public Renderer[] renderers;

        bool? _shownAsLocal;

        void LateUpdate()
        {
            torso.rotation = Quaternion.Euler(0f, controller.CurrentInput.yaw, 0f);

            if (_shownAsLocal == controller.isLocal) return;
            _shownAsLocal = controller.isLocal;
            var mode = controller.isLocal ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            foreach (var r in renderers) r.shadowCastingMode = mode;
        }
    }
}
