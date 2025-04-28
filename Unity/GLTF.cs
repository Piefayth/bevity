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

        // --- 1. Check for the specific component ---
        // GetComponentInChildren also checks the component on the current GameObject itself.
        NewBevityComponents bevityComponents = unityTransform.GetComponentInChildren<NewBevityComponents>(true); // Include inactive components if needed

        // If the component is not found on this GameObject or its children, do nothing for this node.
        if (bevityComponents == null) {
            // Optional: Log if you want to know which nodes are skipped
            // Debug.Log($"CustomExtrasPlugin: Skipping node '{unityTransform.name}' (ID: {nodeId.Id}) - Component 'MyCustomDataComponent' not found in children.");
            return;
        }

        // --- 2. Prepare your custom JSON data ---
        // You can now access data from the found 'dataComponent' if needed.
        var customData = bevityComponents.ToBevyJson();

        // --- 3. Add the custom data to the glTF node's extras ---

        // Check if the Extras object already exists on the node. If not, create it.
        if (gltfNode.Extras == null) {
            gltfNode.Extras = new JObject();
        }

        // Add your custom JObject to the node's Extras under the specified key.
        // This will overwrite if the key already exists on this node's extras.
        gltfNode.Extras[ExtrasDataKey] = customData;

        //Debug.Log($"CustomExtrasPlugin: Added data under key '{ExtrasDataKey}'. Node extras: {gltfNode.Extras.ToString(Newtonsoft.Json.Formatting.None)}"); // Use None formatting for brevity in logs
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