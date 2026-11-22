using System;
using System.Collections.Generic;
using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// A rope (or chain) hanging from this object's position with something on its end — a lamp, a sandbag, a hook.
    /// Set the numbers and press <b>Build</b> in the inspector (or call <see cref="Build"/>): it creates the links (invisible
    /// capsules on ball joints, drawn as one smooth rope by <see cref="RopeRenderer"/>), ties the end object to them, adds a
    /// tether that carries the real load (the rope can't stretch like rubber however hard it's pulled) and a
    /// <see cref="BreakableMount"/> so a hard enough pull tears it off the ceiling.
    /// Hands stick to any link or to the end. Networked: GameObject > BIG > Networking > Physics > Hanging rope.
    /// </summary>
    [DisallowMultipleComponent]
    public class HangingRope : MonoBehaviour
    {
        [Header("Rope")]
        [Tooltip("From this object's position down to the end object, in metres.")]
        [Min(0.2f)] public float length = 5f;
        [Range(2, 40)] public int linkCount = 8;
        public float linkMass = 0.6f;
        public float linkRadius = 0.05f;
        [Tooltip("Ropes need extra solver passes to stay tight.")]
        public int solverIterations = 20;

        [Header("End")]
        [Tooltip("What hangs on the rope (needs a collider; gets a Rigidbody if it has none). Empty = a ball is created.")]
        public GameObject end;
        [Tooltip("Mass of the created ball (or of the end object if it has no Rigidbody yet).")]
        public float endMass = 5f;
        [Tooltip("Diameter of the created ball.")]
        public float endSize = 0.5f;

        [Header("Mount (tear off the ceiling)")]
        public bool breakable = true;
        [Tooltip("Load in newtons carried forever (includes the weight: ~10 N per kg).")]
        public float holdForce = 450f;
        [Tooltip("Over-pull soaked up before it tears out, in newton-seconds.")]
        public float breakImpulse = 150f;

        [Header("Look")]
        public Material ropeMaterial;
        public float ropeWidth = 0.06f;

        /// <summary> After every <see cref="Build"/> — editor tools re-add network components to the new links. </summary>
        public static event Action<HangingRope> Built;

        [SerializeField, HideInInspector] List<GameObject> _generatedObjects = new List<GameObject>();
        [SerializeField, HideInInspector] List<Component> _generatedComponents = new List<Component>();
        [SerializeField, HideInInspector] ConfigurableJoint _tether;
        [SerializeField, HideInInspector] Rigidbody[] _links = new Rigidbody[0];
        [SerializeField, HideInInspector] Rigidbody _endBody;

        /// <summary> Links from the top down. </summary>
        public IReadOnlyList<Rigidbody> Links => _links;
        /// <summary> The body on the end (given or created). </summary>
        public Rigidbody EndBody => _endBody;
        public bool IsBuilt => _links.Length > 0;

        void Awake()
        {
            // Built somewhere else and the whole rope moved since? The tether hangs from wherever the rope is now.
            if (_tether != null) _tether.connectedAnchor = transform.position;
        }

        /// <summary> (Re)creates links, joints, tether, mount and renderer. Previously generated parts are removed first. </summary>
        public void Build()
        {
            Clear();
            Vector3 top = transform.position;
            float linkLength = length / linkCount;

            Rigidbody previous = null;
            Joint topJoint = null;
            _links = new Rigidbody[linkCount];
            var linkTransforms = new Transform[linkCount];
            for (int i = 0; i < linkCount; i++)
            {
                var link = new GameObject($"Rope Link {i + 1}");
                _generatedObjects.Add(link);
                link.transform.SetParent(transform, false);
                link.transform.SetPositionAndRotation(top + Vector3.down * ((i + 0.5f) * linkLength), Quaternion.identity);

                var capsule = link.AddComponent<CapsuleCollider>();
                capsule.direction = 1;
                capsule.radius = linkRadius;
                capsule.height = linkLength;

                Rigidbody body = SwingingBody(link, linkMass);
                Joint joint = BallJoint(link, previous, new Vector3(0f, linkLength * 0.5f, 0f));
                if (i == 0) topJoint = joint;
                _links[i] = body;
                linkTransforms[i] = link.transform;
                previous = body;
            }

            // The end: given, or a ball. Tied at the top of its bounds, hanging right under the last link.
            GameObject endObject = end;
            if (endObject == null)
            {
                endObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                endObject.name = "Rope End";
                _generatedObjects.Add(endObject);
                endObject.transform.SetParent(transform, false);
                endObject.transform.localScale = Vector3.one * endSize;
                endObject.transform.position = top + Vector3.down * (length + endSize * 0.5f);
            }
            else
            {
                endObject.transform.SetParent(transform, true);
                Bounds bounds = WorldBounds(endObject);
                endObject.transform.position += top + Vector3.down * length - new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            }
            if (!endObject.TryGetComponent(out _endBody))
            {
                _endBody = SwingingBody(endObject, endMass);
                if (end != null) _generatedComponents.Add(_endBody);
            }
            _endBody.solverIterations = Mathf.Max(_endBody.solverIterations, solverIterations);

            Bounds endBounds = WorldBounds(endObject);
            Vector3 tieLocal = endObject.transform.InverseTransformPoint(new Vector3(endBounds.center.x, endBounds.max.y, endBounds.center.z));
            _generatedComponents.Add(BallJoint(endObject, previous, tieLocal));

            // Tether: a direct length limit from the end to the hook carries the real load and lets the mount read it.
            _tether = endObject.AddComponent<ConfigurableJoint>();
            _generatedComponents.Add(_tether);
            _tether.anchor = tieLocal;
            _tether.autoConfigureConnectedAnchor = false;
            _tether.connectedAnchor = top;
            _tether.xMotion = _tether.yMotion = _tether.zMotion = ConfigurableJointMotion.Limited;
            _tether.linearLimit = new SoftJointLimit { limit = length };

            if (breakable)
            {
                var mount = endObject.AddComponent<BreakableMount>();
                _generatedComponents.Add(mount);
                mount.joints = new[] { _tether, topJoint };
                mount.holdForce = holdForce;
                mount.breakImpulse = breakImpulse;
            }

            if (!TryGetComponent<RopeRenderer>(out var rope)) rope = gameObject.AddComponent<RopeRenderer>();
            var line = GetComponent<LineRenderer>();
            if (ropeMaterial != null) line.sharedMaterial = ropeMaterial;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            rope.links = linkTransforms;
            rope.linkTop = new Vector3(0f, linkLength * 0.5f, 0f);
            rope.end = endObject.transform;
            rope.endAnchor = tieLocal;
            rope.width = ropeWidth;

            Built?.Invoke(this);
        }

        /// <summary> Removes everything <see cref="Build"/> created (an end object you gave stays). </summary>
        public void Clear()
        {
            // Joints before the bodies they need.
            for (int i = _generatedComponents.Count - 1; i >= 0; i--) Kill(_generatedComponents[i]);
            foreach (var go in _generatedObjects) Kill(go);
            _generatedComponents.Clear();
            _generatedObjects.Clear();
            _links = new Rigidbody[0];
            _tether = null;
            _endBody = null;
        }

        Rigidbody SwingingBody(GameObject go, float mass)
        {
            var body = go.AddComponent<Rigidbody>();
            body.mass = mass;
            body.angularDamping = 0.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.solverIterations = solverIterations;
            return body;
        }

        /// <summary> Pins a point of this object to the previous body (or to the world), free to swing. </summary>
        static Joint BallJoint(GameObject go, Rigidbody connectedTo, Vector3 localAnchor)
        {
            var joint = go.AddComponent<ConfigurableJoint>();
            joint.connectedBody = connectedTo;
            joint.anchor = localAnchor;
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            return joint;
        }

        static Bounds WorldBounds(GameObject go)
        {
            var colliders = go.GetComponentsInChildren<Collider>();
            if (colliders.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            Bounds bounds = colliders[0].bounds;
            foreach (var collider in colliders) bounds.Encapsulate(collider.bounds);
            return bounds;
        }

        static void Kill(UnityEngine.Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, transform.position + Vector3.down * length);
            Gizmos.DrawWireSphere(transform.position, 0.08f);
        }
    }
}
