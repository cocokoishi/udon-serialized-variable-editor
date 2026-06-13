#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Serialization.OdinSerializer;

namespace UdonVarViewer
{
    /// <summary>
    /// Main variable editor window — load, view, edit, and save serialized
    /// public variables on a single UdonBehaviour.
    /// </summary>
    public class UdonVariableEditorWindow : EditorWindow
    {
        // ─────────────────────────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────────────────────────

        private UdonBehaviour         targetBehaviour;
        private BehaviourVariableSet  currentSet;

        // UI
        private string  base64Input      = "";
        private string  base64Output     = "";
        private string  searchFilter     = "";
        private bool    showBase64Panel  = false;
        private bool    showDebugLog     = false;
        private Vector2 variableScroll;
        private Vector2 debugScroll;

        // Log
        private List<LogEntry> logEntries = new List<LogEntry>();

        // Status
        private ToolState toolState     = ToolState.Idle;
        private string    statusMessage = "No behaviour loaded.";

        // ─────────────────────────────────────────────────────────────────
        //  Menu & Public API
        // ─────────────────────────────────────────────────────────────────

        [MenuItem("Udon Var Viewer/Variable Editor")]
        public static UdonVariableEditorWindow ShowWindow()
        {
            var w = GetWindow<UdonVariableEditorWindow>("Udon Var Editor");
            w.minSize = new Vector2(360, 500);
            return w;
        }

        /// <summary>
        /// Called externally (e.g. from Scene Browser) to load a behaviour.
        /// </summary>
        public void SetTargetAndLoad(UdonBehaviour behaviour)
        {
            if (behaviour == null) return;
            targetBehaviour = behaviour;
            LoadFromBehaviour();
            Focus();
        }

        // ─────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────

        private void OnDisable()
        {
            currentSet   = null;
            base64Output = "";
        }

        // ─────────────────────────────────────────────────────────────────
        //  OnGUI
        // ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            // Ensure styles are ready
            var styles = UdonVarViewerUtility.Styles;

            DrawStatusBar();
            DrawTargetSection();

            if (targetBehaviour != null)
                DrawActionButtons();

            EditorGUILayout.Space(2);

            DrawBase64Panel();
            DrawSearchBar();
            DrawVariableList();
            DrawExportSection();

            if (showDebugLog)
                UdonVarViewerUtility.DrawDebugLog(logEntries, ref debugScroll);
        }

        // ─── Status Bar ──────────────────────────────────────────────────

        private void DrawStatusBar()
        {
            int varCount = currentSet?.Variables?.Count ?? 0;
            UdonVarViewerUtility.DrawStatusBar(toolState, statusMessage, varCount, ref showDebugLog);
        }

        // ─── Target Section ──────────────────────────────────────────────

        private void DrawTargetSection()
        {
            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.Space(6);

            GUILayout.Label("TARGET", styles.SectionHeader);

            EditorGUI.BeginChangeCheck();
            var newBehaviour = (UdonBehaviour)EditorGUILayout.ObjectField(
                targetBehaviour, typeof(UdonBehaviour), true);
            if (EditorGUI.EndChangeCheck())
            {
                targetBehaviour = newBehaviour;
                ClearAll();
                if (targetBehaviour != null)
                    SetStatus(ToolState.Idle, $"Selected: {targetBehaviour.name}");
                else
                    SetStatus(ToolState.Idle, "No behaviour loaded.");
            }

            if (targetBehaviour == null)
            {
                EditorGUILayout.HelpBox(
                    "Drag an UdonBehaviour from the Hierarchy, or use the Scene Browser (Udon Var Viewer → Scene Browser) for quick import.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        // ─── Action Buttons ──────────────────────────────────────────────

        private void DrawActionButtons()
        {
            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();

            var prevColor = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.3f, 0.6f, 1.0f, 1f);
            if (GUILayout.Button("⬇  Load from Behaviour", GUILayout.Height(28)))
                LoadFromBehaviour();

            GUI.backgroundColor = toolState == ToolState.Modified
                ? new Color(1f, 0.65f, 0.2f, 1f)
                : new Color(0.35f, 0.75f, 0.35f, 1f);

            EditorGUI.BeginDisabledGroup(currentSet == null || currentSet.Table == null);
            if (GUILayout.Button("⬆  Save to Behaviour", GUILayout.Height(28)))
                SaveToBehaviour();
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = prevColor;
            EditorGUILayout.EndHorizontal();

            // Base64 import toggle
            showBase64Panel = EditorGUILayout.Foldout(showBase64Panel,
                "Manual Base64 Import", true, EditorStyles.foldoutHeader);

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        // ─── Base64 Panel ────────────────────────────────────────────────

        private void DrawBase64Panel()
        {
            if (!showBase64Panel) return;

            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            GUILayout.Label("PASTE BASE64 DATA", styles.SectionHeader);
            base64Input = EditorGUILayout.TextArea(base64Input,
                GUILayout.MinHeight(50), GUILayout.MaxHeight(80));

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(base64Input));
            if (GUILayout.Button("Parse", GUILayout.Height(22)))
                Parse();
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Clear", GUILayout.Width(50), GUILayout.Height(22)))
            {
                base64Input = "";
                ClearAll();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        // ─── Search Bar ──────────────────────────────────────────────────

        private void DrawSearchBar()
        {
            if (currentSet == null || currentSet.Variables.Count == 0) return;

            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("VARIABLES", styles.SectionHeader, GUILayout.Width(72));
            GUILayout.FlexibleSpace();

            searchFilter = EditorGUILayout.TextField(
                searchFilter,
                EditorStyles.toolbarSearchField,
                GUILayout.Width(160));

            if (!string.IsNullOrEmpty(searchFilter) &&
                GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(22)))
                searchFilter = "";

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ─── Variable List ───────────────────────────────────────────────

        private void DrawVariableList()
        {
            if (currentSet == null || currentSet.Variables.Count == 0) return;

            float topUsed = 200f;
            float listH   = Mathf.Max(120f, position.height - topUsed - (showDebugLog ? 130f : 10f));

            variableScroll = EditorGUILayout.BeginScrollView(
                variableScroll, GUILayout.Height(listH));

            bool anyVisible = false;
            foreach (var v in currentSet.Variables)
            {
                if (!UdonVarViewerUtility.MatchesFilter(v.Name, searchFilter) &&
                    !UdonVarViewerUtility.MatchesFilter(v.TypeName, searchFilter))
                    continue;

                anyVisible = true;
                if (UdonVarViewerUtility.DrawVariableEntry(v))
                {
                    if (toolState != ToolState.Modified)
                        SetStatus(ToolState.Modified, "Unsaved changes — remember to Save to Behaviour.");
                    if (currentSet != null) currentSet.IsDirty = true;
                }
            }

            if (!anyVisible)
            {
                EditorGUILayout.LabelField(
                    $"No variables match \"{searchFilter}\".",
                    EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        // ─── Export Section ──────────────────────────────────────────────

        private void DrawExportSection()
        {
            if (currentSet == null || currentSet.Table == null) return;

            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.Space(4);

            GUILayout.Label("EXPORT", styles.SectionHeader);

            EditorGUILayout.BeginHorizontal();

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.55f, 0.3f, 0.8f, 1f);
            if (GUILayout.Button("Export Base64", GUILayout.Height(24)))
                Export();
            GUI.backgroundColor = prevColor;

            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(base64Output));
            if (GUILayout.Button("📋 Copy", GUILayout.Height(24), GUILayout.Width(60)))
                GUIUtility.systemCopyBuffer = base64Output;
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(base64Output))
            {
                EditorGUILayout.LabelField(
                    $"Output: {base64Output.Length} chars",
                    EditorStyles.centeredGreyMiniLabel);
                base64Output = EditorGUILayout.TextArea(base64Output,
                    GUILayout.MinHeight(36), GUILayout.MaxHeight(60));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────────────────────────
        //  Core Logic  (delegates to UdonVarViewerUtility)
        // ─────────────────────────────────────────────────────────────────

        private void LoadFromBehaviour()
        {
            if (targetBehaviour == null) return;
            ClearAll();

            currentSet = UdonVarViewerUtility.LoadFromBehaviour(targetBehaviour, Log);

            if (currentSet.IsLoaded)
            {
                base64Input = currentSet.OriginalBase64;
                SetStatus(ToolState.Loaded,
                    $"Loaded {currentSet.Variables.Count} variables from '{targetBehaviour.name}'.");
            }
            else
            {
                SetStatus(ToolState.Error, currentSet.ErrorMessage ?? "Load failed.");
            }
        }

        private void Parse()
        {
            ClearAll();
            if (string.IsNullOrWhiteSpace(base64Input))
            {
                SetStatus(ToolState.Error, "Base64 input is empty.");
                return;
            }

            Log("Parsing Base64 (manual input)…", LogLevel.Info);

            List<UnityEngine.Object> unityObjects = null;
            if (targetBehaviour != null)
            {
                unityObjects = UdonVarViewerUtility.GetField<List<UnityEngine.Object>>(
                    targetBehaviour, UdonVarViewerUtility.FIELD_UNITY_OBJECTS);
                Log($"Injecting {unityObjects?.Count ?? 0} Unity Object refs from target.", LogLevel.Info);
            }
            else
            {
                Log("No target selected — Unity Object references will resolve as null.", LogLevel.Warning);
            }

            var table = UdonVarViewerUtility.ParseBase64(base64Input, unityObjects, DataFormat.Binary, Log);

            if (table == null)
            {
                SetStatus(ToolState.Error, "Could not deserialize data.");
                return;
            }

            currentSet = new BehaviourVariableSet
            {
                Behaviour     = targetBehaviour,
                Table         = table,
                OriginalBase64 = base64Input,
                UnityObjects  = unityObjects,
            };
            UdonVarViewerUtility.ParseObjectToVariables(table, currentSet.Variables, Log);
            currentSet.IsLoaded = true;

            SetStatus(ToolState.Loaded, $"Parsed {currentSet.Variables.Count} variables from Base64.");
        }

        private void SaveToBehaviour()
        {
            if (targetBehaviour == null || currentSet == null || currentSet.Table == null) return;

            currentSet.Behaviour = targetBehaviour;

            if (UdonVarViewerUtility.SaveToBehaviour(currentSet, Log))
            {
                base64Output = currentSet.OriginalBase64;
                SetStatus(ToolState.Saved, $"Saved to '{targetBehaviour.name}' successfully.");
            }
            else
            {
                SetStatus(ToolState.Error, "Save failed — check Log for details.");
            }
        }

        private void Export()
        {
            if (currentSet == null || currentSet.Table == null) return;

            string result = UdonVarViewerUtility.ExportToBase64(currentSet.Table, Log);
            if (result != null)
            {
                base64Output = result;
                GUIUtility.systemCopyBuffer = result;
                SetStatus(ToolState.Saved, "Base64 exported and copied to clipboard.");
            }
            else
            {
                SetStatus(ToolState.Error, "Export failed — check Log for details.");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────

        private void ClearAll()
        {
            currentSet   = null;
            base64Output = "";
        }

        private void SetStatus(ToolState state, string msg)
        {
            toolState     = state;
            statusMessage = msg;
            Repaint();
        }

        private void Log(string msg, LogLevel level = LogLevel.Info)
        {
            UdonVarViewerUtility.AddLog(logEntries, msg, level);
        }
    }
}
#endif