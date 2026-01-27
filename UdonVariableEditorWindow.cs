using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;
using VRC.Udon.Serialization.OdinSerializer; // 确保项目已导入 VRChat SDK3

// 定义别名以简化调用
using OdinUtility = VRC.Udon.Serialization.OdinSerializer.SerializationUtility;

namespace UdonVarViewer
{
    public class UdonVariableEditorWindow : EditorWindow
    {
        private string base64Input = "";
        private string base64Output = "";
        
        // 使用 object 以兼容不同版本的 SDK 内部实现，但通常它是 IUdonVariableTable
        private object currentTable;
        
        private List<EditableVariable> variables = new List<EditableVariable>();
        private string debugLog = "Ready.";
        private Vector2 debugScroll;
        private Vector2 scrollPos;
        
        // 目标 UdonBehaviour
        private UdonBehaviour targetBehaviour;

        [MenuItem("Tools/Udon Var Viewer")]
        public static void ShowWindow()
        {
            GetWindow<UdonVariableEditorWindow>("Udon Var Editor");
        }

        private void OnGUI()
        {
            GUILayout.Label("YASHIRIRIKA", EditorStyles.boldLabel);
            
            GUILayout.Space(5);
            
            // 绘制对象选择框
            EditorGUI.BeginChangeCheck();
            targetBehaviour = (UdonBehaviour)EditorGUILayout.ObjectField("Target UdonBehaviour", targetBehaviour, typeof(UdonBehaviour), true);
            if (EditorGUI.EndChangeCheck())
            {
                // 如果切换了对象，自动清理之前的缓存，防止误操作
                variables.Clear();
                base64Input = "";
                currentTable = null;
                debugLog += "\nTarget changed. Cleared context.";
            }

            if (targetBehaviour != null)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Load from Behaviour"))
                {
                    LoadFromBehaviour();
                }
                if (GUILayout.Button("Save to Behaviour"))
                {
                    SaveToBehaviour();
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("Please assign a Target UdonBehaviour to handle Object References correctly.", MessageType.Warning);
            }
            
            GUILayout.Space(5);

            GUILayout.Label("Input Base64 Data:");
            base64Input = EditorGUILayout.TextArea(base64Input, GUILayout.Height(60));

            // Parse 按钮逻辑修复：即使手动输入 Base64，也会尝试去 Target 上找引用
            if (GUILayout.Button("Parse Variables"))
            {
                Parse();
            }

            if (variables != null && variables.Count > 0)
            {
                GUILayout.Label($"Loaded Variables: {variables.Count}");
                GUILayout.Space(10);
                GUILayout.Label("Variable List:", EditorStyles.boldLabel);

                scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(300));
                
                for (int i = 0; i < variables.Count; i++)
                {
                    var v = variables[i];
                    
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(v.Name, EditorStyles.boldLabel, GUILayout.Width(150));
                    EditorGUILayout.EndHorizontal();
                    
                    // Value Editing Logic
                    if (v.IsReadOnly)
                    {
                        EditorGUILayout.LabelField(v.ValueDisplay, EditorStyles.wordWrappedLabel);
                    }
                    else
                    {
                        EditorGUI.BeginChangeCheck();
                        
                        // 通过反射获取当前值，确保是最新的
                        object currentVal = null;
                        if (v.ValueProperty != null) currentVal = v.ValueProperty.GetValue(v.RefObject);
                        
                        object newVal = currentVal;

                        // 数组处理逻辑
                        if (currentVal != null && currentVal.GetType().IsArray)
                        {
                            Array arr = (Array)currentVal;
                            v.IsExpanded = EditorGUILayout.Foldout(v.IsExpanded, $"Array [{arr.Length}] ({v.TypeName})");
                            
                            if (v.IsExpanded)
                            {
                                EditorGUI.indentLevel++;
                                for (int j = 0; j < arr.Length; j++)
                                {
                                    object elem = arr.GetValue(j);
                                    // 递归绘制单个元素
                                    object newElem = DrawValueField($"Element {j}", elem);
                                    
                                    // 如果元素发生变化
                                    if (!object.Equals(elem, newElem))
                                    {
                                        arr.SetValue(newElem, j);
                                        // 更新摘要显示
                                        v.ValueDisplay = $"Array[{arr.Length}]"; 
                                    }
                                }
                                EditorGUI.indentLevel--;
                            }
                            newVal = currentVal; // 引用类型直接修改，不需要重新赋值
                        }
                        else
                        {
                            // 普通类型处理
                            newVal = DrawValueField("", currentVal);
                        }

                        // 如果值发生改变，写回原对象
                        if (EditorGUI.EndChangeCheck())
                        {
                            if (v.ValueProperty != null)
                            {
                                v.ValueProperty.SetValue(v.RefObject, newVal);
                            }
                            
                            if (currentVal != null && !currentVal.GetType().IsArray)
                            {
                                v.ValueDisplay = newVal != null ? newVal.ToString() : "null";
                            }
                        }
                    }
                    
                    EditorGUILayout.LabelField(v.TypeName, EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                }
                
                GUILayout.EndScrollView();
            }

            GUILayout.Space(10);
            if (GUILayout.Button("💾 Export Base64"))
            {
                Export();
            }

            GUILayout.Label("Export Result:");
            EditorGUILayout.TextArea(base64Output, GUILayout.Height(60));
            if (!string.IsNullOrEmpty(base64Output))
            {
                 if (GUILayout.Button("Copy to Clipboard"))
                     GUIUtility.systemCopyBuffer = base64Output;
            }

            GUILayout.Space(10);
            GUILayout.Label("Debug Log:", EditorStyles.boldLabel);
            debugScroll = GUILayout.BeginScrollView(debugScroll, GUILayout.Height(100));
            EditorGUILayout.TextArea(debugLog, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
        }

        // ==========================================
        // 核心逻辑修复区：Load / Parse / Save
        // ==========================================

        private void LoadFromBehaviour()
        {
            if (targetBehaviour == null) return;

            try
            {
                debugLog = $"Loading from {targetBehaviour.name}...";
                
                // 1. 获取 Base64 字符串
                FieldInfo bytesField = typeof(UdonBehaviour).GetField("serializedPublicVariablesBytesString", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (bytesField == null)
                {
                    debugLog += "\n[Error] Could not find field 'serializedPublicVariablesBytesString'.";
                    return;
                }
                string base64 = (string)bytesField.GetValue(targetBehaviour);
                if (string.IsNullOrEmpty(base64))
                {
                    debugLog += "\n[Warning] Base64 string is empty.";
                    base64 = "";
                }
                base64Input = base64; 

                // 2. 获取 Unity Object List (这是修复引用丢失的关键)
                List<UnityEngine.Object> unityObjects = GetUnityObjectsFromBehaviour(targetBehaviour);
                if (unityObjects != null)
                    debugLog += $"\nFound {unityObjects.Count} Unity Objects references.";
                else
                    debugLog += "\nNo Unity Objects found (or list is null).";

                // 3. 获取数据格式
                DataFormat format = GetDataFormat(targetBehaviour);
                debugLog += $"\nFormat: {format}";

                // 4. 反序列化
                variables.Clear();
                byte[] data = Convert.FromBase64String(base64Input.Trim());
                var context = new DeserializationContext();
                
                // 使用 IUdonVariableTable 接口
                currentTable = OdinUtility.DeserializeValue<IUdonVariableTable>(data, format, unityObjects, context);

                if (currentTable == null)
                {
                    debugLog += "\n[Error] Deserialized object is NULL.";
                    return;
                }

                ParseObjectToVariables(currentTable);
            }
            catch (Exception ex)
            {
                debugLog += $"\n[Exception] {ex.Message}\n{ex.StackTrace}";
                Debug.LogError($"[UdonVarViewer] Load Error: {ex}");
            }
        }

        /// <summary>
        /// 修复后的 Parse 方法：主动注入 Target 的 Object List，防止引用变为 null
        /// </summary>
        private void Parse()
        {
            try
            {
                variables.Clear();
                debugLog = "Parsing (Manual Input)...";
                
                if (string.IsNullOrEmpty(base64Input))
                {
                    debugLog += "\n[Error] Base64 input is empty.";
                    return;
                }

                byte[] data = Convert.FromBase64String(base64Input.Trim());
                var context = new DeserializationContext();
                
                // --- 修复开始 ---
                List<UnityEngine.Object> unityObjects = null;
                
                if (targetBehaviour != null)
                {
                    unityObjects = GetUnityObjectsFromBehaviour(targetBehaviour);
                    debugLog += $"\n[Ref Fix] Injecting {unityObjects?.Count ?? 0} Unity Objects from target behaviour to resolve references.";
                }
                else
                {
                    debugLog += "\n[Warning] No Target Behaviour selected! Any GameObject/Component references in this Base64 will become NULL.";
                }

                // 关键：传入 unityObjects
                currentTable = OdinUtility.DeserializeValue<IUdonVariableTable>(data, DataFormat.Binary, unityObjects, context);
                // --- 修复结束 ---

                if (currentTable == null)
                {
                    // 尝试作为普通 Object 反序列化（兼容性后备）
                    debugLog += "\n[Info] IUdonVariableTable failed/null, trying strict object...";
                    currentTable = OdinUtility.DeserializeValue<object>(data, DataFormat.Binary, unityObjects, context);
                }

                if (currentTable == null)
                {
                    debugLog += "\n[Error] Deserialized object is NULL.";
                    return;
                }
                
                ParseObjectToVariables(currentTable);
            }
            catch (Exception ex)
            {
                debugLog += $"\n[Exception] {ex.Message}\n{ex.StackTrace}";
                Debug.LogError($"[UdonVarViewer] Parse Error: {ex}");
            }
        }

        private void SaveToBehaviour()
        {
            if (targetBehaviour == null || currentTable == null) return;

            try
            {
                 debugLog = $"Saving to {targetBehaviour.name}...";
                 DataFormat format = GetDataFormat(targetBehaviour);

                 // 1. 序列化并提取 Unity Objects
                 var context = new SerializationContext();
                 List<UnityEngine.Object> unityObjects;
                 
                 // 重新序列化 currentTable
                 byte[] bytes = OdinUtility.SerializeValue(currentTable, format, out unityObjects, context);
                 
                 base64Output = Convert.ToBase64String(bytes);
                 debugLog += $"\nSerialized {bytes.Length} bytes, {unityObjects?.Count ?? 0} Unity Objects.";

                 // 2. 通过反射写回 Behaviour
                 Undo.RecordObject(targetBehaviour, "Modify Udon Variables");

                 FieldInfo bytesField = typeof(UdonBehaviour).GetField("serializedPublicVariablesBytesString", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                 if (bytesField != null) bytesField.SetValue(targetBehaviour, base64Output);

                 FieldInfo objsField = typeof(UdonBehaviour).GetField("publicVariablesUnityEngineObjects", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                 if (objsField != null) objsField.SetValue(targetBehaviour, unityObjects);

                 // 3. 标记脏数据，通知 Unity 保存
                 EditorUtility.SetDirty(targetBehaviour);
                 debugLog += "\nSaved successfully.";
            }
            catch (Exception ex)
            {
                debugLog += $"\n[Exception] {ex.Message}\n{ex.StackTrace}";
                Debug.LogError($"[UdonVarViewer] Save Error: {ex}");
            }
        }

        private void Export()
        {
            try
            {
                if (currentTable == null)
                {
                    Debug.LogError("[UdonVarViewer] Cannot export: Table is null.");
                    return;
                }
                
                var context = new SerializationContext(); 
                List<UnityEngine.Object> discardedList;
                byte[] bytes = OdinUtility.SerializeValue(currentTable, DataFormat.Binary, out discardedList, context);
                base64Output = Convert.ToBase64String(bytes);
                
                debugLog += $"\nExported Base64. (Note: {discardedList?.Count ?? 0} Unity Objects were stripped/separated from this string)";
                GUIUtility.systemCopyBuffer = base64Output;
            }
            catch (Exception ex)
            {
                debugLog += $"\nExport Error: {ex.Message}";
            }
        }

        // ==========================================
        // 辅助方法 (Helpers)
        // ==========================================

        private List<UnityEngine.Object> GetUnityObjectsFromBehaviour(UdonBehaviour behaviour)
        {
            if (behaviour == null) return null;
            FieldInfo objsField = typeof(UdonBehaviour).GetField("publicVariablesUnityEngineObjects", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (objsField != null)
            {
                return (List<UnityEngine.Object>)objsField.GetValue(behaviour);
            }
            return null;
        }

        private DataFormat GetDataFormat(UdonBehaviour behaviour)
        {
            FieldInfo formatField = typeof(UdonBehaviour).GetField("publicVariablesSerializationDataFormat", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (formatField != null)
            {
                return (DataFormat)formatField.GetValue(behaviour);
            }
            return DataFormat.Binary;
        }

        private void ParseObjectToVariables(object tableObj)
        {
            Type dbgType = tableObj.GetType();
            debugLog += $"\nObject Type: {dbgType.FullName}";
            
            // 扫描字段以查找 Dictionary 或 List 存储结构
            var allFields = dbgType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            IEnumerable list = null;
            
            foreach (var field in allFields)
            {
                // 常见的 Udon 存储字段名
                if (field.Name.Equals("variables", StringComparison.OrdinalIgnoreCase) || 
                    (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>)))
                {
                    var val = field.GetValue(tableObj);
                    if (val is IEnumerable enumerable)
                    {
                        list = enumerable;
                        if (field.Name.Equals("variables", StringComparison.OrdinalIgnoreCase)) break;
                    }
                }
                else if (field.Name.Contains("publicVariables") || 
                         (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                {
                     var val = field.GetValue(tableObj);
                     if (val is IDictionary dict)
                     {
                         list = dict.Values;
                         break;
                     }
                }
            }
            
            if (list == null)
            {
                debugLog += "\n[Error] Could not find any valid variable container (List/Dictionary)!";
                return;
            }

            int count = 0;
            foreach (var obj in list)
            {
                count++;
                ProcessLoadedVariable(obj);
            }
            
            debugLog += $"\nParsed {count} items. Displaying: {variables.Count}";
        }

        private void ProcessLoadedVariable(object obj)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            
            // 简单检查是否为 IUdonVariable (或其实现类)
            if (t.Name.Contains("UdonVariable"))
            {
                string symbolName = null;
                object value = null;
                bool success = false;

                try
                {
                    // 尝试通过 Property 获取
                    PropertyInfo propSymbol = t.GetProperty("SymbolName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    PropertyInfo propValue = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    
                    if (propSymbol != null) symbolName = (string)propSymbol.GetValue(obj, null);
                    if (propValue != null) value = propValue.GetValue(obj, null);
                    
                    // 假如 Property 失败，尝试 Field (混淆保护可能导致需要这样做)
                    if (string.IsNullOrEmpty(symbolName))
                    {
                         var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                         foreach(var f in fields)
                         {
                             if (f.Name.Equals("SymbolName", StringComparison.OrdinalIgnoreCase)) symbolName = (string)f.GetValue(obj);
                             // Value 字段可能名字不同，这里暂时主要依赖 Property
                         }
                    }
                    
                    success = !string.IsNullOrEmpty(symbolName);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UdonVarViewer] Error reading variable properties: {e.Message}");
                }
                
                if (!success) return;

                // 确定泛型类型 T
                Type variableType = typeof(object);
                if (t.IsGenericType) variableType = t.GetGenericArguments()[0];

                string typeName = variableType.Name;
                string valDisplay = (value != null) ? value.ToString() : "null";
                bool readOnly = true; // 默认为只读，除非是我们支持的简单类型

                // 判定是否可编辑
                if (value == null ||
                    value is string || value is long || value is int || value is double || value is float || value is bool || value is char || 
                    value is Vector3 || value is Color || value is Quaternion)
                {
                    readOnly = false;
                }
                
                if (value != null && value.GetType().IsArray)
                {
                   readOnly = false; // 允许数组展开编辑
                   valDisplay = $"Array [{(value as Array).Length}]";
                }
                
                // Unity Object 处理：暂时保持只读显示，避免复杂的 ObjectField 逻辑
                if (typeof(UnityEngine.Object).IsAssignableFrom(variableType))
                {
                    readOnly = true; 
                    if (value == null || value.ToString() == "null") valDisplay = $"None ({variableType.Name})";
                    else valDisplay = $"{value.ToString()} ({variableType.Name})";
                }

                variables.Add(new EditableVariable
                {
                    Name = symbolName,
                    TypeName = typeName,
                    ValueDisplay = valDisplay,
                    RefObject = obj,
                    ValueProperty = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), 
                    IsReadOnly = readOnly,
                    IsExpanded = false
                });
            }
        }

        // 通用绘图逻辑：根据值的类型决定使用哪个 EditorGUILayout 控件
        private object DrawValueField(string label, object val)
        {
            if (val == null) 
            {
                EditorGUILayout.LabelField(label, "null");
                return null;
            }

            Type type = val.GetType();

            if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, (Vector3)val);
            if (type == typeof(Color)) return EditorGUILayout.ColorField(label, (Color)val);
            if (type == typeof(Quaternion))
            {
                Vector3 euler = ((Quaternion)val).eulerAngles;
                Vector3 newEuler = EditorGUILayout.Vector3Field(string.IsNullOrEmpty(label) ? "Rotation (Euler)" : label, euler);
                if (newEuler != euler) return Quaternion.Euler(newEuler);
                return val;
            }
            if (type == typeof(bool)) return EditorGUILayout.Toggle(label, (bool)val);
            if (type == typeof(int)) return EditorGUILayout.IntField(label, (int)val);
            if (type == typeof(long)) return EditorGUILayout.LongField(label, (long)val);
            if (type == typeof(float)) return EditorGUILayout.FloatField(label, (float)val);
            if (type == typeof(double)) return EditorGUILayout.DoubleField(label, (double)val);
            if (type == typeof(string)) return EditorGUILayout.TextField(label, (string)val);
            if (type == typeof(char))
            {
                string charStr = EditorGUILayout.TextField(label, val.ToString());
                if (charStr.Length > 0) return charStr[0];
                return val;
            }
            
            // 兜底显示
            EditorGUILayout.LabelField(label, val.ToString());
            return val;
        }

        private class EditableVariable
        {
            public string Name;
            public string TypeName;
            public string ValueDisplay;
            public object RefObject;
            public PropertyInfo ValueProperty;
            public bool IsReadOnly;
            public bool IsExpanded;
        }
    }
}
