using System.IO;
using BIG.Unity.Networking;
using BIG.Unity.Physical.Net;
using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BIG.Unity.Physical.Editor
{
    /// <summary>
    /// One click from a physics object to a networked one ("everyone simulates themselves and what they hold"):
    /// GameObject > BIG > Networking > Physics > ... on the selection (scene objects or prefab mode).
    /// <list type="bullet">
    /// <item><b>Physical player</b> — a <see cref="PhysicalPlayer"/> avatar: NetworkObject, NetworkTransform, NetworkBody
    /// (PhysicalPlayer profile: its owner simulates it, never handed back), NetworkPlayer, <see cref="NetworkPhysicalPlayer"/>;
    /// local-only bits off until the network assigns roles. Save it as a prefab and give it to the PlayerSpawner.</item>
    /// <item><b>Grabbable prop</b> — Rigidbody object: grabbing it takes it over (<see cref="NetworkGrabbable"/>),
    /// PhysicalProp profile (back to the host once it rests).</item>
    /// <item><b>Hanging rope</b> — a <see cref="HangingRope"/>: one NetworkObject on the rope, every link and the end
    /// grabbable, the mount's tear synced (<see cref="NetworkBreakableMount"/>). Rebuilding the rope re-applies this.</item>
    /// <item><b>Hinged (door)</b> — a <see cref="BreakableHinge"/>: grabbable + synced tear.</item>
    /// <item><b>Ragdoll NPC</b> — a <see cref="Ragdoll"/>: one NetworkObject, every part grabbable, <see cref="NetworkRagdoll"/>.</item>
    /// </list>
    /// Missing physics components (HangingRope, BreakableHinge, Ragdoll) are added and built first. Undoable.
    /// </summary>
    public static class PhysicalNetworking
    {
        private const string MENU = "GameObject/BIG/Networking/Physics/";
        private const string PROFILES = "Assets/BIG.Networking/Profiles";
        public const string PROP_PROFILE = PROFILES + "/PhysicalProp.asset";
        public const string PLAYER_PROFILE = PROFILES + "/PhysicalPlayer.asset";

        [MenuItem(MENU + "Physical player", false, 10)] private static void MPlayer() => ForSelection(MakePlayer);
        [MenuItem(MENU + "Grabbable prop", false, 30)] private static void MProp() => ForSelection(MakeGrabbableProp);
        [MenuItem(MENU + "Hanging rope (lamp)", false, 31)] private static void MRope() => ForSelection(MakeRope);
        [MenuItem(MENU + "Hinged (door)", false, 32)] private static void MHinge() => ForSelection(MakeHinged);
        [MenuItem(MENU + "Ragdoll NPC", false, 33)] private static void MRagdoll() => ForSelection(MakeRagdoll);

        [MenuItem(MENU + "Physical player", true)]
        [MenuItem(MENU + "Grabbable prop", true)]
        [MenuItem(MENU + "Hanging rope (lamp)", true)]
        [MenuItem(MENU + "Hinged (door)", true)]
        [MenuItem(MENU + "Ragdoll NPC", true)]
        private static bool HasSelection() => Selection.gameObjects.Length > 0 && !EditorApplication.isPlaying;

        private static void ForSelection(System.Action<GameObject> make)
        {
            foreach (var go in Selection.gameObjects) make(go);
        }

        // Rebuilding a networked rope / hinge drops the components on what it regenerates — put them back.
        [InitializeOnLoadMethod]
        private static void HookRebuilds()
        {
            HangingRope.Built += rope =>
            {
                if (!EditorApplication.isPlaying && rope.TryGetComponent<NetworkObject>(out _)) NetworkRope(rope);
            };
            BreakableHinge.Built += hinge =>
            {
                if (!EditorApplication.isPlaying && hinge.TryGetComponent<NetworkObject>(out _)) NetworkHinge(hinge);
            };
        }

        #region Presets

        public static void MakePlayer(GameObject go)
        {
            Begin(go, "Physical player");
            var controller = go.GetComponent<PhysicalPlayer>();
            if (controller == null)
            {
                Debug.LogError($"[Network] '{go.name}' has no PhysicalPlayer — create one with GameObject > BIG > Physics > Physical Player.", go);
                return;
            }

            Ensure<NetworkObject>(go);
            Ensure<NetworkTransform>(go);
            SetProfile(Ensure<NetworkBody>(go), PlayerProfile());
            Ensure<NetworkPlayer>(go);
            Ensure<NetworkPhysicalPlayer>(go);

            // Spawned for everyone: nobody local, nobody simulating until the network says so.
            Undo.RecordObject(controller, "Physical player");
            controller.isLocal = false;
            controller.simulate = false;
            if (controller.cameraPivot.TryGetComponent<Camera>(out var camera))
            {
                Undo.RecordObject(camera, "Physical player");
                camera.enabled = false;
            }
            if (controller.cameraPivot.TryGetComponent<AudioListener>(out var listener))
            {
                Undo.RecordObject(listener, "Physical player");
                listener.enabled = false;
            }
            End(go, "Physical player — save it as a prefab, put it on the PlayerSpawner.");
        }

        public static void MakeGrabbableProp(GameObject go)
        {
            Begin(go, "Grabbable prop");
            if (go.GetComponentInChildren<Collider>() == null) Undo.AddComponent<BoxCollider>(go);
            Ensure<Rigidbody>(go);
            Ensure<NetworkObject>(go);
            NetworkGrabbableBody(go);
            End(go, "Grabbable prop");
        }

        public static void MakeRope(GameObject go)
        {
            Begin(go, "Hanging rope");
            var rope = Ensure<HangingRope>(go);
            Ensure<NetworkObject>(go);
            if (!rope.IsBuilt) rope.Build(); // Built → NetworkRope via the hook.
            else NetworkRope(rope);
            End(go, "Hanging rope");
        }

        public static void MakeHinged(GameObject go)
        {
            Begin(go, "Hinged");
            var hinge = Ensure<BreakableHinge>(go);
            Ensure<NetworkObject>(go);
            if (hinge.Hinge == null) hinge.Build();
            else NetworkHinge(hinge);
            End(go, "Hinged (door)");
        }

        public static void MakeRagdoll(GameObject go)
        {
            Begin(go, "Ragdoll NPC");
            var ragdoll = Ensure<Ragdoll>(go);
            if (ragdoll.core == null) ragdoll.core = go.GetComponentInChildren<Rigidbody>();
            Ensure<NetworkObject>(go);
            foreach (var body in go.GetComponentsInChildren<Rigidbody>())
            {
                Ensure<RagdollPart>(body.gameObject);
                NetworkGrabbableBody(body.gameObject);
            }
            Ensure<NetworkRagdoll>(go);
            End(go, "Ragdoll NPC — its brain runs where Ragdoll.simulate is true");
        }

        #endregion

        #region Building blocks

        private static void NetworkRope(HangingRope rope)
        {
            foreach (var link in rope.Links) NetworkGrabbableBody(link.gameObject);
            NetworkGrabbableBody(rope.EndBody.gameObject);
            if (rope.EndBody.TryGetComponent<BreakableMount>(out _)) Ensure<NetworkBreakableMount>(rope.EndBody.gameObject);
            AssignSceneIds(rope.gameObject);
        }

        private static void NetworkHinge(BreakableHinge hinge)
        {
            NetworkGrabbableBody(hinge.gameObject);
            if (hinge.TryGetComponent<BreakableMount>(out _)) Ensure<NetworkBreakableMount>(hinge.gameObject);
            AssignSceneIds(hinge.gameObject);
        }

        /// <summary> A rigidbody under a NetworkObject: synced transform, owner-simulated NetworkBody, grab = take over. </summary>
        public static void NetworkGrabbableBody(GameObject go)
        {
            Ensure<NetworkTransform>(go);
            SetProfile(Ensure<NetworkBody>(go), PropProfile());
            Ensure<NetworkGrabbable>(go);
        }

        private static T Ensure<T>(GameObject go) where T : Component =>
            go.TryGetComponent<T>(out var existing) ? existing : Undo.AddComponent<T>(go);

        private static void SetProfile(NetworkBody body, NetworkBodyProfile profile)
        {
            var serialized = new SerializedObject(body);
            serialized.FindProperty("_profile").objectReferenceValue = profile;
            serialized.ApplyModifiedProperties();
        }

        private static void Begin(GameObject go, string what) => Undo.SetCurrentGroupName($"Make Networked ({what})");

        private static void End(GameObject go, string what)
        {
            AssignSceneIds(go);
            Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
            EditorUtility.SetDirty(go);
            Debug.Log($"[Network] '{go.name}' is networked: {what}.", go);
        }

        /// <summary>
        /// Scene objects get their FishNet SceneId in OnValidate — not run for components added from code (SceneId 0 =
        /// never spawns). Runs FishNet's own generator. Prefabs don't need it.
        /// </summary>
        private static void AssignSceneIds(GameObject go)
        {
            // Not saved yet (scene builders): they assign ids after saving.
            if (PrefabUtility.IsPartOfPrefabAsset(go) || PrefabStageUtility.GetPrefabStage(go) != null || !go.scene.IsValid() ||
                string.IsNullOrEmpty(go.scene.path)) return;
            Scene scene = go.scene;
            var createSceneId = typeof(NetworkObject).GetMethod("CreateSceneId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (createSceneId == null)
                throw new System.MissingMethodException("FishNet NetworkObject.CreateSceneId not found — FishNet changed; " +
                                                        "run Tools > Fish-Networking > Utility > Reserialize NetworkObjects.");
            createSceneId.Invoke(null, new object[] { scene, true, 0 });
            EditorSceneManager.MarkSceneDirty(scene);
        }

        #endregion

        #region Profiles

        /// <summary>
        /// Props, links, doors, ragdoll parts: owner authority + owner physics; no owner = the host simulates; grabbed = the
        /// grabber does; back to the host once it rests. No extrapolation: held things swing on springy hands and change
        /// direction constantly — guessing ahead overshoots and snaps back.
        /// </summary>
        public static NetworkBodyProfile PropProfile() => Profile(PROP_PROFILE, autoReturn: true);

        /// <summary> The avatar: always simulated by its owner, never handed back. </summary>
        public static NetworkBodyProfile PlayerProfile() => Profile(PLAYER_PROFILE, autoReturn: false);

        private static NetworkBodyProfile Profile(string path, bool autoReturn)
        {
            var profile = AssetDatabase.LoadAssetAtPath<NetworkBodyProfile>(path);
            if (profile != null) return profile;

            Directory.CreateDirectory(PROFILES);
            profile = NetworkBodyProfile.Create(BodyPreset.PhysicsProp);
            profile.Authority = BodyAuthority.Owner;
            profile.Physics = BodyPhysics.Owner;
            profile.Interpolation = 2;
            profile.Extrapolation = 0;
            profile.AutoReturnToHost = autoReturn;
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }

        #endregion
    }
}
