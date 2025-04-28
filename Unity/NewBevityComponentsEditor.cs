using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="NewBevityComponents"/> that renders every component/value directly from the schema
/// produced by the Bevy remote protocol. It works with the flat <c>object</c> graph stored by
/// <see cref="NewBevityComponents"/>.
/// </summary>
[CustomEditor(typeof(NewBevityComponents))]
public class NewBevityComponentsEditor : Editor
{
    private NewBevityComponents _target;
    private Vector2 _scroll;
    private string _searchText = "";
    private readonly Dictionary<string, bool> _foldouts = new();

    // Cache for available types
    private string[] _availableTypes = new string[0];
    private string[] _filteredTypes = new string[0];
    private string _lastSearchText = "";

    // Pagination for component types
    private readonly int _typesPerPage = 10;
    private int _currentPage = 0;

    // Track if a component was just opened
    private bool _anyComponentJustExpanded = false;

    // Style for type labels
    private GUIStyle _typeStyle;

    /*—————————— Unity entry points ——————————*/

    private void OnEnable() {
        _target = (NewBevityComponents)target;
        RefreshAvailableTypes();

        // Initialize type style  
        try {
            _typeStyle = new GUIStyle(EditorStyles.miniLabel) {
                fontStyle = FontStyle.Italic,
                fontSize = 9,
                normal = {
                textColor = new Color(0.5f, 0.5f, 0.5f)
            }
            };
        } catch (Exception) {
            // this happens sometimes dont worry about it
        }

    }

    public override void OnInspectorGUI() {
        if (_target == null) return;

        // Reset any component expansion tracking
        _anyComponentJustExpanded = false;

        // Registry field - use SerializedProperty to access the registry field correctly 
        var registryProp = serializedObject.FindProperty("_registry");
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(registryProp, new GUIContent("Registry"));
        if (EditorGUI.EndChangeCheck()) {
            serializedObject.ApplyModifiedProperties();
            RefreshAvailableTypes();
        }

        // Action buttons
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Reload Schemas")) {
            _target.LoadSchemasFromRegistry();
            RefreshAvailableTypes();
            GUI.FocusControl(null);
        }

        if (GUILayout.Button("Generate JSON")) {
            var json = _target.ToBevyJson();
            EditorGUIUtility.systemCopyBuffer = json.ToString();
            Debug.Log($"Generated JSON (copied to clipboard):\n{json}");
        }
        EditorGUILayout.EndHorizontal();

        // Display existing components
        if (_target.ComponentTypes.Any()) {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Components", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var typePath in _target.ComponentTypes.ToList()) {
                DrawComponent(typePath);
            }
            EditorGUILayout.EndScrollView();
        } else {
            EditorGUILayout.HelpBox("No components added yet.", MessageType.Info);
        }

        // Add component section
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Add Component", EditorStyles.boldLabel);
        DrawAddComponentSection();

        if (GUI.changed) EditorUtility.SetDirty(_target);
    }

    /*—————————— Component Drawing ——————————*/

    private void DrawComponent(string typePath) {
        var schema = GetSchema(typePath);
        if (schema == null) {
            EditorGUILayout.HelpBox($"Schema for {typePath} not found", MessageType.Error);
            return;
        }

        bool isSimpleComponent = IsSimpleComponent(schema);
        var foldKey = $"comp_{typePath}";
        _foldouts.TryGetValue(foldKey, out var open);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        // Header row with foldout and remove button
        EditorGUILayout.BeginHorizontal();

        // Use a better style for the component header
        var style = new GUIStyle(EditorStyles.foldout) {
            fontStyle = FontStyle.Bold,
            fontSize = 12
        };
        var labelStyle = new GUIStyle(EditorStyles.boldLabel) {
            fontSize = 12
        };

        // For simple components, just show label. For complex ones, use foldout
        if (!isSimpleComponent) {
            bool wasOpen = open;
            open = EditorGUILayout.Foldout(open, Simplify(typePath), true, style);

            // Track if this component was just expanded
            if (!wasOpen && open) {
                _anyComponentJustExpanded = true;
            }

            _foldouts[foldKey] = open;
        } else {
            EditorGUILayout.LabelField(Simplify(typePath), labelStyle);
            open = true; // Always consider simple components as "open"
        }

        if (GUILayout.Button("Remove", GUILayout.Width(60))) {
            if (EditorUtility.DisplayDialog("Remove Component",
                $"Are you sure you want to remove the {Simplify(typePath)} component?",
                "Yes", "Cancel")) {
                _target.RemoveComponent(typePath);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
        }

        EditorGUILayout.EndHorizontal();

        // Component fields (if expanded)
        if (open) {
            EditorGUI.indentLevel++;

            var cur = _target.GetValue(typePath);

            if (isSimpleComponent) {
                // For simple components, draw directly (no nested foldout)
                var next = DrawSimpleComponent(cur, schema);
                _target.SetValue(typePath, next);
            } else {
                // For complex components with nested structure
                var next = DrawValue(cur, schema, Simplify(typePath), _anyComponentJustExpanded);
                _target.SetValue(typePath, next);
            }

            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2);
    }

    // Draw a component directly without nesting
    private object DrawSimpleComponent(object value, JObject schema) {
        var kind = schema["kind"]?.ToString();

        if (kind == "Enum") {
            // For enum-like components with no data (like RigidBody)
            var variants = new List<string>();

            if (schema["oneOf"] is JArray oneOf) {
                foreach (var variant in oneOf) {
                    if (variant is JObject varObj && varObj["shortPath"] != null) {
                        variants.Add(varObj["shortPath"].ToString());
                    } else if (variant is JValue jVal) {
                        variants.Add(jVal.ToString());
                    }
                }
            }

            if (variants.Count == 0) {
                EditorGUILayout.LabelField("No variants found");
                return value;
            }

            var box = value as NewBevityComponents.EnumBox ?? new NewBevityComponents.EnumBox();
            int currentIndex = string.IsNullOrEmpty(box.Variant) ? 0 : variants.IndexOf(box.Variant);
            if (currentIndex < 0) currentIndex = 0;

            int newIndex = EditorGUILayout.Popup(currentIndex, variants.ToArray());
            if (newIndex != currentIndex || string.IsNullOrEmpty(box.Variant)) {
                box.Variant = variants[newIndex];
                box.Data = null; // Clear data when variant changes for simple enums
            }

            return box;
        } else if (kind == "Value") {
            var tp = schema["typePath"]?.ToString();
            return DrawPrimitive(value, tp, "", true);
        } else if (kind == "Struct") {
            var dict = value as Dictionary<string, object> ?? new Dictionary<string, object>();

            if (schema["properties"] is JObject props) {
                foreach (var p in props.Properties()) {
                    string fieldName = p.Name;
                    dict.TryGetValue(fieldName, out var fieldValue);

                    var fieldSchema = ResolveRef(p.Value["type"], schema);
                    if (fieldSchema != null) {
                        var newValue = DrawValue(fieldValue, fieldSchema, fieldName, false);
                        dict[fieldName] = newValue;
                    }
                }
            }

            return dict;
        } else if (kind == "TupleStruct") {
            var list = value as IList<object> ?? new List<object>();

            if (schema["prefixItems"] is JArray items) {
                // Ensure list has enough elements
                while (list.Count < items.Count)
                    list.Add(null);

                // Special case for single-field tuple struct with glam type
                if (items.Count == 1) {
                    var fieldSchema = ResolveRef(items[0]["type"], schema);
                    if (fieldSchema != null) {
                        var fieldTypePath = fieldSchema["typePath"]?.ToString();

                        if (IsGlam(fieldTypePath)) {
                            // If list[0] is null or not a float array, initialize it
                            if (list[0] is not float[]) {
                                list[0] = new float[GetComponentCount(fieldTypePath)];
                            }

                            // Draw directly 
                            list[0] = DrawGlam(list[0], "", fieldTypePath);
                            return list;
                        }
                    }
                }

                // Regular case - draw each field directly
                for (int i = 0; i < items.Count; i++) {
                    var fieldSchema = ResolveRef(items[i]["type"], schema);
                    if (fieldSchema != null) {
                        var newValue = DrawValue(list[i], fieldSchema, $"Field {i}", false);
                        if (!ReferenceEquals(list[i], newValue))
                            list[i] = newValue;
                    }
                }
            }

            return list;
        } else if (kind == "List" || kind == "Array") {
            // Draw list items directly
            return DrawList(value as IList<object>, schema, "", false);
        } else if (kind == "Option") {
            // Draw option directly
            return DrawOption(value as NewBevityComponents.EnumBox, schema, "", false);
        }

        // Default fallback
        return value;
    }

    private void DrawAddComponentSection() {
        if (_availableTypes.Length == 0) {
            var registry = serializedObject.FindProperty("_registry").objectReferenceValue as BevityRegistry;
            EditorGUILayout.HelpBox(registry == null ?
                "Please assign a Registry" :
                "No component schemas available", MessageType.Info);
            return;
        }

        // Search field
        EditorGUI.BeginChangeCheck();
        _searchText = EditorGUILayout.TextField("Search", _searchText);
        if (EditorGUI.EndChangeCheck() || _lastSearchText != _searchText) {
            FilterAvailableTypes();
            _lastSearchText = _searchText;
            _currentPage = 0;
        }

        // Display types with pagination
        if (_filteredTypes.Length > 0) {
            int totalPages = Mathf.CeilToInt((float)_filteredTypes.Length / _typesPerPage);

            // Pagination controls
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _currentPage > 0;
            if (GUILayout.Button("< Previous", GUILayout.Width(100))) {
                _currentPage--;
            }
            GUI.enabled = true;

            EditorGUILayout.LabelField($"Page {_currentPage + 1} of {totalPages}",
                EditorStyles.centeredGreyMiniLabel,
                GUILayout.Width(80));

            GUI.enabled = _currentPage < totalPages - 1;
            if (GUILayout.Button("Next >", GUILayout.Width(100))) {
                _currentPage++;
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Calculate visible range
            int startIndex = _currentPage * _typesPerPage;
            int endIndex = Mathf.Min(startIndex + _typesPerPage, _filteredTypes.Length);

            // Display types as buttons
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            for (int i = startIndex; i < endIndex; i++) {
                string type = _filteredTypes[i];
                bool alreadyExists = _target.ComponentTypes.Contains(type);

                EditorGUI.BeginDisabledGroup(alreadyExists);
                if (GUILayout.Button(Simplify(type), GUILayout.Height(24))) {
                    _target.AddComponent(type);
                    _foldouts[$"comp_{type}"] = true; // Auto-expand newly added component
                    _anyComponentJustExpanded = true; // Expand all child fields
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndVertical();
        } else {
            EditorGUILayout.HelpBox("No matching types found.", MessageType.Info);
        }
    }

    /*—————————— Value Drawing ——————————*/

    private object DrawValue(object val, JObject schema, string label, bool parentJustExpanded = false) {
        if (schema == null) return val;

        string kind = schema["kind"]?.ToString();
        string typePath = schema["typePath"]?.ToString();

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(typePath))
            return val;

        // Check for glam types first, no matter what container they're in
        if (IsGlam(typePath)) {
            // Initialize properly if the value is null or wrong type
            if (val is not float[] glamData || glamData.Length != GetComponentCount(typePath)) {
                val = new float[GetComponentCount(typePath)];
            }
            return DrawGlam(val, label, typePath);
        }

        switch (kind) {
            case "Value":
                return DrawPrimitive(val, typePath, label, true);
            case "Struct":
                return DrawStruct(val as Dictionary<string, object>, schema, label, parentJustExpanded);
            case "TupleStruct":
                return DrawTupleStruct(val as IList<object>, schema, label, parentJustExpanded);
            case "Enum":
                return DrawEnum(val as NewBevityComponents.EnumBox, schema, label, parentJustExpanded);
            case "Option":
                return DrawOption(val as NewBevityComponents.EnumBox, schema, label, parentJustExpanded);
            case "List":
            case "Array":
                return DrawList(val as IList<object>, schema, label, parentJustExpanded);
            default:
                EditorGUILayout.LabelField(label, $"Unsupported kind: {kind}");
                return val;
        }
    }

    /*—————————— Primitive Types ——————————*/

    private object DrawPrimitive(object val, string typePath, string label, bool showType = false) {
        // Create a field with type label
        if (showType) {
            EditorGUILayout.BeginHorizontal();
            var result = typePath switch {
                "f32" or "f64" => EditorGUILayout.FloatField(label, val is float f ? f : 0f),
                "bool" => EditorGUILayout.Toggle(label, val is bool b && b),
                "alloc::string::String" => EditorGUILayout.TextField(label, val as string ?? ""),
                var s when s.Contains("::Cow<str>") => EditorGUILayout.TextField(label, val as string ?? ""),
                var i when i.StartsWith("i") || i.StartsWith("u") =>
                    EditorGUILayout.IntField(label, val is int n ? n : Convert.ToInt32(val ?? 0)),
                _ => val
            };

            // Show the type
            GUILayout.Label(GetSimpleTypeName(typePath), _typeStyle, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();
            return result;
        } else {
            // If not showing type, use the original implementation
            return typePath switch {
                "f32" or "f64" => EditorGUILayout.FloatField(label, val is float f ? f : 0f),
                "bool" => EditorGUILayout.Toggle(label, val is bool b && b),
                "alloc::string::String" => EditorGUILayout.TextField(label, val as string ?? ""),
                var s when s.Contains("::Cow<str>") => EditorGUILayout.TextField(label, val as string ?? ""),
                var i when i.StartsWith("i") || i.StartsWith("u") =>
                    EditorGUILayout.IntField(label, val is int n ? n : Convert.ToInt32(val ?? 0)),
                _ => val
            };
        }
    }

    private object DrawGlam(object val, string label, string typePath) {
        // Handle glam vector types as a float array 
        float[] arr = val as float[];

        if (arr == null) {
            return arr;
        }

        // Match the style of Unity's Vector3Field etc.  
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(label);

        EditorGUILayout.BeginVertical();
        string[] labels = { "X", "Y", "Z", "W" };
        for (int i = 0; i < arr.Length; i++) {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(i < labels.Length ? labels[i] : $"[{i}]", GUILayout.Width(15));
            arr[i] = EditorGUILayout.FloatField(arr[i]);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();

        // Add the type label for glam
        GUILayout.Label(GetSimpleTypeName(typePath), _typeStyle, GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        return arr;
    }

    /*—————————— Compound Types ——————————*/

    private object DrawStruct(Dictionary<string, object> dict, JObject schema, string label, bool parentJustExpanded) {
        dict ??= new Dictionary<string, object>();

        // Create a foldout for the struct
        string foldoutKey = $"struct_{label}";
        _foldouts.TryGetValue(foldoutKey, out bool open);

        // If parent was just expanded, also expand this struct
        if (parentJustExpanded && !open) {
            open = true;
            _foldouts[foldoutKey] = open;
        }

        // Add type information next to the foldout
        EditorGUILayout.BeginHorizontal();
        open = EditorGUILayout.Foldout(open, label, true);
        if (schema["typePath"] != null) {
            GUILayout.Label(GetSimpleTypeName(schema["typePath"].ToString()), _typeStyle, GUILayout.Width(50));
        }
        EditorGUILayout.EndHorizontal();

        _foldouts[foldoutKey] = open;

        if (!open) return dict;

        // Note if we just opened this struct
        bool thisJustExpanded = parentJustExpanded || (open && !_foldouts.ContainsKey(foldoutKey));
        _foldouts[foldoutKey] = open;

        EditorGUI.indentLevel++;

        // Draw each field
        if (schema["properties"] is JObject props) {
            foreach (var p in props.Properties()) {
                string fieldName = p.Name;
                dict.TryGetValue(fieldName, out var fieldValue);

                var fieldSchema = ResolveRef(p.Value["type"], schema);
                if (fieldSchema != null) {
                    var newValue = DrawValue(fieldValue, fieldSchema, fieldName, thisJustExpanded);
                    // Always update the value and mark as changed
                    dict[fieldName] = newValue;
                }
            }
        }

        EditorGUI.indentLevel--;
        return dict; // Always return the dict to ensure changes propagate
    }

    public static int GetComponentCount(string typePath) {
        if (typePath.Contains("Vec2") || typePath.EndsWith("IVec2") || typePath.EndsWith("UVec2") || typePath.EndsWith("DVec2"))
            return 2;
        if (typePath.Contains("Vec3") || typePath.EndsWith("UVec3"))
            return 3;
        return 4; // Vec4, Quat, etc.
    }

    private object DrawTupleStruct(IList<object> list, JObject schema, string label, bool parentJustExpanded) {
        list ??= new List<object>();

        // Create a foldout for the tuple
        string foldoutKey = $"tuple_{label}";
        _foldouts.TryGetValue(foldoutKey, out bool open);

        // If parent was just expanded, also expand this
        if (parentJustExpanded && !open) {
            open = true;
            _foldouts[foldoutKey] = open;
        }

        // Add type information next to the foldout
        EditorGUILayout.BeginHorizontal();
        open = EditorGUILayout.Foldout(open, label, true);
        if (schema["typePath"] != null) {
            GUILayout.Label(GetSimpleTypeName(schema["typePath"].ToString()), _typeStyle, GUILayout.Width(50));
        }
        EditorGUILayout.EndHorizontal();

        _foldouts[foldoutKey] = open;

        if (!open) return list;

        bool thisJustExpanded = parentJustExpanded || (open && !_foldouts.ContainsKey(foldoutKey));
        _foldouts[foldoutKey] = open;

        EditorGUI.indentLevel++;

        if (schema["prefixItems"] is JArray items) {
            // Ensure list has enough elements
            while (list.Count < items.Count)
                list.Add(null);

            // Special case for single-field tuple struct with glam type
            if (items.Count == 1) {
                var fieldSchema = ResolveRef(items[0]["type"], schema);
                if (fieldSchema != null) {
                    var fieldTypePath = fieldSchema["typePath"]?.ToString();

                    if (IsGlam(fieldTypePath)) {
                        // If list[0] is null or not a float array, initialize it
                        if (list[0] is not float[]) {
                            list[0] = new float[GetComponentCount(fieldTypePath)];
                        }

                        // Draw directly without the "Field 0" label
                        list[0] = DrawGlam(list[0], label, fieldTypePath);
                        EditorGUI.indentLevel--;
                        return list;
                    }
                }
            }

            // Regular case - draw each field
            for (int i = 0; i < items.Count; i++) {
                var fieldSchema = ResolveRef(items[i]["type"], schema);
                if (fieldSchema != null) {
                    var newValue = DrawValue(list[i], fieldSchema, $"Field {i}", thisJustExpanded);
                    if (!ReferenceEquals(list[i], newValue))
                        list[i] = newValue;
                }
            }
        }

        EditorGUI.indentLevel--;
        return list;
    }

    private object DrawEnum(NewBevityComponents.EnumBox box, JObject schema, string label, bool parentJustExpanded) {
        // Create a new EnumBox if null
        if (box == null) {
            box = new NewBevityComponents.EnumBox();
        }

        // Get all available variants
        var variants = new List<string>();
        var variantSchemas = new List<JObject>();

        if (schema["oneOf"] is JArray oneOf) {
            foreach (var variant in oneOf) {
                if (variant is JObject varObj && varObj["shortPath"] != null) {
                    variants.Add(varObj["shortPath"].ToString());
                    variantSchemas.Add(varObj);
                } else if (variant is JValue jVal) {
                    variants.Add(jVal.ToString());
                    variantSchemas.Add(null);
                }
            }
        }

        if (variants.Count == 0) {
            EditorGUILayout.LabelField(label, "No variants found");
            return box;
        }

        // Find current variant index
        int currentIndex = string.IsNullOrEmpty(box.Variant) ? 0 : variants.IndexOf(box.Variant);
        if (currentIndex < 0) currentIndex = 0;

        // Draw variant selector dropdown with type label
        EditorGUILayout.BeginHorizontal();
        int newIndex = EditorGUILayout.Popup(label, currentIndex, variants.ToArray());
        if (schema["typePath"] != null) {
            GUILayout.Label(GetSimpleTypeName(schema["typePath"].ToString()), _typeStyle, GUILayout.Width(50));
        }
        EditorGUILayout.EndHorizontal();

        // If the variant changed, update the EnumBox
        if (newIndex != currentIndex || string.IsNullOrEmpty(box.Variant)) {
            box.Variant = variants[newIndex];
            box.Data = null; // Clear data when variant changes
        }

        // Draw variant data if applicable
        var variantSchema = newIndex < variantSchemas.Count ? variantSchemas[newIndex] : null;
        if (variantSchema != null && box.Variant == variants[newIndex]) {
            // Check if this variant has data
            if (variantSchema["kind"]?.ToString() == "Tuple") {
                EditorGUI.indentLevel++;
                if (variantSchema["prefixItems"] is JArray prefixItems && prefixItems.Count > 0) {
                    var itemSchema = ResolveRef(prefixItems[0]["type"], schema);
                    if (itemSchema != null) {
                        var itemTypePath = itemSchema["typePath"]?.ToString();

                        // Special handling for glam types in enum variants
                        if (IsGlam(itemTypePath)) {
                            // Initialize glam data if needed
                            if (box.Data is not float[] glamData || glamData.Length != GetComponentCount(itemTypePath)) {
                                box.Data = new float[GetComponentCount(itemTypePath)];
                            }

                            // Draw the glam vector field
                            box.Data = DrawGlam(box.Data, "Value", itemTypePath);
                        } else {
                            // Standard handling for non-glam types
                            box.Data = DrawValue(box.Data, itemSchema, "Value", parentJustExpanded);
                        }
                    }
                }
                EditorGUI.indentLevel--;
            } else if (variantSchema["kind"]?.ToString() == "Struct") {
                EditorGUI.indentLevel++;
                box.Data = DrawValue(box.Data, variantSchema, "Data", parentJustExpanded);
                EditorGUI.indentLevel--;
            }
        }

        return box;
    }

    private object DrawOption(NewBevityComponents.EnumBox box, JObject schema, string label, bool parentJustExpanded) {
        // Create a new EnumBox if null
        if (box == null) {
            box = new NewBevityComponents.EnumBox();
        }

        // Determine current state
        bool isSome = box.Variant == "Some" && box.Data != null;

        // Create a toggle for None/Some with type label
        EditorGUILayout.BeginHorizontal();
        bool newIsSome = EditorGUILayout.Toggle(label, isSome);
        EditorGUILayout.LabelField(newIsSome ? "Some" : "None", GUILayout.Width(50));

        // Add type information
        if (schema["typePath"] != null) {
            GUILayout.Label(GetSimpleTypeName(schema["typePath"].ToString()), _typeStyle, GUILayout.Width(60));
        }
        EditorGUILayout.EndHorizontal();

        // Update variant based on toggle
        if (newIsSome != isSome) {
            box.Variant = newIsSome ? "Some" : "None";
            if (!newIsSome) {
                box.Data = null; // Clear data if None
            }
        }

        // If Some, draw the value field
        if (newIsSome) {
            // Find the Some variant schema
            JObject someVariant = null;
            if (schema["oneOf"] is JArray oneOf) {
                foreach (var variant in oneOf) {
                    if (variant is JObject obj && obj["shortPath"]?.ToString() == "Some") {
                        someVariant = obj;
                        break;
                    }
                }
            }

            if (someVariant != null && someVariant["kind"]?.ToString() == "Tuple") {
                EditorGUI.indentLevel++;
                if (someVariant["prefixItems"] is JArray prefixItems && prefixItems.Count > 0) {
                    var itemSchema = ResolveRef(prefixItems[0]["type"], schema);
                    if (itemSchema != null) {
                        box.Data = DrawValue(box.Data, itemSchema, "Value", parentJustExpanded);
                    }
                }
                EditorGUI.indentLevel--;
            }
        }

        return box;
    }

    private object DrawList(IList<object> list, JObject schema, string label, bool parentJustExpanded) {
        list ??= new List<object>();

        // Create a foldout for the list
        string foldoutKey = $"list_{label}";
        _foldouts.TryGetValue(foldoutKey, out bool open);

        // If parent was just expanded, also expand this
        if (parentJustExpanded && !open) {
            open = true;
            _foldouts[foldoutKey] = open;
        }

        // Draw header with size controls and type information
        EditorGUILayout.BeginHorizontal();
        open = EditorGUILayout.Foldout(open, label, true);

        // Add type information between foldout and size controls
        if (schema["typePath"] != null) {
            GUILayout.Label(GetSimpleTypeName(schema["typePath"].ToString()), _typeStyle, GUILayout.Width(60));
        }

        GUILayout.FlexibleSpace();

        // Show size field and +/- buttons
        GUILayout.Label("Size:", GUILayout.Width(40));
        int count = EditorGUILayout.IntField(list.Count, GUILayout.Width(50));

        if (GUILayout.Button("+", GUILayout.Width(24)))
            count++;

        if (GUILayout.Button("-", GUILayout.Width(24)) && count > 0)
            count--;

        EditorGUILayout.EndHorizontal();

        _foldouts[foldoutKey] = open;

        // Update list size
        if (count != list.Count) {
            if (count < list.Count) {
                while (list.Count > count)
                    list.RemoveAt(list.Count - 1);
            } else {
                var itemSchema = ResolveRef(schema["items"]["type"], schema);
                while (list.Count < count)
                    list.Add(CreateDefault(itemSchema));
            }
        }

        // Track if we just opened this list
        bool thisJustExpanded = parentJustExpanded || (open && !_foldouts.ContainsKey(foldoutKey));
        _foldouts[foldoutKey] = open;

        // Draw list items
        if (open && count > 0) {
            EditorGUI.indentLevel++;

            var itemSchema = ResolveRef(schema["items"]["type"], schema);
            if (itemSchema != null) {
                // Show item type once at the top of the list
                var itemTypePath = itemSchema["typePath"]?.ToString();
                if (!string.IsNullOrEmpty(itemTypePath)) {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Item Type:", GUILayout.Width(65));
                    EditorGUILayout.LabelField(GetSimpleTypeName(itemTypePath), _typeStyle);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space(2);
                }

                for (int i = 0; i < list.Count; i++) {
                    var newValue = DrawValue(list[i], itemSchema, $"[{i}]", thisJustExpanded);
                    if (!ReferenceEquals(list[i], newValue))
                        list[i] = newValue;
                }
            }

            EditorGUI.indentLevel--;
        }

        return list;
    }

    /*—————————— Helpers ——————————*/

    // Helper method to extract a simplified type name from a full path
    private string GetSimpleTypeName(string typePath) {
        if (string.IsNullOrEmpty(typePath))
            return "";

        // Handle primitive types
        if (typePath == "f32") return "f32";
        if (typePath == "f64") return "f64";
        if (typePath == "bool") return "bool";
        if (typePath.StartsWith("i") && typePath.Length <= 3) return typePath; // i32, i64, etc.
        if (typePath.StartsWith("u") && typePath.Length <= 3) return typePath; // u32, u64, etc.

        // Handle strings
        if (typePath.Contains("::string::String")) return "String";
        if (typePath.Contains("::Cow<str>")) return "Str";

        // Handle Glam vector types
        if (typePath.Contains("Vec2")) return "Vec2";
        if (typePath.Contains("Vec3")) return "Vec3";
        if (typePath.Contains("Vec4")) return "Vec4";
        if (typePath.Contains("Quat")) return "Quat";
        if (typePath.EndsWith("IVec2")) return "IVec2";
        if (typePath.EndsWith("UVec2")) return "UVec2";
        if (typePath.EndsWith("UVec3")) return "UVec3";
        if (typePath.EndsWith("DVec2")) return "DVec2";

        // For general types, get the part after the last ::
        if (typePath.Contains("::")) {
            return typePath[(typePath.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
        }

        // For collection types like Vec<T>, Option<T>, etc.
        if (typePath.Contains("<") && typePath.EndsWith(">")) {
            var baseName = typePath[..typePath.IndexOf("<")];
            if (baseName.Contains("::")) {
                baseName = baseName[(baseName.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
            }

            // Get the inner type 
            var innerStart = typePath.IndexOf("<") + 1;
            var innerEnd = typePath.LastIndexOf(">");
            var innerTypePath = typePath[innerStart..innerEnd];

            var innerType = GetSimpleTypeName(innerTypePath);
            return $"{baseName}<{innerType}>";
        }

        // Default case - just return as is
        return typePath;
    }

    // Identify if a component is simple enough to not need nested foldouts
    private bool IsSimpleComponent(JObject schema) {
        if (schema == null) return false;

        var kind = schema["kind"]?.ToString();

        // Simple enum without data fields
        if (kind == "Enum") {
            if (schema["oneOf"] is JArray oneOf) {
                bool hasComplexVariants = false;

                foreach (var variant in oneOf) {
                    if (variant is JObject varObj && varObj["kind"] != null) {
                        hasComplexVariants = true;
                        break;
                    }
                }

                return !hasComplexVariants;
            }

            return true; // No variants means simple (unlikely case)
        }

        // Simple primitive values
        if (kind == "Value") {
            return true;
        }

        // For all components - no extra nesting
        return true;
    }


    private void RefreshAvailableTypes() {
        var dict = GetSchemaDictionary();
        if (dict == null || dict.Count == 0) {
            _availableTypes = new string[0];
            _filteredTypes = new string[0];
            return;
        }

        _availableTypes = dict.Keys.OrderBy(k => k).ToArray();
        FilterAvailableTypes();
    }

    private bool IsComponent(JObject schema) {
        if (schema == null) return false;

        // Check if the schema has reflectTypes field
        if (schema["reflectTypes"] is JArray reflectTypes) {
            // Check if "Component" is in the reflectTypes array
            foreach (var type in reflectTypes) {
                if (type.ToString() == "Component") {
                    return true;
                }
            }
        }

        return false;
    }

    private void FilterAvailableTypes() {
        var dict = GetSchemaDictionary();

        // Start with all available types
        var filteredList = _availableTypes;

        // Filter by component type (if types exist and dictionary is available)
        if (filteredList.Length > 0 && dict != null) {
            filteredList = filteredList
                .Where(typePath => IsComponent(dict.GetValueOrDefault(typePath)))
                .ToArray();
        }

        // Then filter by search text if needed
        if (!string.IsNullOrWhiteSpace(_searchText)) {
            filteredList = filteredList
                .Where(t => t.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
        }

        _filteredTypes = filteredList;
    }

    private static Dictionary<string, JObject> GetSchemaDictionary() => typeof(NewBevityComponents)
        .GetField("_schemas", BindingFlags.Static | BindingFlags.NonPublic)
        ?.GetValue(null) as Dictionary<string, JObject>;

    private static JObject GetSchema(string typePath) {
        var dict = GetSchemaDictionary();
        return dict != null && dict.TryGetValue(typePath, out var schema) ? schema : null;
    }

    private static bool IsGlam(string tp) {
        var field = typeof(NewBevityComponents).GetField("_glamTypes", BindingFlags.Static | BindingFlags.NonPublic);
        var glamTypes = field?.GetValue(null) as HashSet<string>;
        return glamTypes?.Contains(tp) ?? false;
    }

    private static string Simplify(string tp) => tp.Contains("::") ? tp[(tp.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : tp;

    private static JObject ResolveRef(JToken token, JObject root) {
        var m = typeof(NewBevityComponents).GetMethod("ResolveRef", BindingFlags.Static | BindingFlags.NonPublic);
        return (JObject)m.Invoke(null, new object[] { token, root });
    }

    private static object CreateDefault(JObject schema) {
        var m = typeof(NewBevityComponents).GetMethod("CreateDefault", BindingFlags.Static | BindingFlags.NonPublic);
        return m.Invoke(null, new object[] { schema });
    }
}