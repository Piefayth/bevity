using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class BevityComponents : MonoBehaviour
{
    // SERIALIZED DATA THAT MUST SURVIVE DOMAIN RELOADS
    [SerializeField] private string _json = "{}"; // Store the JSON representation of all components
    [SerializeField] private BevityRegistry _registry;

    // RUNTIME STATE - reconstructed when OnEnable runs
    private readonly Dictionary<string, object> _components = new();

    // STATIC CACHE - will be cleared on domain reload 
    private static readonly Dictionary<string, JObject> _schemas = new();

    // Helper for handling special glam vector types
    private static readonly HashSet<string> _glamTypes = new() {
        "glam::Vec2", "glam::Vec3", "glam::Vec4", "glam::Quat",
        "glam::DVec2", "glam::IVec2", "glam::UVec2", "glam::UVec3"
    };

    private static bool IsGlam(string typePath) => _glamTypes.Contains(typePath);

    // ============================ PUBLIC API ============================

    public IEnumerable<string> ComponentTypes => _components.Keys;

    public void LoadSchemasFromRegistry() {
        if (_registry == null) {
            _registry = FindAnyObjectByType<BevityRegistry>();
            if (_registry == null) {
                Debug.LogError("No BevityRegistry found!");
                return;
            }
        }

        if (string.IsNullOrEmpty(_registry.jsonResponse)) {
            Debug.LogError("BevityRegistry has no JSON response!");
            return;
        }

        try {
            var rootObj = JObject.Parse(_registry.jsonResponse);

            if (rootObj["result"] is not JObject resultObj) {
                Debug.LogError("Invalid schema format in registry response!");
                return;
            }

            _schemas.Clear();

            foreach (var prop in resultObj.Properties()) {
                _schemas[prop.Name] = (JObject)prop.Value;
            }

            Debug.Log($"[NewBevityComponents] Loaded {_schemas.Count} component schemas");

            // After schemas are loaded, deserialize any pending components
            DeserializeComponents();
        } catch (Exception ex) {
            Debug.LogError($"Error loading schemas: {ex.Message}");
        }
    }

    public void AddComponent(string typePath) {
        if (_components.ContainsKey(typePath)) return;

        if (!_schemas.TryGetValue(typePath, out var schema)) {
            Debug.LogError($"Schema for {typePath} not found - call LoadSchemas first");
            return;
        }

        _components[typePath] = CreateDefault(schema);
        SaveComponents();
    }

    public void RemoveComponent(string typePath) {
        if (_components.Remove(typePath)) {
            SaveComponents();
        }
    }

    public object GetValue(string typePath) =>
        _components.TryGetValue(typePath, out var value) ? value : null;

    public void SetValue(string typePath, object value) {
        if (_components.ContainsKey(typePath)) {
            _components[typePath] = value;
            SaveComponents();
        }
    }

    public JArray ToBevyJson() {
        // Convert from object format {"Type1": {...}, "Type2": {...}}
        // to array format [{"Type1": {...}}, {"Type2": {...}}]
        try {
            if (string.IsNullOrEmpty(_json) || _json == "{}") {
                return new JArray(); // Empty array if no components
            }

            var objRoot = JObject.Parse(_json);
            var arrayRoot = new JArray();

            foreach (var prop in objRoot.Properties()) {
                // Create a new object for each component
                var componentObj = new JObject {
                    [prop.Name] = prop.Value
                };
                arrayRoot.Add(componentObj);
            }

            return arrayRoot;
        } catch (Exception ex) {
            Debug.LogError($"Error converting to Bevy JSON format: {ex.Message}");
            return new JArray();
        }
    }

    // ============================ LIFECYCLE METHODS ============================

    private void OnEnable() {
        // During domain reload, we need to restore components from serialized JSON
        DeserializeComponents();
    }

    private void OnValidate() {
        if (_schemas.Count == 0) {
            LoadSchemasFromRegistry();
        }
    }

    // ============================ SERIALIZATION CORE ===========================

    private void SaveComponents() {
        try {
            // We'll keep the internal _json as an object format for simplicity in loading/saving
            var root = new JObject();

            foreach (var (typePath, value) in _components) {
                if (_schemas.TryGetValue(typePath, out var schema)) {
                    var serialized = SerializeValue(value, schema);
                    root[typePath] = serialized;
                } else {
                    Debug.LogWarning($"No schema found for {typePath}");
                }
            }

            _json = root.ToString(Newtonsoft.Json.Formatting.None);

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        } catch (Exception ex) {
            Debug.LogError($"Error serializing components: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void DeserializeComponents() {
        _components.Clear();

        if (string.IsNullOrEmpty(_json) || _json == "{}") {
            return; // Nothing to deserialize
        }

        try {
            var root = JObject.Parse(_json);
            foreach (var prop in root.Properties()) {
                string typePath = prop.Name;
                JToken value = prop.Value;

                // Only deserialize if schema is available
                if (_schemas.TryGetValue(typePath, out var schema)) {
                    _components[typePath] = DeserializeValue(value, schema);
                } else {
                    Debug.LogWarning($"Schema for {typePath} not available, component will be loaded when schema is available");
                }
            }

            Debug.Log($"Deserialized {_components.Count} components from saved data");
        } catch (Exception ex) {
            Debug.LogError($"Error deserializing components: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ============================ VALUE SERIALIZATION ===========================

    private static JToken SerializeValue(object value, JObject schema) {
        if (value == null) return JValue.CreateNull();

        var kind = schema["kind"]?.ToString();
        var typePath = schema["typePath"]?.ToString();

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(typePath)) {
            return JValue.CreateNull();
        }

        // Special handling for Option<T> type - check before other types
        if (typePath.StartsWith("core::option::Option<")) {
            // Handle Option<T> type (Some/None)
            if (value is EnumBox optionBox) {
                // None case
                if (optionBox.Variant == "None" || optionBox.Data == null) {
                    return JValue.CreateNull();
                }

                // Some case - get the inner type and serialize the wrapped value directly
                if (schema["oneOf"] is JArray optionOneOf && optionOneOf.Count >= 2) {
                    JObject someVariant = null;
                    foreach (var variant in optionOneOf) {
                        if (variant is JObject obj && obj["shortPath"]?.ToString() == "Some") {
                            someVariant = obj;
                            break;
                        }
                    }

                    if (someVariant != null && someVariant["kind"]?.ToString() == "Tuple") {
                        var innerSchema = ResolveRef(someVariant["prefixItems"][0]["type"]);
                        if (innerSchema != null) {
                            // Important: Return the inner value directly, not wrapped in "Some"
                            return SerializeValue(optionBox.Data, innerSchema);
                        }
                    }
                }

                // Fallback - should rarely happen
                return optionBox.Data != null ? JToken.FromObject(optionBox.Data) : JValue.CreateNull();
            }

            return JValue.CreateNull();
        }

        switch (kind) {
            case "Struct":
                var jo = new JObject();

                // Handle unit structs (no properties)
                if (schema["properties"] is not JObject props) {
                    return JValue.CreateNull();
                }

                var dict = value as Dictionary<string, object> ?? new Dictionary<string, object>();

                foreach (var p in props.Properties()) {
                    if (dict.TryGetValue(p.Name, out var fieldVal)) {
                        var fieldSchema = ResolveRef(p.Value["type"]);
                        if (fieldSchema != null) {
                            // Check if this field is a Glam type
                            var fieldTypePath = fieldSchema["typePath"]?.ToString();
                            if (IsGlam(fieldTypePath)) {
                                // Use SerializeGlam directly for Glam fields
                                jo[p.Name] = SerializeGlam(fieldVal, fieldTypePath);
                            } else {
                                jo[p.Name] = SerializeValue(fieldVal, fieldSchema);
                            }
                        }
                    }
                }
                return jo;

            case "TupleStruct":
                var items = (JArray)schema["prefixItems"];

                // Special direct handling for Glam types
                if (IsGlam(typePath)) {
                    return SerializeGlam(value, typePath);
                }

                // Handle single-field tuple struct that contains a glam type
                if (items.Count == 1) {
                    var innerSchema = ResolveRef(items[0]["type"]);
                    if (innerSchema != null) {
                        var innerTypePath = innerSchema["typePath"]?.ToString();
                        if (IsGlam(innerTypePath)) {
                            // Extract value from the list or use directly if it's already a glam value
                            object innerValue = value;
                            if (value is IList<object> lllist && lllist.Count > 0) {
                                innerValue = lllist[0];
                            }

                            // Serialize the glam value directly 
                            return SerializeGlam(innerValue, innerTypePath);
                        }

                        // Handle as single value or extract from list
                        if (value is IList<object> llist && llist.Count > 0) {
                            return SerializeValue(llist[0], innerSchema);
                        }
                        return SerializeValue(value, innerSchema);
                    }
                }

                // Handle multi-field tuple struct
                var array = value as IList<object> ?? new List<object>();
                var ja = new JArray();

                for (int ii = 0; ii < items.Count; ii++) {
                    var iitemSchema = ResolveRef(items[ii]["type"]);
                    if (iitemSchema != null) {
                        ja.Add(SerializeValue(ii < array.Count ? array[ii] : null, iitemSchema));
                    }
                }

                return ja;

            case "Enum":
                var enumBox = value as EnumBox ?? new EnumBox();

                // Check if we have a valid variant name
                if (string.IsNullOrEmpty(enumBox.Variant)) {
                    Debug.LogWarning($"Enum {typePath} has no variant selected, returning empty string");
                    return new JValue("");
                }

                // Simple enum (no data)
                if (enumBox.Data == null) {
                    // Simple enum case - just return the variant name
                    return new JValue(enumBox.Variant);
                }

                // Complex enum with data
                var variantObj = new JObject();
                var variantSchema = FindVariantSchema(schema, enumBox.Variant);

                if (variantSchema == null) {
                    Debug.LogWarning($"Could not find variant schema for {enumBox.Variant} in {typePath}");
                    variantObj[enumBox.Variant] = JValue.CreateNull();
                    return variantObj;
                }

                // Check if it's a tuple variant (most common)
                if (variantSchema["kind"]?.ToString() == "Tuple") {
                    if (variantSchema["prefixItems"] is JArray prefixItems && prefixItems.Count > 0) {
                        var innerSchema = ResolveRef(prefixItems[0]["type"]);
                        if (innerSchema != null) {
                            var innerTypePath = innerSchema["typePath"]?.ToString();

                            // Special case for Glam types (Vec2, Vec3, etc.)
                            if (IsGlam(innerTypePath)) {
                                // Ensure the data is properly formatted as float[]
                                var token = SerializeGlam(enumBox.Data, innerTypePath);
                                variantObj[enumBox.Variant] = token;
                            } else {
                                var token = SerializeValue(enumBox.Data, innerSchema);
                                variantObj[enumBox.Variant] = token;
                            }
                        } else {
                            Debug.LogWarning($"Could not resolve inner schema for tuple variant {enumBox.Variant}");
                            variantObj[enumBox.Variant] = JValue.CreateNull();
                        }
                    } else {
                        Debug.LogWarning($"Tuple variant {enumBox.Variant} has no prefixItems");
                        variantObj[enumBox.Variant] = JValue.CreateNull();
                    }
                } else {
                    // Struct variant - serialize the data normally
                    Debug.Log($"Struct variant: {enumBox.Variant}");
                    variantObj[enumBox.Variant] = SerializeValue(enumBox.Data, variantSchema);
                }

                return variantObj;

            case "List":
            case "Array":
                var list = value as IList<object> ?? new List<object>();
                var itemSchema = ResolveRef(schema["items"]["type"]);

                if (itemSchema != null) {
                    var jArray = new JArray();
                    foreach (var item in list) {
                        jArray.Add(SerializeValue(item, itemSchema));
                    }
                    return jArray;
                }

                return new JArray();

            case "Value":
                if (IsGlam(typePath)) {
                    return SerializeGlam(value, typePath);
                }

                // Handle primitive types
                if (value is bool b) return new JValue(b);
                if (value is int i) return new JValue(i);
                if (value is float f) return new JValue(f);
                if (value is double d) return new JValue(d);
                if (value is string s) return new JValue(s);

                // Try to convert other types
                return JToken.FromObject(value);

            default:
                Debug.LogWarning($"Unhandled schema kind: {kind} for type {typePath}");
                return JValue.CreateNull();
        }
    }

    private static object DeserializeValue(JToken token, JObject schema) {
        if (token == null || token.Type == JTokenType.Null || schema == null) {
            return null;
        }

        var kind = schema["kind"]?.ToString();
        var typePath = schema["typePath"]?.ToString();

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(typePath)) {
            return null;
        }

        // Special case for glam types - check first before any other processing
        if (IsGlam(typePath)) {
            return DeserializeGlam(token, typePath);
        }

        // Special handling for Option<T> type
        if (typePath.StartsWith("core::option::Option<")) {
            // If the token is null, this is a None variant
            if (token.Type == JTokenType.Null) {
                return new EnumBox { Variant = "None" };
            }

            // Otherwise, it's a Some variant with the value directly stored (not wrapped)
            // We need to find the inner type and parse the value
            if (schema["oneOf"] is JArray optionOneOf && optionOneOf.Count >= 2) {
                JObject someVariant = null;
                foreach (var variant in optionOneOf) {
                    if (variant is JObject obj && obj["shortPath"]?.ToString() == "Some") {
                        someVariant = obj;
                        break;
                    }
                }

                if (someVariant != null && someVariant["kind"]?.ToString() == "Tuple") {
                    var innerSchema = ResolveRef(someVariant["prefixItems"][0]["type"]);
                    if (innerSchema != null) {
                        var innerValue = DeserializeValue(token, innerSchema);
                        return new EnumBox {
                            Variant = "Some",
                            Data = innerValue
                        };
                    }
                }
            }

            // Fallback
            var value = token.Type == JTokenType.Null ? null : token.ToObject<object>();
            return new EnumBox {
                Variant = value == null ? "None" : "Some",
                Data = value
            };
        }

        switch (kind) {
            case "Struct":
                var dict = new Dictionary<string, object>();

                // Handle unit structs - they can be serialized as null or empty objects
                if (schema["properties"] is not JObject props) {
                    return dict; // Return empty dict for unit struct regardless of input
                }

                if (token is JObject tokenObj) {
                    foreach (var p in props.Properties()) {
                        if (tokenObj.TryGetValue(p.Name, out var childToken)) {
                            var propSchema = ResolveRef(p.Value["type"]);
                            if (propSchema != null) {
                                // Check if this property is a glam type
                                var propTypePath = propSchema["typePath"]?.ToString();
                                if (IsGlam(propTypePath)) {
                                    dict[p.Name] = DeserializeGlam(childToken, propTypePath);
                                } else {
                                    dict[p.Name] = DeserializeValue(childToken, propSchema);
                                }
                            }
                        }
                    }
                }

                return dict;

            case "TupleStruct":
                var items = (JArray)schema["prefixItems"];

                // Handle single-field tuple struct with a glam type
                if (items.Count == 1) {
                    var innerSchema = ResolveRef(items[0]["type"]);
                    if (innerSchema != null) {
                        var innerTypePath = innerSchema["typePath"]?.ToString();
                        if (IsGlam(innerTypePath)) {
                            // For glam types, deserialize directly and wrap in list
                            var glamValue = DeserializeGlam(token, innerTypePath);
                            return new List<object> { glamValue };
                        }

                        // For non-glam types 
                        var innerValue = DeserializeValue(token, innerSchema);
                        return new List<object> { innerValue };
                    }
                }

                // Handle multi-field tuple struct
                var list = new List<object>();

                if (token is JArray tokenArr) {
                    for (int i = 0; i < Math.Min(items.Count, tokenArr.Count); i++) {
                        var itemSchema = ResolveRef(items[i]["type"]);
                        if (itemSchema != null) {
                            list.Add(DeserializeValue(tokenArr[i], itemSchema));
                        }
                    }
                }

                return list;

            case "Enum":
                // Simple enum (string)
                if (token.Type == JTokenType.String) {
                    return new EnumBox { Variant = token.ToString() };
                }

                // Complex enum (with data)
                if (token is JObject enumObj && enumObj.Count > 0) {
                    var prop = enumObj.Properties().First();
                    var box = new EnumBox { Variant = prop.Name };

                    var variantSchema = FindVariantSchema(schema, prop.Name);
                    if (variantSchema != null) {
                        if (variantSchema["kind"]?.ToString() == "Tuple") {
                            var innerSchema = ResolveRef(variantSchema["prefixItems"][0]["type"]);
                            if (innerSchema != null) {
                                var innerTypePath = innerSchema["typePath"]?.ToString();

                                // Special handling for glam types in enum variants
                                if (IsGlam(innerTypePath)) {
                                    box.Data = DeserializeGlam(prop.Value, innerTypePath);
                                } else {
                                    box.Data = DeserializeValue(prop.Value, innerSchema);
                                }
                            }
                        } else // Struct variant
                          {
                            box.Data = DeserializeValue(prop.Value, variantSchema);
                        }
                    }

                    return box;
                }

                return new EnumBox();

            case "List":
            case "Array":
                if (token is JArray listArr) {
                    var itemSchema = ResolveRef(schema["items"]["type"]);
                    if (itemSchema != null) {
                        return listArr.Select(t => DeserializeValue(t, itemSchema)).ToList();
                    }
                }

                return new List<object>();

            case "Value":
                if (IsGlam(typePath)) {
                    return DeserializeGlam(token, typePath);
                }

                if (token is JValue jval) {
                    // Convert JValue to appropriate type
                    switch (typePath) {
                        case "f32":
                        case "f64":
                            return Convert.ToSingle(jval.Value);
                        case "bool":
                            return Convert.ToBoolean(jval.Value);
                        case "alloc::string::String":
                        case var s when s.Contains("::Cow<str>"):
                            return jval.Value?.ToString() ?? "";
                        case var i when i.StartsWith("i") || i.StartsWith("u"):
                            return Convert.ToInt32(jval.Value);
                        default:
                            return jval.Value;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    // ============================ HELPER METHODS ============================

    private static JToken SerializeGlam(object value, string typePath) {
        float[] floatValues;

        if (value is float[] floatArray) {
            floatValues = floatArray;
        } else if (value is IEnumerable<float> floatEnum) {
            floatValues = floatEnum.ToArray();
        } else if (value is IList<object> objList) {
            floatValues = objList.Select(v => {
                if (v is float f) return f;
                try {
                    return Convert.ToSingle(v);
                } catch {
                    return 0f;
                }
            }).ToArray();
        } else if (value is Dictionary<string, object> dict) {
            // Extract components from dictionary (e.g. {"x": 1, "y": 2, "z": 3})
            int components = GetComponentCount(typePath);
            floatValues = new float[components];

            string[] componentNames = { "x", "y", "z", "w" };
            for (int i = 0; i < components && i < componentNames.Length; i++) {
                if (dict.TryGetValue(componentNames[i], out var comp) && comp != null) {
                    try {
                        floatValues[i] = Convert.ToSingle(comp);
                    } catch {
                        floatValues[i] = 0f;
                    }
                }
            }
        } else {
            // Default to appropriate size array based on type
            int components = GetComponentCount(typePath);
            floatValues = new float[components];
        }

        // Explicitly create a JArray to ensure array format
        return new JArray(floatValues.Cast<object>().ToArray());
    }

    private static object DeserializeGlam(JToken token, string typePath) {
        // Get the number of components for this glam type
        int components = GetComponentCount(typePath);
        float[] floatValues = new float[components];

        // If token is a JArray (the normal case), extract values
        if (token is JArray jArray) {
            for (int i = 0; i < Math.Min(components, jArray.Count); i++) {
                floatValues[i] = jArray[i].Value<float>();
            }
            return floatValues;
        }

        // If token is a JObject with x,y,z properties (from old format or editor changes)
        if (token is JObject jObj) {
            string[] componentNames = { "x", "y", "z", "w" };
            for (int i = 0; i < Math.Min(components, componentNames.Length); i++) {
                if (jObj.TryGetValue(componentNames[i], out var val)) {
                    floatValues[i] = val.Value<float>();
                }
            }
            return floatValues;
        }

        // Default case - just return a zero-filled array of the right size
        return floatValues;
    }

    private static int GetComponentCount(string typePath) {
        if (typePath.Contains("Vec2") || typePath.EndsWith("IVec2") || typePath.EndsWith("UVec2") || typePath.EndsWith("DVec2"))
            return 2;
        if (typePath.Contains("Vec3") || typePath.EndsWith("UVec3"))
            return 3;
        return 4; // Vec4, Quat, etc.
    }

    private static JObject ResolveRef(JToken token) {
        if (token is JObject o && o.TryGetValue("$ref", out var refToken)) {
            string refPath = refToken.ToString();

            // Handle schema reference format: "#/$defs/type::path::Type"
            string typePath = refPath.Contains("/")
                ? refPath.Split('/').Last()
                : refPath;

            // Try to resolve from schema cache
            if (_schemas.TryGetValue(typePath, out var schema)) {
                return schema;
            } else {
                Debug.LogWarning($"Could not resolve schema reference: {typePath}");
            }
        }

        return token as JObject;
    }

    private static JObject FindVariantSchema(JObject enumSchema, string shortPath) {

        if (enumSchema?["oneOf"] is JArray oneOf) {
            foreach (var variant in oneOf) {
                if (variant is JObject varObj) {
                    if (varObj["shortPath"]?.ToString() == shortPath) {
                        return varObj;
                    }
                }
            }
        } else {
            Debug.LogWarning($"Schema does not have oneOf array: {enumSchema?.ToString(Newtonsoft.Json.Formatting.None)}");
        }

        return null;
    }

    private static object CreateDefault(JObject schema) {
        if (schema == null) return null;

        var kind = schema["kind"]?.ToString();
        var typePath = schema["typePath"]?.ToString();

        if (string.IsNullOrEmpty(kind)) return null;

        switch (kind) {
            case "Struct":
                var dict = new Dictionary<string, object>();
                if (schema["properties"] is JObject props) {
                    foreach (var p in props.Properties()) {
                        var propSchema = ResolveRef(p.Value["type"]);
                        if (propSchema != null) {
                            dict[p.Name] = CreateDefault(propSchema);

                        }
                    }
                }
                return dict;

            case "TupleStruct":
                if (schema["prefixItems"] is not JArray items) return new List<object>();

                // Special case for glam types
                if (items.Count == 1 && IsGlam(typePath)) {
                    int components = GetComponentCount(typePath);
                    return new float[components];
                }

                var list = new List<object>();
                foreach (var item in items) {
                    var itemSchema = ResolveRef(item["type"]);
                    list.Add(CreateDefault(itemSchema));
                }
                return list;

            case "Enum":
                if (schema["oneOf"] is not JArray oneOf || oneOf.Count == 0) return new EnumBox();

                // Get the first variant as default
                var first = oneOf[0];

                if (first is JObject fo && fo["shortPath"] != null) {
                    var variant = fo["shortPath"].ToString();

                    // Create an empty EnumBox with just the variant name for simple enum
                    if (fo["kind"] == null) {
                        return new EnumBox { Variant = variant };
                    }

                    // For complex variants, create the inner data too
                    object data = null;
                    if (fo["kind"]?.ToString() == "Tuple" && fo["prefixItems"] is JArray prefixItems && prefixItems.Count > 0) {
                        var innerSchema = ResolveRef(prefixItems[0]["type"]);
                        if (innerSchema != null) {
                            data = CreateDefault(innerSchema);
                        }
                    } else {
                        data = CreateDefault(fo);
                    }

                    return new EnumBox {
                        Variant = variant,
                        Data = data
                    };
                }

                if (first is JValue v) {
                    // Simple enum
                    return new EnumBox { Variant = v.ToString() };
                }

                return new EnumBox();

            case "Option":
                // Default to None for Option<T>
                return new EnumBox { Variant = "None" };

            case "List":
            case "Array":
                return new List<object>();

            case "Value":
                if (IsGlam(typePath)) {
                    int components = GetComponentCount(typePath);
                    return new float[components];
                }

                // Default values for primitive type
                switch (typePath) {
                    case "f32":
                    case "f64":
                        return 0.0f;
                    case "bool":
                        return false;
                    case "alloc::string::String":
                    case var s when s.Contains("::Cow<str>"):
                        return "";
                    case var i when i.StartsWith("i") || i.StartsWith("u"):
                        return 0;
                    default:
                        return null;
                }

            default:
                return null;
        }
    }


    [Serializable]
    public class EnumBox
    {
        public string Variant;
        public object Data;

        public override string ToString() {
            return $"EnumBox{{Variant={Variant}, Data={Data}}}";
        }
    }
}
