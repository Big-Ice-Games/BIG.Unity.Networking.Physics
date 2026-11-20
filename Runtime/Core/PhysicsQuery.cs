using UnityEngine;

namespace BIG.Unity.Physical
{
    /// <summary>
    /// Raycasts that skip one rigidbody. Players live on a normal layer so they can grab, hit and stand on
    /// each other; each player's own queries (hand shots, arm wrapping, ground check) use these to ignore
    /// only that player's own body.
    /// </summary>
    public static class PhysicsQuery
    {
        static readonly RaycastHit[] Hits = new RaycastHit[32];

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float distance, int mask, Rigidbody ignore)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(count, ignore, out hit);
        }

        /// <summary>Raycast that skips every collider under the given root (a creature made of several bodies).</summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float distance, int mask, Transform ignoreRoot)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            hit = default;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = Hits[i];
                if (ignoreRoot != null && candidate.collider.transform.IsChildOf(ignoreRoot)) continue;
                if (found && candidate.distance >= hit.distance) continue;
                hit = candidate;
                found = true;
            }
            return found;
        }

        public static bool SphereCast(Vector3 origin, float radius, Vector3 direction, out RaycastHit hit, float distance, int mask, Rigidbody ignore)
        {
            int count = Physics.SphereCastNonAlloc(origin, radius, direction, Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(count, ignore, out hit);
        }

        static bool Nearest(int count, Rigidbody ignore, out RaycastHit hit)
        {
            hit = default;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = Hits[i];
                if (ignore != null && candidate.rigidbody == ignore) continue;
                // A sweep that starts already overlapping a collider reports it at distance 0 with no real
                // contact point. The single-hit casts skip those; do the same.
                if (candidate.distance <= 0f && candidate.point == Vector3.zero) continue;
                if (found && candidate.distance >= hit.distance) continue;
                hit = candidate;
                found = true;
            }
            return found;
        }
    }
}
