using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Physical.Editor
{
    /// <summary>
    /// Creates ready-to-play physics objects (single player — for networked ones see GameObject > BIG Networking > Physics):
    /// GameObject > BIG Physics > Physical Player / Hanging Rope / Hinged Door / Simple Ragdoll. The same calls are public
    /// for scene builders. Materials it needs are created in Assets/BIG.Physics/Materials (URP Lit if present, else Standard).
    /// </summary>
    public static class PhysicalAuthoring
    {
        private const string MENU = "GameObject/BIG Physics/";
        private const string MATERIALS = "Assets/BIG.Physics/Materials";

        [MenuItem(MENU + "Physical Player", false, 10)]
        private static void MenuPlayer() => Place(CreatePlayer(Vector3.zero), "Create Physical Player");

        [MenuItem(MENU + "Hanging Rope (lamp)", false, 30)]
        private static void MenuRope()
        {
            var go = new GameObject("Hanging Rope");
            go.transform.position = new Vector3(0f, 6f, 0f);
            var rope = go.AddComponent<HangingRope>();
            rope.ropeMaterial = ColorMaterial("Rope", new Color(0.2f, 0.2f, 0.22f));
            rope.Build();
            Place(go, "Create Hanging Rope");
        }

        [MenuItem(MENU + "Hinged Door", false, 31)]
        private static void MenuDoor() => Place(CreateDoor(Vector3.zero, ColorMaterial("Door", new Color(0.25f, 0.45f, 0.3f))), "Create Hinged Door");

        [MenuItem(MENU + "Simple Ragdoll", false, 32)]
        private static void MenuRagdoll() =>
            Place(CreateSimpleRagdoll("Ragdoll", Vector3.zero, ColorMaterial("Ragdoll Skin", new Color(0.55f, 0.65f, 0.45f)),
                ColorMaterial("Ragdoll Clothes", new Color(0.35f, 0.3f, 0.4f))).gameObject, "Create Simple Ragdoll");

        private static void Place(GameObject go, string undo)
        {
            if (SceneView.lastActiveSceneView != null)
                go.transform.position += SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(go, undo);
            Selection.activeGameObject = go;
        }

        #region Player

        /// <summary>
        /// A first-person physical player: force-driven capsule (<see cref="PhysicalPlayer"/>), eyes / camera, two
        /// <see cref="StretchyHand"/>s, a simple body for others to see (<see cref="PlayerBodyVisual"/>) and the debug HUD.
        /// The hand blobs are children (they detach at runtime), so the whole avatar fits in one prefab.
        /// </summary>
        public static GameObject CreatePlayer(Vector3 feet, Material skin = null, Material arm = null)
        {
            skin = skin != null ? skin : ColorMaterial("Avatar Skin", new Color(0.35f, 0.9f, 0.2f));
            arm = arm != null ? arm : ColorMaterial("Avatar Arm", new Color(0.35f, 0.9f, 0.2f), unlit: true);

            var player = new GameObject("Physical Player");
            player.transform.position = feet + Vector3.up * 0.7f;

            var capsule = player.AddComponent<CapsuleCollider>();
            capsule.height = 1.2f;
            capsule.radius = 0.35f;
            var body = player.AddComponent<Rigidbody>();
            body.mass = 30f;

            var eyes = new GameObject("Camera") { tag = "MainCamera" };
            eyes.transform.SetParent(player.transform, false);
            eyes.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            var camera = eyes.AddComponent<Camera>();
            camera.nearClipPlane = 0.05f;
            camera.fieldOfView = 75f;
            eyes.AddComponent<AudioListener>();

            var controller = player.AddComponent<PhysicalPlayer>();
            controller.cameraPivot = eyes.transform;

            var hands = player.AddComponent<PlayerHands>();
            hands.body = body;
            hands.aim = eyes.transform;
            hands.left = CreateHand(player.transform, "Left", -1f, hands, eyes.transform, skin, arm);
            hands.right = CreateHand(player.transform, "Right", 1f, hands, eyes.transform, skin, arm);
            player.AddComponent<HandsDebugHud>();

            CreateBodyVisual(player, controller, eyes.transform, skin);
            return player;
        }

        private static StretchyHand CreateHand(Transform player, string name, float side, PlayerHands owner, Transform eyes, Material handMat, Material armMat)
        {
            // Shoulder: low and to the side, just outside the view.
            var root = new GameObject($"{name} Arm");
            root.transform.SetParent(eyes, false);
            root.transform.localPosition = new Vector3(side * 0.32f, -0.45f, 0.15f);

            // Idle pose of the hand: lower corner of the screen, palm forward.
            var rest = new GameObject($"{name} Hand Rest");
            rest.transform.SetParent(eyes, false);
            rest.transform.localPosition = new Vector3(side * 0.34f, -0.24f, 0.62f);
            rest.transform.localRotation = Quaternion.Euler(-70f, 0f, 0f);

            // Sticky blob: a flattened sphere. A child here; StretchyHand moves it to the scene root at runtime.
            var blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            blob.name = $"{name} Hand";
            Object.DestroyImmediate(blob.GetComponent<Collider>());
            blob.transform.SetParent(player, false);
            blob.transform.localScale = new Vector3(0.2f, 0.05f, 0.2f);
            blob.GetComponent<MeshRenderer>().sharedMaterial = handMat;

            var line = root.AddComponent<LineRenderer>();
            line.sharedMaterial = armMat;
            line.numCapVertices = 4;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var hand = root.AddComponent<StretchyHand>();
            hand.owner = owner;
            hand.sideSign = side;
            hand.rest = rest.transform;
            hand.handVisual = blob.transform;
            hand.line = line;
            return hand;
        }

        /// <summary> Simple alien body for others to see: torso + legs turning with yaw, head with eyes on the camera pivot. </summary>
        private static void CreateBodyVisual(GameObject player, PhysicalPlayer controller, Transform eyes, Material skin)
        {
            Material eye = ColorMaterial("Avatar Eye", new Color(0.05f, 0.05f, 0.08f));
            var renderers = new List<Renderer>();

            var torso = new GameObject("Body Visual").transform;
            torso.SetParent(player.transform, false);
            Part(torso, "Torso", PrimitiveType.Capsule, new Vector3(0f, -0.12f, 0f), new Vector3(0.6f, 0.36f, 0.48f), skin, renderers);
            Part(torso, "Leg L", PrimitiveType.Capsule, new Vector3(-0.13f, -0.46f, 0f), new Vector3(0.17f, 0.14f, 0.17f), skin, renderers);
            Part(torso, "Leg R", PrimitiveType.Capsule, new Vector3(0.13f, -0.46f, 0f), new Vector3(0.17f, 0.14f, 0.17f), skin, renderers);
            Part(torso, "Shoulder L", PrimitiveType.Sphere, new Vector3(-0.3f, 0.08f, 0f), Vector3.one * 0.16f, skin, renderers);
            Part(torso, "Shoulder R", PrimitiveType.Sphere, new Vector3(0.3f, 0.08f, 0f), Vector3.one * 0.16f, skin, renderers);

            var head = Part(eyes, "Head", PrimitiveType.Sphere, new Vector3(0f, 0.02f, -0.04f), new Vector3(0.46f, 0.4f, 0.42f), skin, renderers);
            Part(head, "Eye L", PrimitiveType.Sphere, new Vector3(-0.2f, 0.06f, 0.4f), new Vector3(0.26f, 0.32f, 0.12f), eye, renderers);
            Part(head, "Eye R", PrimitiveType.Sphere, new Vector3(0.2f, 0.06f, 0.4f), new Vector3(0.26f, 0.32f, 0.12f), eye, renderers);

            var visual = player.AddComponent<PlayerBodyVisual>();
            visual.controller = controller;
            visual.torso = torso;
            visual.renderers = renderers.ToArray();
        }

        private static Transform Part(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, List<Renderer> renderers)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderers.Add(renderer);
            return go.transform;
        }

        #endregion

        #region Door, ragdoll

        /// <summary> A 1 x 2 m door slab on a <see cref="BreakableHinge"/> (left edge), standing on <paramref name="floor"/>. </summary>
        public static GameObject CreateDoor(Vector3 floor, Material material)
        {
            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.position = floor + new Vector3(0f, 1.03f, 0f);
            door.transform.localScale = new Vector3(1f, 1.96f, 0.08f);
            door.GetComponent<MeshRenderer>().sharedMaterial = material;
            door.AddComponent<BreakableHinge>().Build();
            return door;
        }

        /// <summary>
        /// A ~25 kg ragdoll out of primitives (torso, head, two arms, two legs on joints) with a <see cref="Ragdoll"/> on the
        /// root. No brain — add your own (it reads Ragdoll.simulate / IsHeld and pushes Ragdoll.core).
        /// </summary>
        public static Ragdoll CreateSimpleRagdoll(string name, Vector3 feet, Material skin, Material clothes)
        {
            var go = new GameObject(name);
            go.transform.position = feet;

            Rigidbody torso = CreateRagdollPart(go.transform, "Torso", PrimitiveType.Cube, feet + new Vector3(0f, 1.05f, 0f), new Vector3(0.45f, 0.6f, 0.25f), 12f, clothes);
            Rigidbody head = CreateRagdollPart(go.transform, "Head", PrimitiveType.Sphere, feet + new Vector3(0f, 1.53f, 0f), Vector3.one * 0.34f, 3f, skin);
            LimbJoint(head, torso, feet + new Vector3(0f, 1.36f, 0f), 35f);
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                string sideName = i == 0 ? "Left" : "Right";
                // Capsule primitive is 2 units tall, so scale.y is half the limb length.
                var arm = CreateRagdollPart(go.transform, $"{sideName} Arm", PrimitiveType.Capsule, feet + new Vector3(side * 0.3f, 1.0f, 0f), new Vector3(0.13f, 0.28f, 0.13f), 2f, skin);
                LimbJoint(arm, torso, feet + new Vector3(side * 0.3f, 1.28f, 0f), 0f);
                var leg = CreateRagdollPart(go.transform, $"{sideName} Leg", PrimitiveType.Capsule, feet + new Vector3(side * 0.12f, 0.42f, 0f), new Vector3(0.16f, 0.33f, 0.16f), 3f, clothes);
                LimbJoint(leg, torso, feet + new Vector3(side * 0.12f, 0.75f, 0f), 50f);
            }

            var ragdoll = go.AddComponent<Ragdoll>();
            ragdoll.core = torso;
            return ragdoll;
        }

        private static Rigidbody CreateRagdollPart(Transform root, string name, PrimitiveType type, Vector3 position, Vector3 scale, float mass, Material material)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = $"{root.name} {name}";
            part.transform.SetParent(root);
            part.transform.position = position;
            part.transform.localScale = scale;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;

            var body = part.AddComponent<Rigidbody>();
            body.mass = mass;
            body.angularDamping = 0.5f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.solverIterations = 12;
            part.AddComponent<RagdollPart>();
            return body;
        }

        /// <summary> Pins a limb to the torso at a world point. swingLimit 0 = swings freely, otherwise limited to that many degrees. </summary>
        private static void LimbJoint(Rigidbody limb, Rigidbody torso, Vector3 worldPivot, float swingLimit)
        {
            var joint = limb.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = torso;
            joint.anchor = limb.transform.InverseTransformPoint(worldPivot);
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            if (swingLimit <= 0f) return;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit = new SoftJointLimit { limit = -swingLimit };
            joint.highAngularXLimit = new SoftJointLimit { limit = swingLimit };
            joint.angularYLimit = new SoftJointLimit { limit = swingLimit * 0.5f };
            joint.angularZLimit = new SoftJointLimit { limit = swingLimit * 0.5f };
        }

        #endregion

        /// <summary> A plain coloured material asset in Assets/BIG.Physics/Materials (created once, then reused). </summary>
        public static Material ColorMaterial(string name, Color color, bool unlit = false)
        {
            string path = $"{MATERIALS}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            Directory.CreateDirectory(MATERIALS);
            Shader shader = Shader.Find(unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find(unlit ? "Unlit/Color" : "Standard");
            material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.color = color;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
