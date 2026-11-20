using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary> On each body of a <see cref="Ragdoll"/> (added automatically): forwards its collisions. </summary>
    [DisallowMultipleComponent]
    public class RagdollPart : MonoBehaviour
    {
        public Ragdoll ragdoll;
        Rigidbody _body;

        void Awake() => _body = GetComponent<Rigidbody>();

        void OnCollisionEnter(Collision collision)
        {
            if (ragdoll != null) ragdoll.ReportHit(collision, _body);
        }
    }
}
