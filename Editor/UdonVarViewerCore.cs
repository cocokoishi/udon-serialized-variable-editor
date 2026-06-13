#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Serialization.OdinSerializer;

using OdinUtility = VRC.Udon.Serialization.OdinSerializer.SerializationUtility;

namespace UdonVarViewer
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Data Models
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Single editable variable parsed from an Udon variable table.</summary>
    public class EditableVariable
    {
        public string       Name;
        public string       TypeName;
        public string       ValueDisplay;
        public object       RefObject;
        public PropertyInfo ValueProperty;
        public bool         IsReadOnly;
        public bool         IsExpanded;
    }

    /// <summary>Complete variable set for one UdonBehaviour, including its deserialized table.</summary>
    public class BehaviourVariableSet
    {
        public UdonBehaviour            Behaviour;
        public object                   Table;
        public List<EditableVariable>   Variables    = new List<EditableVariable>();
        public bool                     IsDirty;
        public string                   OriginalBase64;
        public DataFormat               Format       = DataFormat.Binary;
        public List<UnityEngine.Object> UnityObjects;
        public bool                     IsLoaded;
        public string                   ErrorMessage;
        public bool                     IsExpanded;  // for scene browser UI

        /// <summary>Returns false if the underlying Behaviour has been destroyed.</summary>
        public bool IsValid => Behaviour != null;

        public string DisplayName => IsValid ? Behaviour.name : "(destroyed)";

        public string GameObjectPath => IsValid ? GetFullPath(Behaviour.transform) : "";

        private static string GetFullPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetFullPath(t.parent) + "/" + t.name;
        }
    }

    public class LogEntry
    {
        public string   Message;
        public LogLevel Level;
        public string   Time;
    }

    public enum LogLevel  { Info, Warning, Error }
    public enum ToolState { Idle, Loaded, Modified, Saved, Error }

    // ═══════════════════════════════════════════════════════════════════════
    //  Shared Styles  (pre-cached, built once per domain reload)
    // ═══════════════════════════════════════════════════════════════════════

    public class SharedStyles
    {
        public GUIStyle VarBox;
        public GUIStyle VarName;
        public GUIStyle TypeBadge;
        public GUIStyle ReadOnly;
        public GUIStyle StatusBar;
        public GUIStyle SectionHeader;
        public GUIStyle LogInfo;
        public GUIStyle LogWarning;
        public GUIStyle LogError;
        public GUIStyle DirtyBadge;
        public GUIStyle PathLabel;

        private bool _built;

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            VarBox = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 6, 6),
                margin  = new RectOffset(0, 0, 2, 2),
            };
            VarName = new GUIStyle(EditorStyles.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 12,
                richText  = true,
            };
            TypeBadge = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal    = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };
            ReadOnly = new GUIStyle(EditorStyles.label)
            {
                normal   = { textColor = new Color(0.55f, 0.55f, 0.55f) },
                wordWrap = true,
            };
            StatusBar = new GUIStyle(EditorStyles.toolbar)
            {
                padding     = new RectOffset(8, 8, 0, 0),
                fixedHeight = 22,
            };
            SectionHeader = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.5f, 0.5f, 0.5f) },
            };
            LogInfo = new GUIStyle(EditorStyles.miniLabel)
            {
                normal   = { textColor = new Color(0.8f, 0.8f, 0.8f) },
                wordWrap = true,
            };
            LogWarning = new GUIStyle(EditorStyles.miniLabel)
            {
                normal   = { textColor = new Color(1f, 0.75f, 0.2f) },
                wordWrap = true,
            };
            LogError = new GUIStyle(EditorStyles.miniLabel)
            {
                normal   = { textColor = new Color(1f, 0.4f, 0.4f) },
                wordWrap = true,
            };
            DirtyBadge = new GUIStyle(EditorStyles.miniLabel)
            {
                normal    = { textColor = new Color(1f, 0.65f, 0.2f) },
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            PathLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                normal   = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                wordWrap = false,
                richText = true,
            };
        }

        public GUIStyle GetLogStyle(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error:   return LogError;
                case LogLevel.Warning: return LogWarning;
                default:               return LogInfo;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Utility  (static helper methods shared across all windows)
    // ═══════════════════════════════════════════════════════════════════════

    public static class UdonVarViewerUtility
    {
        // ─── Field-name constants ─────────────────────────────────────────
        public const string FIELD_SERIALIZED_BYTES = "serializedPublicVariablesBytesString";
        public const string FIELD_UNITY_OBJECTS    = "publicVariablesUnityEngineObjects";
        public const string FIELD_DATA_FORMAT      = "publicVariablesSerializationDataFormat";

        // ─── Shared styles singleton ──────────────────────────────────────
        private static SharedStyles _styles;
        public static SharedStyles Styles
        {
            get
            {
                if (_styles == null) _styles = new SharedStyles();
                _styles.EnsureBuilt();
                return _styles;
            }
        }

        // ─── Search Utility ───────────────────────────────────────────────
        public static string HighlightText(string text, string filter)
        {
            if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(text)) return text;
            try
            {
                return System.Text.RegularExpressions.Regex.Replace(
                    text,
                    System.Text.RegularExpressions.Regex.Escape(filter),
                    "<color=#FFFF00><b>$&</b></color>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
            }
            catch { return text; }
        }


        // ═════════════════════════════════════════════════════════════════
        //  Reflection
        // ═════════════════════════════════════════════════════════════════

        public static T GetField<T>(object obj, string fieldName, T defaultValue = default)
        {
            if (obj == null) return defaultValue;
            var fi = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null) return defaultValue;
            var v = fi.GetValue(obj);
            return v is T t ? t : defaultValue;
        }

        /// <summary>
        /// Sets a field value via reflection. Returns false and logs an error if
        /// the field cannot be found — prevents silent data loss.
        /// </summary>
        public static bool SetField(object obj, string fieldName, object value)
        {
            if (obj == null)
            {
                Debug.LogError($"[UdonVarViewer] SetField: target object is null (field: {fieldName})");
                return false;
            }
            var fi = obj.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null)
            {
                Debug.LogError($"[UdonVarViewer] SetField: field '{fieldName}' not found on {obj.GetType().Name}. " +
                               "The VRChat SDK version may have changed.");
                return false;
            }
            try
            {
                fi.SetValue(obj, value);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UdonVarViewer] SetField: failed to set '{fieldName}': {ex.Message}");
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Loading / Saving / Exporting
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Load and deserialize all public variables from an UdonBehaviour.</summary>
        public static BehaviourVariableSet LoadFromBehaviour(UdonBehaviour behaviour,
            Action<string, LogLevel> log = null)
        {
            var set = new BehaviourVariableSet { Behaviour = behaviour };

            if (behaviour == null)
            {
                set.ErrorMessage = "Behaviour is null.";
                log?.Invoke(set.ErrorMessage, LogLevel.Error);
                return set;
            }

            try
            {
                log?.Invoke($"Loading from '{behaviour.name}'…", LogLevel.Info);

                string base64 = GetField<string>(behaviour, FIELD_SERIALIZED_BYTES);
                if (string.IsNullOrEmpty(base64))
                {
                    set.ErrorMessage = "No serialized variable data.";
                    log?.Invoke($"Warning: {set.ErrorMessage}", LogLevel.Warning);
                    return set;
                }

                set.OriginalBase64 = base64;
                set.UnityObjects   = GetField<List<UnityEngine.Object>>(behaviour, FIELD_UNITY_OBJECTS);
                set.Format         = GetField<DataFormat>(behaviour, FIELD_DATA_FORMAT, DataFormat.Binary);

                log?.Invoke($"Unity Objects: {set.UnityObjects?.Count ?? 0}  Format: {set.Format}", LogLevel.Info);

                byte[] data = Convert.FromBase64String(base64.Trim());
                set.Table = OdinUtility.DeserializeValue<IUdonVariableTable>(
                    data, set.Format, set.UnityObjects, new DeserializationContext());

                if (set.Table == null)
                {
                    set.ErrorMessage = "Deserialization returned null — data may be corrupt.";
                    log?.Invoke(set.ErrorMessage, LogLevel.Error);
                    return set;
                }

                ParseObjectToVariables(set.Table, set.Variables, log);
                set.IsLoaded = true;
                log?.Invoke($"Loaded {set.Variables.Count} variables.", LogLevel.Info);
            }
            catch (FormatException ex)
            {
                set.ErrorMessage = $"Invalid Base64: {ex.Message}";
                log?.Invoke(set.ErrorMessage, LogLevel.Error);
                Debug.LogError($"[UdonVarViewer] {set.ErrorMessage}");
            }
            catch (Exception ex)
            {
                set.ErrorMessage = $"Load failed: {ex.Message}";
                log?.Invoke($"Exception: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
                Debug.LogError($"[UdonVarViewer] Load Error: {ex}");
            }

            return set;
        }

        /// <summary>Serialize the modified table back into the UdonBehaviour.</summary>
        public static bool SaveToBehaviour(BehaviourVariableSet set,
            Action<string, LogLevel> log = null)
        {
            if (set == null || set.Behaviour == null)
            {
                log?.Invoke("Save failed: behaviour reference is null or destroyed.", LogLevel.Error);
                return false;
            }
            if (set.Table == null)
            {
                log?.Invoke("Save failed: variable table is null.", LogLevel.Error);
                return false;
            }

            try
            {
                log?.Invoke($"Saving to '{set.Behaviour.name}'…", LogLevel.Info);

                List<UnityEngine.Object> unityObjects;
                byte[] bytes = OdinUtility.SerializeValue(
                    set.Table, set.Format, out unityObjects, new SerializationContext());
                string base64 = Convert.ToBase64String(bytes);

                log?.Invoke($"Serialized {bytes.Length} bytes, {unityObjects?.Count ?? 0} Unity Objects.",
                    LogLevel.Info);

                SerializedObject so = new SerializedObject(set.Behaviour);
                
                var bytesProp = so.FindProperty(FIELD_SERIALIZED_BYTES);
                if (bytesProp != null)
                {
                    bytesProp.stringValue = base64;
                }
                else
                {
                    log?.Invoke($"CRITICAL: failed to find SerializedProperty '{FIELD_SERIALIZED_BYTES}' — data NOT saved!", LogLevel.Error);
                    return false;
                }

                var objsProp = so.FindProperty(FIELD_UNITY_OBJECTS);
                if (objsProp != null)
                {
                    int count = unityObjects != null ? unityObjects.Count : 0;
                    objsProp.arraySize = count;
                    for (int i = 0; i < count; i++)
                    {
                        objsProp.GetArrayElementAtIndex(i).objectReferenceValue = unityObjects[i];
                    }
                }
                else
                {
                    log?.Invoke($"CRITICAL: failed to find SerializedProperty '{FIELD_UNITY_OBJECTS}' — references NOT saved!", LogLevel.Error);
                    return false;
                }

                so.ApplyModifiedProperties();
                
                // Force UdonBehaviour to re-parse the base64 string into its memory cache immediately
                if (set.Behaviour is ISerializationCallbackReceiver receiver)
                {
                    try
                    {
                        receiver.OnAfterDeserialize();
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Warning: OnAfterDeserialize threw an exception (Cache might not refresh): {ex.Message}", LogLevel.Warning);
                    }
                }
                set.IsDirty        = false;
                set.OriginalBase64 = base64;
                set.UnityObjects   = unityObjects;
                log?.Invoke("Save complete.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Save exception: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
                Debug.LogError($"[UdonVarViewer] Save Error: {ex}");
                return false;
            }
        }

        /// <summary>Export a table to Base64 (Unity Object references are stripped).</summary>
        public static string ExportToBase64(object table, Action<string, LogLevel> log = null)
        {
            if (table == null)
            {
                log?.Invoke("Export failed: table is null.", LogLevel.Error);
                return null;
            }

            try
            {
                List<UnityEngine.Object> discarded;
                byte[] bytes = OdinUtility.SerializeValue(
                    table, DataFormat.Binary, out discarded, new SerializationContext());
                string base64 = Convert.ToBase64String(bytes);
                log?.Invoke($"Exported {bytes.Length} bytes → Base64. " +
                            $"({discarded?.Count ?? 0} Unity Objects stripped — use Save to preserve them.)",
                    LogLevel.Info);
                return base64;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Export failed: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>Parse a Base64 string into a variable table.</summary>
        public static object ParseBase64(string base64,
            List<UnityEngine.Object> unityObjects = null,
            DataFormat format = DataFormat.Binary,
            Action<string, LogLevel> log = null)
        {
            if (string.IsNullOrWhiteSpace(base64))
            {
                log?.Invoke("Parse failed: Base64 input is empty.", LogLevel.Error);
                return null;
            }

            try
            {
                byte[] data = Convert.FromBase64String(base64.Trim());

                object table = OdinUtility.DeserializeValue<IUdonVariableTable>(
                    data, format, unityObjects, new DeserializationContext());

                if (table == null)
                {
                    // Fallback: try deserializing as raw object
                    table = OdinUtility.DeserializeValue<object>(
                        data, format, unityObjects, new DeserializationContext());
                }

                if (table == null)
                    log?.Invoke("Could not deserialize data.", LogLevel.Error);

                return table;
            }
            catch (FormatException)
            {
                log?.Invoke("Input is not a valid Base64 string.", LogLevel.Error);
                return null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Parse failed: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Variable Parsing
        // ═════════════════════════════════════════════════════════════════

        public static void ParseObjectToVariables(object tableObj, List<EditableVariable> output,
            Action<string, LogLevel> log = null)
        {
            if (tableObj == null || output == null) return;

            Type t = tableObj.GetType();
            log?.Invoke($"Table type: {t.FullName}", LogLevel.Info);

            IEnumerable list = null;

            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    if (field.GetValue(tableObj) is IEnumerable enumerable)
                    {
                        list = enumerable;
                        if (field.Name.Equals("variables", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }
                else if (field.GetValue(tableObj) is IDictionary dict)
                {
                    list = dict.Values;
                    break;
                }
            }

            if (list == null)
            {
                log?.Invoke("No variable container (List / Dictionary) found in table.", LogLevel.Error);
                return;
            }

            int count = 0;
            foreach (var item in list)
            {
                count++;
                ProcessLoadedVariable(item, output, log);
            }
            log?.Invoke($"Parsed {count} entries → {output.Count} displayable variables.", LogLevel.Info);
        }

        public static void ProcessLoadedVariable(object obj, List<EditableVariable> output,
            Action<string, LogLevel> log = null)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            // UdonVariable<T> type check via name — required because the concrete
            // generic type is internal to the VRC SDK.
            if (!t.Name.Contains("UdonVariable")) return;

            try
            {
                var propSymbol = t.GetProperty("SymbolName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var propValue = t.GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                string symbolName = propSymbol?.GetValue(obj, null) as string;
                if (string.IsNullOrEmpty(symbolName))
                {
                    // Fallback: try field instead of property
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.Name.Equals("SymbolName", StringComparison.OrdinalIgnoreCase))
                        {
                            symbolName = f.GetValue(obj) as string;
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(symbolName)) return;

                object value    = propValue?.GetValue(obj, null);
                Type   varType  = t.IsGenericType ? t.GetGenericArguments()[0] : typeof(object);
                string typeName = varType.Name;
                string valDisplay = value?.ToString() ?? "null";

                // If we have no setter, force read-only to avoid false "Modified" state
                bool readOnly = (propValue == null || !propValue.CanWrite);

                if (!readOnly)
                {
                    if (value == null ||
                        value is string || value is long || value is int ||
                        value is double || value is float || value is bool || value is char ||
                        value is Vector3 || value is Vector2 || value is Color || value is Quaternion ||
                        varType.IsEnum)
                    {
                        readOnly = false;
                    }
                    else if (value != null && value.GetType().IsArray)
                    {
                        readOnly   = false;
                        valDisplay = $"Array [{((Array)value).Length}]";
                    }
                    else
                    {
                        readOnly = true;
                    }
                }

                // Unity Object references are always read-only in this editor
                if (typeof(UnityEngine.Object).IsAssignableFrom(varType))
                {
                    readOnly = true;
                    valDisplay = (value == null || value.ToString() == "null")
                        ? $"None ({varType.Name})"
                        : $"{value} ({varType.Name})";
                }

                output.Add(new EditableVariable
                {
                    Name          = symbolName,
                    TypeName      = typeName,
                    ValueDisplay  = valDisplay,
                    RefObject     = obj,
                    ValueProperty = propValue,
                    IsReadOnly    = readOnly,
                    IsExpanded    = false,
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"Skipped variable: {ex.Message}", LogLevel.Warning);
            }
        }

        // ═════════════════════════════════════════════════════════════════
        //  Value Drawing  (shared UI components)
        // ═════════════════════════════════════════════════════════════════

        public static object DrawValueField(string label, object val)
        {
            var readOnlyStyle = Styles.ReadOnly;

            if (val == null)
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrEmpty(label) ? "" : label,
                    "null", readOnlyStyle);
                return null;
            }

            Type type = val.GetType();

            if (type == typeof(Vector3))    return EditorGUILayout.Vector3Field(label, (Vector3)val);
            if (type == typeof(Vector2))    return EditorGUILayout.Vector2Field(label, (Vector2)val);
            if (type.IsEnum)                return EditorGUILayout.EnumPopup(string.IsNullOrEmpty(label) ? "Enum" : label, (Enum)val);
            if (type == typeof(Color))      return EditorGUILayout.ColorField(label, (Color)val);
            if (type == typeof(bool))       return EditorGUILayout.Toggle(label, (bool)val);
            if (type == typeof(int))        return EditorGUILayout.IntField(label, (int)val);
            if (type == typeof(long))       return EditorGUILayout.LongField(label, (long)val);
            if (type == typeof(float))      return EditorGUILayout.FloatField(label, (float)val);
            if (type == typeof(double))     return EditorGUILayout.DoubleField(label, (double)val);
            if (type == typeof(string))     return EditorGUILayout.TextField(label, (string)val);

            if (type == typeof(Quaternion))
            {
                Quaternion q     = (Quaternion)val;
                Vector3 euler    = q.eulerAngles;
                Vector3 newEuler = EditorGUILayout.Vector3Field(
                    string.IsNullOrEmpty(label) ? "Euler" : label, euler);
                return newEuler != euler ? Quaternion.Euler(newEuler) : val;
            }

            if (type == typeof(char))
            {
                string s = EditorGUILayout.TextField(label, val.ToString());
                return s.Length > 0 ? (object)s[0] : val;
            }

            // Fallback: read-only display
            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(label) ? "" : label,
                val.ToString(), readOnlyStyle);
            return val;
        }

        private const int ARRAY_PAGE_SIZE = 100;

        public static object DrawArrayField(EditableVariable v, Array arr, out bool changed)
        {
            changed = false;
            int totalLen = arr.Length;
            v.IsExpanded = EditorGUILayout.Foldout(v.IsExpanded, $"Array [{totalLen}]", true);
            if (!v.IsExpanded) return arr;

            EditorGUI.indentLevel++;

            int displayCount = totalLen;
            if (totalLen > ARRAY_PAGE_SIZE)
            {
                EditorGUILayout.HelpBox(
                    $"Large array ({totalLen} elements). Showing first {ARRAY_PAGE_SIZE}.",
                    MessageType.Info);
                displayCount = ARRAY_PAGE_SIZE;
            }

            EditorGUI.BeginChangeCheck();
            for (int j = 0; j < displayCount; j++)
            {
                object elem    = arr.GetValue(j);
                object newElem = DrawValueField($"[{j}]", elem);
                if (!object.Equals(elem, newElem))
                    arr.SetValue(newElem, j);
            }
            if (EditorGUI.EndChangeCheck())
                changed = true;

            if (totalLen > ARRAY_PAGE_SIZE)
            {
                EditorGUILayout.LabelField(
                    $"… {totalLen - ARRAY_PAGE_SIZE} more elements hidden",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUI.indentLevel--;
            return arr;
        }

        /// <summary>Draw a single editable variable entry (header + value field).</summary>
        /// <returns>True if the value was changed by the user.</returns>
        public static bool DrawVariableEntry(EditableVariable v, string searchFilter = "")
        {
            var styles = Styles;
            bool changed = false;

            EditorGUILayout.BeginVertical(styles.VarBox);

            // ── Header row ──
            EditorGUILayout.BeginHorizontal();
            string dispName = HighlightText(v.Name, searchFilter);
            string dispType = HighlightText(v.TypeName, searchFilter);
            EditorGUILayout.LabelField(dispName, styles.VarName);
            EditorGUILayout.LabelField(dispType, styles.TypeBadge);
            EditorGUILayout.EndHorizontal();

            // ── Value row ──
            if (v.IsReadOnly)
            {
                EditorGUILayout.LabelField(v.ValueDisplay, styles.ReadOnly);
            }
            else
            {
                object currentVal = v.ValueProperty?.GetValue(v.RefObject);

                object newVal;
                if (currentVal != null && currentVal.GetType().IsArray)
                {
                    newVal = DrawArrayField(v, (Array)currentVal, out bool subChanged);
                    if (subChanged)
                    {
                        v.ValueProperty?.SetValue(v.RefObject, newVal);
                        changed = true;
                    }
                }
                else
                {
                    EditorGUI.BeginChangeCheck();
                    newVal = DrawValueField("", currentVal);
                    if (EditorGUI.EndChangeCheck())
                    {
                        v.ValueProperty?.SetValue(v.RefObject, newVal);
                        if (currentVal != null && !currentVal.GetType().IsArray)
                            v.ValueDisplay = newVal?.ToString() ?? "null";
                        changed = true;
                    }
                }
            }

            EditorGUILayout.EndVertical();
            return changed;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Scene Utilities
        // ═════════════════════════════════════════════════════════════════

        /// <summary>Find all UdonBehaviours in the current scene.</summary>
        public static List<UdonBehaviour> GetAllSceneUdonBehaviours()
        {
            var result = new List<UdonBehaviour>();
#if UNITY_2023_1_OR_NEWER
            var behaviours = UnityEngine.Object.FindObjectsByType<UdonBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var behaviours = UnityEngine.Object.FindObjectsOfType<UdonBehaviour>(true);
#endif
            if (behaviours != null)
                result.AddRange(behaviours);
            return result;
        }

        /// <summary>
        /// Find scene UdonBehaviours that reference an asset identified by its .meta GUID.
        /// Only scans behaviours already in the scene — never walks the entire project.
        /// </summary>
        public static List<UdonBehaviour> FindBehavioursByAssetGuid(string guid,
            List<UdonBehaviour> sceneBehaviours = null)
        {
            var result = new List<UdonBehaviour>();
            if (string.IsNullOrWhiteSpace(guid)) return result;

            string assetPath = AssetDatabase.GUIDToAssetPath(guid.Trim());
            if (string.IsNullOrEmpty(assetPath))
                return result;   // unknown GUID

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return result;

            if (sceneBehaviours == null)
                sceneBehaviours = GetAllSceneUdonBehaviours();

            foreach (var behaviour in sceneBehaviours)
            {
                if (behaviour == null) continue;

                try
                {
                    // Check programme source / serialized programme asset
                    var programSource = GetField<UnityEngine.Object>(behaviour, "programSource");
                    if (programSource != null && programSource == asset)
                    {
                        result.Add(behaviour);
                        continue;
                    }

                    var serializedProgramAsset = GetField<UnityEngine.Object>(behaviour, "serializedProgramAsset");
                    if (serializedProgramAsset != null && serializedProgramAsset == asset)
                    {
                        result.Add(behaviour);
                        continue;
                    }

                    // Check Unity Object references stored in serialized variables
                    var unityObjects = GetField<List<UnityEngine.Object>>(behaviour, FIELD_UNITY_OBJECTS);
                    if (unityObjects != null)
                    {
                        bool found = false;
                        foreach (var obj in unityObjects)
                        {
                            if (obj != null && obj == asset)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (found)
                        {
                            result.Add(behaviour);
                            continue;
                        }
                    }

                    // Check if the behaviour's own GameObject matches (e.g. GUID is a prefab)
                    if (asset is GameObject go && behaviour.gameObject == go)
                    {
                        result.Add(behaviour);
                    }
                }
                catch
                {
                    // Skip problematic behaviours silently
                }
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Shared UI Components
        // ═════════════════════════════════════════════════════════════════

        public static void DrawStatusBar(ToolState state, string message, int varCount,
            ref bool showLog)
        {
            var styles = Styles;

            Color barColor;
            string icon;
            switch (state)
            {
                case ToolState.Loaded:
                    barColor = new Color(0.18f, 0.45f, 0.22f);
                    icon = "●";
                    break;
                case ToolState.Modified:
                    barColor = new Color(0.55f, 0.42f, 0.10f);
                    icon = "◆";
                    break;
                case ToolState.Saved:
                    barColor = new Color(0.18f, 0.45f, 0.22f);
                    icon = "✔";
                    break;
                case ToolState.Error:
                    barColor = new Color(0.55f, 0.18f, 0.18f);
                    icon = "✖";
                    break;
                default:
                    barColor = new Color(0.3f, 0.3f, 0.3f);
                    icon = "○";
                    break;
            }

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = barColor;
            EditorGUILayout.BeginHorizontal(styles.StatusBar);
            GUI.backgroundColor = prevBg;

            GUILayout.Label($"{icon}  {message}", EditorStyles.whiteMiniLabel);
            GUILayout.FlexibleSpace();

            if (varCount > 0)
                GUILayout.Label($"{varCount} vars", EditorStyles.whiteMiniLabel);

            showLog = GUILayout.Toggle(showLog, "Log", EditorStyles.toolbarButton, GUILayout.Width(38));
            EditorGUILayout.EndHorizontal();
        }

        public static void DrawDebugLog(List<LogEntry> entries, ref Vector2 scroll)
        {
            var styles = Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("LOG", styles.SectionHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(40)))
                entries.Clear();
            EditorGUILayout.EndHorizontal();

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(100));

            foreach (var e in entries)
            {
                GUILayout.Label($"[{e.Time}] {e.Message}", styles.GetLogStyle(e.Level));
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // ─── Log helper ──────────────────────────────────────────────────

        public static void AddLog(List<LogEntry> entries, string msg,
            LogLevel level = LogLevel.Info, int maxEntries = 200)
        {
            entries.Add(new LogEntry
            {
                Message = msg,
                Level   = level,
                Time    = DateTime.Now.ToString("HH:mm:ss"),
            });
            while (entries.Count > maxEntries)
                entries.RemoveAt(0);
        }

        // ─── Search helper ───────────────────────────────────────────────

        public static bool MatchesFilter(string text, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (string.IsNullOrEmpty(text))   return false;
            return text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
#endif
