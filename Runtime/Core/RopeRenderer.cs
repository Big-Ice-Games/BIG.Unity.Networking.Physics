using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Draws a chain of physics links as one smooth rope. The links stay invisible colliders;
    /// this runs a curve through their joints so there are no visible gaps or kinks.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class RopeRenderer : MonoBehaviour
    {
        [Tooltip("Chain links from top to bottom. The rope passes through the top end of each.")]
        public Transform[] links;
        [Tooltip("Local position of a link's top end (its joint to the link above).")]
        public Vector3 linkTop = new Vector3(0f, 0.25f, 0f);
        [Tooltip("What hangs at the bottom of the rope.")]
        public Transform end;
        [Tooltip("Local position on the end object where the rope is tied.")]
        public Vector3 endAnchor = new Vector3(0f, 0.5f, 0f);
        [Tooltip("Extra points drawn between each pair of joints. Higher = smoother curve.")]
        [Range(1, 8)] public int smoothness = 4;
        public float width = 0.06f;

        LineRenderer _line;
        Vector3[] _knots;

        void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.useWorldSpace = true;
        }

        void LateUpdate()
        {
            int knotCount = links.Length + (end != null ? 1 : 0);
            if (knotCount < 2)
            {
                _line.positionCount = 0;
                return;
            }
            if (_knots == null || _knots.Length != knotCount) _knots = new Vector3[knotCount];

            for (int i = 0; i < links.Length; i++)
                _knots[i] = links[i] != null ? links[i].TransformPoint(linkTop) : (i > 0 ? _knots[i - 1] : transform.position);
            if (end != null) _knots[knotCount - 1] = end.TransformPoint(endAnchor);

            // Catmull-Rom spline: passes exactly through every joint, rounding the corners between them.
            int segments = knotCount - 1;
            _line.positionCount = segments * smoothness + 1;
            _line.startWidth = _line.endWidth = width;
            int index = 0;
            for (int s = 0; s < segments; s++)
            {
                Vector3 p0 = _knots[Mathf.Max(s - 1, 0)];
                Vector3 p1 = _knots[s];
                Vector3 p2 = _knots[s + 1];
                Vector3 p3 = _knots[Mathf.Min(s + 2, knotCount - 1)];
                for (int i = 0; i < smoothness; i++)
                {
                    float t = i / (float)smoothness;
                    float t2 = t * t, t3 = t2 * t;
                    _line.SetPosition(index++, 0.5f * (2f * p1 + (p2 - p0) * t
                        + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                        + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                }
            }
            _line.SetPosition(index, _knots[knotCount - 1]);
        }
    }
}
