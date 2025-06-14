using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq; // Required for JObject, JProperty, JArray
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Plugins; // Make sure you have the UnityGLTF package installed

/// <summary>
/// Defines the custom export plugin for adding data to glTF node extras
/// based on the presence of a specific component.
/// This class tells UnityGLTF about the plugin.
/// </summary>
public class CustomExtrasPlugin : GLTFExportPlugin
{
    // Name displayed in the UnityGLTF export settings
    public override string DisplayName => "Custom GLTF Node Extras (Component Based)";
    // Description displayed in the UnityGLTF export settings
    public override string Description => "Adds custom JSON data to the glTF node extras field if the GameObject or its children have a specific component (e.g., MyCustomDataComponent).";

    // Creates an instance of the context that handles the actual logic
    public override GLTFExportPluginContext CreateInstance(ExportContext context) {
        return new CustomExtrasContext();
    }
}

/// <summary>
/// Handles the logic for adding custom data to glTF node extras.
/// </summary>
public class CustomExtrasContext : GLTFExportPluginContext
{
    // --- Configuration ---
    // Set this to the specific key you want your custom data to appear under in the extras object.
    private const string ExtrasDataKey = "bevity";

    /// <summary>
    /// This method is called after each Unity node (Transform) is converted into a glTF node.
    /// It allows modification of the glTF node based on the original Unity object.
    /// </summary>
    /// <param name="exporter">The exporter instance, providing context and helper methods.</param>
    /// <param name="gltfRoot">The root object of the glTF file being built.</param>
    /// <param name="unityTransform">The original Unity Transform component that this glTF node represents.</param>
    /// <param name="gltfNode">The glTF node that has just been created.</param>
    /// <param name="nodeId">The ID assigned to this node in the glTF structure.</param>
    public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform unityTransform, Node gltfNode) {
        if (unityTransform == null || gltfNode == null) return;

        // YOUR EXISTING BEVITY COMPONENT LOGIC
        BevityComponents bevityComponents = unityTransform.GetComponent<BevityComponents>();
        if (bevityComponents == null) {
            Debug.Log($"CustomExtrasPlugin: Adding BevityComponents to '{unityTransform.name}' during export");
            bevityComponents = unityTransform.gameObject.AddComponent<BevityComponents>();
        }

        var customData = bevityComponents.ToBevyJson();
        if (gltfNode.Extras == null) {
            gltfNode.Extras = new JObject();
        }
        gltfNode.Extras[ExtrasDataKey] = customData;

        // NEW: FIX UNITY'S BAKED SCALING
        //FixUnityBakedTransforms(unityTransform, gltfNode);
    }

    private void FixUnityBakedTransforms(Transform unityTransform, Node gltfNode) {
        // Only fix child objects (root objects don't have baked scaling)
        if (unityTransform.parent == null) return;

        // Calculate the accumulated scale from all parents
        Vector3 accumulatedParentScale = CalculateAccumulatedParentScale(unityTransform);

        // If there's no scaling in the hierarchy, nothing to fix
        if (accumulatedParentScale == Vector3.one) return;

        // Unity has already baked the parent scaling into this transform's position
        // We need to "unbake" it so Bevy can apply the scaling correctly
        Vector3 unbakedPosition = Vector3.Scale(unityTransform.localPosition,
            new Vector3(1f / accumulatedParentScale.x, 1f / accumulatedParentScale.y, 1f / accumulatedParentScale.z));

        // Convert Unity Vector3 to GLTF Vector3
        gltfNode.Translation = new GLTF.Math.Vector3(unbakedPosition.x, unbakedPosition.y, unbakedPosition.z);

        Debug.Log($"Fixed baked scaling for '{unityTransform.name}': " +
                  $"{unityTransform.localPosition} -> {unbakedPosition} " +
                  $"(parent scale: {accumulatedParentScale})");
    }

    private Vector3 CalculateAccumulatedParentScale(Transform child) {
        Vector3 accumulated = Vector3.one;
        Transform current = child.parent;

        while (current != null) {
            accumulated = Vector3.Scale(accumulated, current.localScale);
            current = current.parent;
        }

        return accumulated;
    }

    // Optional: If you still need to add data to the ROOT extras as well,
    // you can uncomment and use the AfterSceneExport method from the previous version.
    /*
    public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
    {
        Debug.Log("CustomExtrasPlugin: Running AfterSceneExport to add root extras.");
        if (gltfRoot.Extras == null) gltfRoot.Extras = new JObject();
        gltfRoot.Extras["myPluginSpecificRootData"] = new JObject(new JProperty("globalInfo", "Root data added by plugin"));
        Debug.Log($"CustomExtrasPlugin: Added root extras. Current glTF root extras: {gltfRoot.Extras.ToString(Newtonsoft.Json.Formatting.Indented)}");
    }
    */
}
