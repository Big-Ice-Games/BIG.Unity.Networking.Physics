using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Physical.Editor
{
    /// <summary> Build / Clear buttons for generated physics objects. </summary>
    internal static class BuildButtons
    {
        public static void Draw(Object target, bool isBuilt, System.Action build, System.Action clear, string note)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(isBuilt ? note : "Not built yet — press Build.", isBuilt ? MessageType.Info : MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !EditorApplication.isPlaying;
                if (GUILayout.Button(isBuilt ? "Rebuild" : "Build", GUILayout.Height(24)))
                {
                    Undo.RegisterFullObjectHierarchyUndo(target is Component c ? c.gameObject : target, "Build");
                    build();
                    EditorUtility.SetDirty(target);
                }
                if (clear != null && isBuilt && GUILayout.Button("Clear", GUILayout.Width(70), GUILayout.Height(24)))
                {
                    Undo.RegisterFullObjectHierarchyUndo(target is Component c2 ? c2.gameObject : target, "Clear");
                    clear();
                    EditorUtility.SetDirty(target);
                }
                GUI.enabled = true;
            }
        }
    }

    [CustomEditor(typeof(HangingRope))]
    internal sealed class HangingRopeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var rope = (HangingRope)target;
            BuildButtons.Draw(rope, rope.IsBuilt, rope.Build, rope.Clear,
                $"{rope.Links.Count} links. Changed the numbers? Rebuild. (Networked ropes get their network components back automatically.)");
        }
    }

    [CustomEditor(typeof(BreakableHinge))]
    internal sealed class BreakableHingeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var hinge = (BreakableHinge)target;
            BuildButtons.Draw(hinge, hinge.Hinge != null, hinge.Build, null, "Changed the numbers? Rebuild.");
        }

        private void OnSceneGUI()
        {
            var hinge = ((BreakableHinge)target).Hinge;
            if (hinge == null) return;
            Vector3 point = hinge.transform.TransformPoint(hinge.anchor);
            Vector3 axis = hinge.transform.TransformDirection(hinge.axis).normalized;
            Handles.color = Color.yellow;
            Handles.DrawLine(point - axis, point + axis, 3f);
        }
    }
}
