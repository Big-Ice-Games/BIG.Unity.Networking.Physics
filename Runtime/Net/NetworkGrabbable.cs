using BIG.Unity.Physical;
using BIG.Unity.Networking;
using UnityEngine;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// On every networked rigidbody players can grab (props, chain links, door, zombie parts — never avatars): a hand
    /// sticking to it may take its object's ownership, so the grabbing player simulates it with no lag. Granted only while
    /// nobody else holds it (NetworkBody); a second grabber pulls through remote forces instead. The body's profile
    /// (Auto Return To Host) gives it back to the host once it rests after the last hand let go.
    /// </summary>
    [RequireComponent(typeof(NetworkBody))]
    public sealed class NetworkGrabbable : MonoBehaviour, IOwnershipRule
    {
        [Tooltip("Host refuses grabs from avatars farther than this (lag guard; hands reach ~2.5 m).")]
        public float maxDistance = 6f;

        public bool AllowsTake(PlayerId player, NetworkPlayer avatar) =>
            avatar == null || Vector3.Distance(avatar.transform.position, transform.position) <= maxDistance;
    }
}
