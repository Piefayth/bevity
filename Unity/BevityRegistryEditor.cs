#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// Create a custom editor for BevityComponent
[CustomEditor(typeof(BevityRegistry))]
public class BevityRegistryEditor : Editor
{
    public override void OnInspectorGUI() {
        DrawDefaultInspector();

        EditorGUILayout.Space();

        BevityRegistry myTarget = (BevityRegistry)target;

        if (GUILayout.Button("Fetch JSON Data", GUILayout.Height(30))) {
            myTarget.FetchData();
        }
    }
}
#endif