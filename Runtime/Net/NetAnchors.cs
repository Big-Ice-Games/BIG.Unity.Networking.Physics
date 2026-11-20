using BIG.Unity.Physical;
using System.Text;
using BIG.Unity.Networking;
using FishNet;
using FishNet.Object;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace BIG.Unity.Physical.Net
{
    /// <summary>
    /// Turns what a hand is stuck to (a Transform) into a string every machine resolves to the same object, and back.
    /// <list type="bullet">
    /// <item>Inside a player avatar: <c>p|playerId|path-under-avatar</c> — avatars are spawned at runtime; the player id is
    /// the same everywhere.</item>
    /// <item>Anything networked (props, chain links, door, zombie parts): <c>b|objectId:componentIndex|path-under-body</c> —
    /// the NetworkBody's network identity, the same on every machine no matter how the scene hierarchy got reordered.</item>
    /// <item>Static scenery (walls, floor): <c>s|scene|object-name/child-name...</c> — by names, not sibling indices (scene
    /// root order is not the same on every machine once objects are spawned/moved at runtime; that broke the old ids).</item>
    /// </list>
    /// </summary>
    public static class NetAnchors
    {
        const char Separator = '|';

        public static string IdOf(Transform target)
        {
            var avatar = target.GetComponentInParent<NetworkPlayer>();
            if (avatar != null)
                return $"p{Separator}{avatar.Id.Value}{Separator}{IndexPath(target, avatar.transform)}";

            var body = target.GetComponentInParent<NetworkBody>();
            if (body != null && body.IsSpawned)
                return $"b{Separator}{body.ObjectId}:{body.ComponentIndex}{Separator}{IndexPath(target, body.transform)}";

            return $"s{Separator}{target.gameObject.scene.name}{Separator}{NamePath(target)}";
        }

        /// <summary> The Transform behind <paramref name="id"/> on this machine, or null (not spawned / not loaded yet). </summary>
        public static Transform Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var parts = id.Split(Separator);
            if (parts.Length != 3) return null;

            switch (parts[0])
            {
                case "p":
                    return NetworkSession.Instance.Players.TryGet(new PlayerId(parts[1]), out var avatar)
                        ? WalkIndices(avatar.transform, parts[2])
                        : null;

                case "b":
                {
                    var key = parts[1].Split(':');
                    if (key.Length != 2 || !int.TryParse(key[0], out int objectId) || !byte.TryParse(key[1], out byte component))
                        return null;
                    var spawned = InstanceFinder.IsServerStarted
                        ? InstanceFinder.ServerManager.Objects.Spawned
                        : InstanceFinder.ClientManager.Objects.Spawned;
                    if (!spawned.TryGetValue(objectId, out NetworkObject networkObject)) return null;
                    if (component >= networkObject.NetworkBehaviours.Count) return null;
                    return WalkIndices(networkObject.NetworkBehaviours[component].transform, parts[2]);
                }

                case "s":
                {
                    var scene = UnitySceneManager.GetSceneByName(parts[1]);
                    if (!scene.isLoaded) return null;
                    var names = parts[2].Split('/');
                    foreach (var root in scene.GetRootGameObjects())
                        if (root.name == names[0])
                            return WalkNames(root.transform, names);
                    return null;
                }
            }
            return null;
        }

        /// <summary> Sibling indices from <paramref name="top"/> (exclusive) down to <paramref name="target"/>. </summary>
        static string IndexPath(Transform target, Transform top)
        {
            var path = new StringBuilder();
            for (var t = target; t != null && t != top; t = t.parent)
                path.Insert(0, path.Length == 0 ? $"{t.GetSiblingIndex()}" : $"{t.GetSiblingIndex()}/");
            return path.ToString();
        }

        static string NamePath(Transform target)
        {
            var path = new StringBuilder();
            for (var t = target; t != null; t = t.parent)
                path.Insert(0, path.Length == 0 ? t.name : $"{t.name}/");
            return path.ToString();
        }

        static Transform WalkIndices(Transform from, string path)
        {
            if (path.Length == 0) return from;
            var current = from;
            foreach (var part in path.Split('/'))
            {
                if (!int.TryParse(part, out int index) || index >= current.childCount) return null;
                current = current.GetChild(index);
            }
            return current;
        }

        static Transform WalkNames(Transform root, string[] names)
        {
            var current = root;
            for (int i = 1; i < names.Length && current != null; i++)
                current = current.Find(names[i]);
            return current;
        }
    }
}
