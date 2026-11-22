using System;
using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Puts this object on a physics hinge along one of its edges — a door, a gate, a hatch, a lid — and gives the hinge a
    /// strength (<see cref="BreakableMount"/>): push or pull it open with hands or body, or tear it off.
    /// Set the numbers and press <b>Build</b> in the inspector (or call <see cref="Build"/>).
    /// Networked: GameObject > BIG > Networking > Physics > Hinged (door).
    /// </summary>
    [DisallowMultipleComponent]
    public class BreakableHinge : MonoBehaviour
    {
        public enum Edge { Left, Right, Top, Bottom, Custom }

        [Header("Hinge")]
        [Tooltip("Which edge of the object (its local bounds) the hinge runs along. Left/Right: vertical axis, Top/Bottom: horizontal.")]
        public Edge edge = Edge.Left;
        [Tooltip("Custom edge: hinge point and axis in local space.")]
        public Vector3 customAnchor;
        public Vector3 customAxis = Vector3.up;
        public float minAngle = -110f;
        public float maxAngle = 110f;
        [Tooltip("Hinge friction (damper). 0 = swings freely.")]
        public float friction = 1.5f;
        [Tooltip("Mass of the moving part.")]
        public float mass = 12f;

        [Header("Mount (tear off the hinges)")]
        public bool breakable = true;
        [Tooltip("Load in newtons carried forever.")]
        public float holdForce = 2000f;
        [Tooltip("Over-pull soaked up before it tears off, in newton-seconds.")]
        public float breakImpulse = 120f;

        /// <summary> After every <see cref="Build"/>. </summary>
        public static event Action<BreakableHinge> Built;

        [SerializeField, HideInInspector] HingeJoint _hinge;
        [SerializeField, HideInInspector] BreakableMount _mount;

        public HingeJoint Hinge => _hinge;

        /// <summary> (Re)creates the Rigidbody settings, the HingeJoint and the mount. </summary>
        public void Build()
        {
            Kill(_mount);
            Kill(_hinge);

            if (!TryGetComponent<Rigidbody>(out var body)) body = gameObject.AddComponent<Rigidbody>();
            body.mass = mass;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            Bounds local = LocalBounds();
            Vector3 anchor, axis;
            switch (edge)
            {
                case Edge.Left: anchor = new Vector3(local.min.x, local.center.y, local.center.z); axis = Vector3.up; break;
                case Edge.Right: anchor = new Vector3(local.max.x, local.center.y, local.center.z); axis = Vector3.up; break;
                case Edge.Top: anchor = new Vector3(local.center.x, local.max.y, local.center.z); axis = Vector3.right; break;
                case Edge.Bottom: anchor = new Vector3(local.center.x, local.min.y, local.center.z); axis = Vector3.right; break;
                default: anchor = customAnchor; axis = customAxis; break;
            }

            _hinge = gameObject.AddComponent<HingeJoint>();
            _hinge.anchor = anchor;
            _hinge.axis = axis;
            _hinge.useLimits = true;
            _hinge.limits = new JointLimits { min = minAngle, max = maxAngle };
            _hinge.useSpring = friction > 0f;
            _hinge.spring = new JointSpring { spring = 0f, damper = friction }; // friction only, no self-closing

            if (breakable)
            {
                _mount = gameObject.AddComponent<BreakableMount>();
                _mount.joints = new Joint[] { _hinge };
                _mount.holdForce = holdForce;
                _mount.breakImpulse = breakImpulse;
            }
            Built?.Invoke(this);
        }

        /// <summary> Bounds of the object's own mesh / box collider in its local space. </summary>
        Bounds LocalBounds()
        {
            if (TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null) return filter.sharedMesh.bounds;
            if (TryGetComponent<BoxCollider>(out var box)) return new Bounds(box.center, box.size);
            return new Bounds(Vector3.zero, Vector3.one);
        }

        static void Kill(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }
    }
}
