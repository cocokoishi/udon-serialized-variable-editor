#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace UdonVarViewer
{
    /// <summary>
    /// Scene Browser — scans the active scene for all UdonBehaviours, displays them
    /// in a searchable list, supports inline variable editing, GUID-based lookup,
    /// individual save / export, and quick import into the Variable Editor.
    /// </summary>
    public class UdonSceneBrowserWindow : EditorWindow
    {
        // ─────────────────────────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────────────────────────

        private List<BehaviourVariableSet> behaviourSets = new List<BehaviourVariableSet>();
        private List<BehaviourVariableSet> filteredSets  = new List<BehaviourVariableSet>();

        // UI
        private string  searchFilter  = "";
        private string  guidInput     = "";
        private string  guidResult    = "";
        private bool    showDebugLog  = false;
        private Vector2 mainScroll;
        private Vector2 debugScroll;

        // Status
        private ToolState toolState     = ToolState.Idle;
        private string    statusMessage = "Press 'Scan Scene' to begin.";

        // Log
        private List<LogEntry> logEntries = new List<LogEntry>();

        // Scan tracking
        private bool    hasScanned   = false;
        private int     dirtyCount   = 0;

        // ─────────────────────────────────────────────────────────────────
        //  Menu
        // ─────────────────────────────────────────────────────────────────

        [MenuItem("Udon Var Viewer/Scene Browser")]
        public static UdonSceneBrowserWindow ShowWindow()
        {
            var w = GetWindow<UdonSceneBrowserWindow>("Scene Browser");
            w.minSize = new Vector2(420, 500);
            return w;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            behaviourSets.Clear();
            filteredSets.Clear();
        }

        private void OnHierarchyChanged()
        {
            // Mark as stale so user knows to rescan
            if (hasScanned)
                SetStatus(ToolState.Modified, "Scene hierarchy changed — consider rescanning.");
        }

        // ─────────────────────────────────────────────────────────────────
        //  OnGUI
        // ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            var styles = UdonVarViewerUtility.Styles;

            DrawStatusBar();
            DrawToolbar();
            DrawGuidLookup();
            DrawBatchActions();

            EditorGUILayout.Space(2);

            DrawBehaviourList();

            if (showDebugLog)
                UdonVarViewerUtility.DrawDebugLog(logEntries, ref debugScroll);
        }

        // ─── Status Bar ──────────────────────────────────────────────────

        private void DrawStatusBar()
        {
            UdonVarViewerUtility.DrawStatusBar(toolState, statusMessage,
                behaviourSets.Count, ref showDebugLog);
        }

        // ─── Toolbar ─────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.Space(6);

            EditorGUILayout.BeginHorizontal();

            var prevColor = GUI.backgroundColor;

            // Scan button
            GUI.backgroundColor = new Color(0.3f, 0.6f, 1.0f, 1f);
            if (GUILayout.Button("🔍  Scan Scene", GUILayout.Height(28)))
                ScanScene();

            // Refresh button
            GUI.backgroundColor = new Color(0.4f, 0.7f, 0.5f, 1f);
            EditorGUI.BeginDisabledGroup(!hasScanned);
            if (GUILayout.Button("↻  Refresh", GUILayout.Height(28), GUILayout.Width(90)))
                ScanScene();
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = prevColor;
            EditorGUILayout.EndHorizontal();

            // Search bar
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("🔎", GUILayout.Width(20));

            EditorGUI.BeginChangeCheck();
            searchFilter = EditorGUILayout.TextField(searchFilter, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
                RebuildFilteredList();

            if (!string.IsNullOrEmpty(searchFilter) &&
                GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(22)))
            {
                searchFilter = "";
                RebuildFilteredList();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        // ─── GUID Lookup ─────────────────────────────────────────────────

        private void DrawGuidLookup()
        {
            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("META GUID", styles.SectionHeader, GUILayout.Width(80));
            guidInput = EditorGUILayout.TextField(guidInput);

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(guidInput) || !hasScanned);
            if (GUILayout.Button("Locate", GUILayout.Width(55), GUILayout.Height(18)))
                LocateByGuid();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(guidResult))
            {
                EditorGUILayout.HelpBox(guidResult, MessageType.Info);
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.EndVertical();
        }

        // ─── Batch Actions ───────────────────────────────────────────────

        private void DrawBatchActions()
        {
            if (!hasScanned || behaviourSets.Count == 0) return;

            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();

            // Count dirty
            dirtyCount = 0;
            foreach (var s in behaviourSets)
                if (s.IsDirty) dirtyCount++;

            // Save All Modified
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = dirtyCount > 0
                ? new Color(1f, 0.65f, 0.2f, 1f)
                : new Color(0.35f, 0.75f, 0.35f, 1f);

            EditorGUI.BeginDisabledGroup(dirtyCount == 0);
            if (GUILayout.Button($"💾  Save All Modified ({dirtyCount})", GUILayout.Height(24)))
                SaveAllModified();
            EditorGUI.EndDisabledGroup();

            // Collapse All
            GUI.backgroundColor = prevColor;
            if (GUILayout.Button("Collapse All", GUILayout.Width(85), GUILayout.Height(24)))
            {
                foreach (var s in behaviourSets) s.IsExpanded = false;
            }

            // Expand All
            if (GUILayout.Button("Expand All", GUILayout.Width(75), GUILayout.Height(24)))
            {
                foreach (var s in behaviourSets) s.IsExpanded = true;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
            EditorGUILayout.EndVertical();
        }

        // ─── Behaviour List ──────────────────────────────────────────────

        private void DrawBehaviourList()
        {
            if (!hasScanned) return;

            var styles = UdonVarViewerUtility.Styles;

            float topUsed = 220f;
            float listH   = Mathf.Max(120f, position.height - topUsed - (showDebugLog ? 130f : 10f));

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll, GUILayout.Height(listH));

            var listToShow = filteredSets.Count > 0 || !string.IsNullOrEmpty(searchFilter)
                ? filteredSets : behaviourSets;

            if (listToShow.Count == 0)
            {
                if (!string.IsNullOrEmpty(searchFilter))
                    EditorGUILayout.LabelField($"No behaviours match \"{searchFilter}\".",
                        EditorStyles.centeredGreyMiniLabel);
                else
                    EditorGUILayout.LabelField("No UdonBehaviours found in scene.",
                        EditorStyles.centeredGreyMiniLabel);
            }

            foreach (var set in listToShow)
            {
                DrawBehaviourCard(set);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBehaviourCard(BehaviourVariableSet set)
        {
            var styles = UdonVarViewerUtility.Styles;

            // Outer card box
            EditorGUILayout.BeginVertical(styles.VarBox);

            // ── Header row ──
            EditorGUILayout.BeginHorizontal();

            // Foldout
            set.IsExpanded = EditorGUILayout.Foldout(set.IsExpanded, "", true);

            // Name
            string displayName = set.IsValid ? set.DisplayName : "(destroyed)";
            GUILayout.Label(displayName, styles.VarName, GUILayout.MinWidth(60));

            // Dirty badge
            if (set.IsDirty)
                GUILayout.Label("● Modified", styles.DirtyBadge, GUILayout.Width(68));

            // Var count
            string varInfo = set.IsLoaded ? $"{set.Variables.Count} vars" : (set.ErrorMessage ?? "not loaded");
            GUILayout.Label(varInfo, styles.TypeBadge, GUILayout.Width(70));

            EditorGUILayout.EndHorizontal();

            // ── Path row ──
            if (set.IsValid)
            {
                EditorGUILayout.LabelField(set.GameObjectPath, styles.PathLabel);
            }

            // ── Button row ──
            EditorGUILayout.BeginHorizontal();

            // Open in Variable Editor
            if (GUILayout.Button("Open in Editor", EditorStyles.miniButton, GUILayout.Height(20)))
            {
                var editor = UdonVariableEditorWindow.ShowWindow();
                if (set.IsValid)
                    editor.SetTargetAndLoad(set.Behaviour);
            }

            // Load (if not loaded)
            if (!set.IsLoaded)
            {
                if (GUILayout.Button("Load", EditorStyles.miniButton,
                    GUILayout.Width(45), GUILayout.Height(20)))
                {
                    LoadSingleBehaviour(set);
                }
            }

            // Save This
            EditorGUI.BeginDisabledGroup(!set.IsDirty || !set.IsLoaded);
            if (GUILayout.Button("Save", EditorStyles.miniButton,
                GUILayout.Width(40), GUILayout.Height(20)))
            {
                SaveSingleBehaviour(set);
            }
            EditorGUI.EndDisabledGroup();

            // Export
            EditorGUI.BeginDisabledGroup(!set.IsLoaded || set.Table == null);
            if (GUILayout.Button("Export", EditorStyles.miniButton,
                GUILayout.Width(48), GUILayout.Height(20)))
            {
                ExportSingleBehaviour(set);
            }
            EditorGUI.EndDisabledGroup();

            // Select in Hierarchy
            if (GUILayout.Button("Select", EditorStyles.miniButton,
                GUILayout.Width(46), GUILayout.Height(20)))
            {
                if (set.IsValid)
                {
                    Selection.activeGameObject = set.Behaviour.gameObject;
                    EditorGUIUtility.PingObject(set.Behaviour.gameObject);
                }
            }

            EditorGUILayout.EndHorizontal();

            // ── Expanded variable list ──
            if (set.IsExpanded && set.IsLoaded)
            {
                DrawInlineVariables(set);
            }
            else if (set.IsExpanded && !set.IsLoaded && set.ErrorMessage != null)
            {
                EditorGUILayout.HelpBox(set.ErrorMessage, MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawInlineVariables(BehaviourVariableSet set)
        {
            if (set.Variables.Count == 0)
            {
                EditorGUILayout.LabelField("(no variables)", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUI.indentLevel++;

            foreach (var v in set.Variables)
            {
                // Apply search filter to variables too
                if (!string.IsNullOrEmpty(searchFilter) &&
                    !UdonVarViewerUtility.MatchesFilter(v.Name, searchFilter) &&
                    !UdonVarViewerUtility.MatchesFilter(v.TypeName, searchFilter))
                    continue;

                if (UdonVarViewerUtility.DrawVariableEntry(v))
                {
                    set.IsDirty = true;
                    if (toolState != ToolState.Modified)
                        SetStatus(ToolState.Modified, "Unsaved changes exist.");
                }
            }

            EditorGUI.indentLevel--;
        }

        // ─────────────────────────────────────────────────────────────────
        //  Core Logic
        // ─────────────────────────────────────────────────────────────────

        private void ScanScene()
        {
            behaviourSets.Clear();
            filteredSets.Clear();

            Log("Scanning scene for UdonBehaviours…");

            List<UdonBehaviour> allBehaviours;
            try
            {
                allBehaviours = UdonVarViewerUtility.GetAllSceneUdonBehaviours();
            }
            catch (Exception ex)
            {
                SetStatus(ToolState.Error, $"Scan failed: {ex.Message}");
                Log($"Scan exception: {ex.Message}", LogLevel.Error);
                return;
            }

            Log($"Found {allBehaviours.Count} UdonBehaviours.");

            int loadedCount = 0;
            int errorCount  = 0;

            for (int i = 0; i < allBehaviours.Count; i++)
            {
                var behaviour = allBehaviours[i];
                if (behaviour == null) continue;

                // Show progress for large scenes
                if (allBehaviours.Count > 20)
                {
                    EditorUtility.DisplayProgressBar("Scanning Scene",
                        $"Loading {behaviour.name}… ({i + 1}/{allBehaviours.Count})",
                        (float)(i + 1) / allBehaviours.Count);
                }

                try
                {
                    var set = UdonVarViewerUtility.LoadFromBehaviour(behaviour, Log);
                    behaviourSets.Add(set);

                    if (set.IsLoaded) loadedCount++;
                    else errorCount++;
                }
                catch (Exception ex)
                {
                    // Never let one bad behaviour crash the whole scan
                    Log($"Error loading '{behaviour.name}': {ex.Message}", LogLevel.Error);
                    behaviourSets.Add(new BehaviourVariableSet
                    {
                        Behaviour    = behaviour,
                        ErrorMessage = ex.Message,
                    });
                    errorCount++;
                }
            }

            if (allBehaviours.Count > 20)
                EditorUtility.ClearProgressBar();

            hasScanned = true;
            RebuildFilteredList();

            string msg = $"Scanned: {allBehaviours.Count} behaviours, {loadedCount} loaded";
            if (errorCount > 0) msg += $", {errorCount} errors";
            SetStatus(errorCount > 0 ? ToolState.Modified : ToolState.Loaded, msg);
            Log(msg);
        }

        private void LoadSingleBehaviour(BehaviourVariableSet set)
        {
            if (!set.IsValid) return;

            var newSet = UdonVarViewerUtility.LoadFromBehaviour(set.Behaviour, Log);

            // Replace in-place in the list
            int idx = behaviourSets.IndexOf(set);
            if (idx >= 0)
            {
                newSet.IsExpanded = set.IsExpanded;
                behaviourSets[idx] = newSet;
                RebuildFilteredList();
            }

            if (newSet.IsLoaded)
                Log($"Loaded '{newSet.DisplayName}': {newSet.Variables.Count} variables.");
            else
                Log($"Failed to load '{newSet.DisplayName}': {newSet.ErrorMessage}", LogLevel.Error);
        }

        private void SaveSingleBehaviour(BehaviourVariableSet set)
        {
            if (!set.IsValid || !set.IsLoaded || set.Table == null) return;

            if (UdonVarViewerUtility.SaveToBehaviour(set, Log))
            {
                Log($"Saved '{set.DisplayName}' successfully.");
                SetStatus(ToolState.Saved, $"Saved '{set.DisplayName}'.");
            }
            else
            {
                Log($"Failed to save '{set.DisplayName}'.", LogLevel.Error);
                SetStatus(ToolState.Error, $"Save failed for '{set.DisplayName}'.");
            }
        }

        private void ExportSingleBehaviour(BehaviourVariableSet set)
        {
            if (set.Table == null) return;

            string result = UdonVarViewerUtility.ExportToBase64(set.Table, Log);
            if (result != null)
            {
                GUIUtility.systemCopyBuffer = result;
                Log($"Exported '{set.DisplayName}' → {result.Length} chars. Copied to clipboard.");
                SetStatus(ToolState.Saved, $"Exported '{set.DisplayName}' to clipboard.");
            }
            else
            {
                SetStatus(ToolState.Error, $"Export failed for '{set.DisplayName}'.");
            }
        }

        private void SaveAllModified()
        {
            int saved  = 0;
            int failed = 0;

            foreach (var set in behaviourSets)
            {
                if (!set.IsDirty || !set.IsLoaded) continue;

                if (UdonVarViewerUtility.SaveToBehaviour(set, Log))
                    saved++;
                else
                    failed++;
            }

            string msg = $"Batch save: {saved} saved";
            if (failed > 0) msg += $", {failed} failed";
            SetStatus(failed > 0 ? ToolState.Error : ToolState.Saved, msg);
            Log(msg);
        }

        private void LocateByGuid()
        {
            if (string.IsNullOrWhiteSpace(guidInput)) return;

            string trimmed = guidInput.Trim();

            // First: resolve what asset this GUID maps to
            string assetPath = AssetDatabase.GUIDToAssetPath(trimmed);
            if (string.IsNullOrEmpty(assetPath))
            {
                guidResult = $"GUID '{trimmed}' not found in AssetDatabase.";
                Log(guidResult, LogLevel.Warning);
                return;
            }

            Log($"GUID → {assetPath}");

            // Find behaviours referencing this asset (only scan already-loaded scene behaviours)
            var sceneBehaviours = new List<UdonBehaviour>();
            foreach (var s in behaviourSets)
            {
                if (s.IsValid) sceneBehaviours.Add(s.Behaviour);
            }

            var found = UdonVarViewerUtility.FindBehavioursByAssetGuid(trimmed, sceneBehaviours);

            if (found.Count == 0)
            {
                guidResult = $"Asset: {assetPath}\nNo scene UdonBehaviours reference this asset.";
                Log(guidResult, LogLevel.Info);
                return;
            }

            // Expand matching behaviours in the UI
            int matchCount = 0;
            foreach (var set in behaviourSets)
            {
                if (!set.IsValid) continue;
                if (found.Contains(set.Behaviour))
                {
                    set.IsExpanded = true;
                    matchCount++;
                }
            }

            guidResult = $"Asset: {assetPath}\nFound {matchCount} UdonBehaviour(s) referencing this asset.";
            Log(guidResult);
            SetStatus(ToolState.Loaded, $"GUID lookup: {matchCount} matches.");
        }

        // ─────────────────────────────────────────────────────────────────
        //  Filtering
        // ─────────────────────────────────────────────────────────────────

        private void RebuildFilteredList()
        {
            filteredSets.Clear();

            if (string.IsNullOrEmpty(searchFilter))
            {
                // No filter — show all
                return;
            }

            foreach (var set in behaviourSets)
            {
                // Match behaviour name or path
                if (UdonVarViewerUtility.MatchesFilter(set.DisplayName, searchFilter) ||
                    UdonVarViewerUtility.MatchesFilter(set.GameObjectPath, searchFilter))
                {
                    filteredSets.Add(set);
                    continue;
                }

                // Match any variable name or type
                if (set.IsLoaded)
                {
                    bool anyVarMatch = false;
                    foreach (var v in set.Variables)
                    {
                        if (UdonVarViewerUtility.MatchesFilter(v.Name, searchFilter) ||
                            UdonVarViewerUtility.MatchesFilter(v.TypeName, searchFilter))
                        {
                            anyVarMatch = true;
                            break;
                        }
                    }
                    if (anyVarMatch)
                    {
                        filteredSets.Add(set);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────

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
