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

        // Search State (Find-on-Page mode)
        private List<int> searchMatchIndices   = new List<int>();
        private int       currentMatchIndex    = -1;
        private bool      scrollToCurrentMatch = false;
        private Dictionary<int, Rect> cardRects = new Dictionary<int, Rect>();

        // Pagination State
        private int currentPage = 0;
        private int itemsPerPage = 100;

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
            searchMatchIndices.Clear();
            cardRects.Clear();
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
            DrawPagination();

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
                UpdateSearchMatches();

            if (!string.IsNullOrEmpty(searchFilter))
            {
                if (searchMatchIndices.Count > 0)
                {
                    GUILayout.Label($"{currentMatchIndex + 1} of {searchMatchIndices.Count}", EditorStyles.miniLabel);
                    if (GUILayout.Button("↑", EditorStyles.toolbarButton, GUILayout.Width(22)))
                    {
                        currentMatchIndex = (currentMatchIndex - 1 + searchMatchIndices.Count) % searchMatchIndices.Count;
                        JumpToMatch(currentMatchIndex);
                    }
                    if (GUILayout.Button("↓", EditorStyles.toolbarButton, GUILayout.Width(22)))
                    {
                        currentMatchIndex = (currentMatchIndex + 1) % searchMatchIndices.Count;
                        JumpToMatch(currentMatchIndex);
                    }
                }
                else
                {
                    GUILayout.Label("0 of 0", EditorStyles.miniLabel);
                }

                if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    searchFilter = "";
                    UpdateSearchMatches();
                }
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

        // ─── Pagination ──────────────────────────────────────────────────

        private void DrawPagination()
        {
            if (behaviourSets.Count == 0) return;

            int totalPages = Mathf.CeilToInt((float)behaviourSets.Count / itemsPerPage);
            if (totalPages == 0) totalPages = 1;

            if (currentPage >= totalPages) currentPage = Mathf.Max(0, totalPages - 1);
            if (currentPage < 0) currentPage = 0;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(currentPage == 0);
            if (GUILayout.Button("|<", EditorStyles.toolbarButton, GUILayout.Width(25))) currentPage = 0;
            if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(25))) currentPage--;
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(10);
            GUILayout.Label($"Page {currentPage + 1} of {totalPages}  ({behaviourSets.Count} total)", EditorStyles.miniLabel);
            GUILayout.Space(10);

            EditorGUI.BeginDisabledGroup(currentPage >= totalPages - 1);
            if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(25))) currentPage++;
            if (GUILayout.Button(">|", EditorStyles.toolbarButton, GUILayout.Width(25))) currentPage = totalPages - 1;
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(20);
            GUILayout.Label("Per page:", EditorStyles.miniLabel);
            int newPerPage = EditorGUILayout.IntField(itemsPerPage, GUILayout.Width(40));
            if (newPerPage != itemsPerPage && newPerPage > 0)
            {
                itemsPerPage = newPerPage;
                currentPage = 0;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ─── Behaviour List ──────────────────────────────────────────────

        private void DrawBehaviourList()
        {
            if (!hasScanned) return;

            var styles = UdonVarViewerUtility.Styles;

            var listToShow = behaviourSets;

            if (listToShow.Count == 0)
            {
                EditorGUILayout.LabelField("No UdonBehaviours found in scene.",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // Pagination boundaries
            int totalItems = listToShow.Count;
            int totalPages = Mathf.CeilToInt((float)totalItems / itemsPerPage);
            if (currentPage >= totalPages) currentPage = Mathf.Max(0, totalPages - 1);
            if (currentPage < 0) currentPage = 0;

            int startIndex = currentPage * itemsPerPage;
            int endIndex = Mathf.Min(startIndex + itemsPerPage, totalItems);

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);

            for (int i = startIndex; i < endIndex; i++)
            {
                var set = listToShow[i];
                bool isCurrentMatch = searchMatchIndices.Count > 0 && searchMatchIndices[currentMatchIndex] == i;

                EditorGUILayout.BeginVertical();
                DrawBehaviourCard(set, isCurrentMatch);
                EditorGUILayout.EndVertical();

                if (Event.current.type == EventType.Repaint)
                {
                    cardRects[i] = GUILayoutUtility.GetLastRect();

                    if (scrollToCurrentMatch && isCurrentMatch)
                    {
                        Rect r = cardRects[i];
                        if (r.height > 0)
                        {
                            mainScroll.y = Mathf.Max(0, r.y - 40); // Scroll slightly above the card
                            scrollToCurrentMatch = false;
                            Repaint(); // Force immediately applying the scroll
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBehaviourCard(BehaviourVariableSet set, bool isCurrentMatch)
        {
            var styles = UdonVarViewerUtility.Styles;

            // Outer card box - tint yellow if it's the current search match
            var prevColor = GUI.backgroundColor;
            if (isCurrentMatch) GUI.backgroundColor = new Color(1f, 1f, 0.4f, 1f);
            EditorGUILayout.BeginVertical(styles.VarBox);
            if (isCurrentMatch) GUI.backgroundColor = prevColor;

            // ── Header row ──
            EditorGUILayout.BeginHorizontal();

            // Foldout
            set.IsExpanded = EditorGUILayout.Foldout(set.IsExpanded, "", true);

            // Name
            string displayName = set.IsValid ? set.DisplayName : "(destroyed)";
            displayName = UdonVarViewerUtility.HighlightText(displayName, searchFilter);
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
                string dispPath = UdonVarViewerUtility.HighlightText(set.GameObjectPath, searchFilter);
                EditorGUILayout.LabelField(dispPath, styles.PathLabel);
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
                    GUILayout.Width(60), GUILayout.Height(20)))
                {
                    LoadSingleBehaviour(set);
                }
            }

            // Save This
            EditorGUI.BeginDisabledGroup(!set.IsDirty || !set.IsLoaded);
            if (GUILayout.Button("Save", EditorStyles.miniButton,
                GUILayout.Width(55), GUILayout.Height(20)))
            {
                SaveSingleBehaviour(set);
            }
            EditorGUI.EndDisabledGroup();

            // Export
            EditorGUI.BeginDisabledGroup(!set.IsLoaded || set.Table == null);
            if (GUILayout.Button("Export", EditorStyles.miniButton,
                GUILayout.Width(65), GUILayout.Height(20)))
            {
                ExportSingleBehaviour(set);
            }
            EditorGUI.EndDisabledGroup();

            // Select in Hierarchy
            if (GUILayout.Button("Select", EditorStyles.miniButton,
                GUILayout.Width(60), GUILayout.Height(20)))
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
                // We no longer filter out variables to preserve context.
                // We just pass the searchFilter to highlight matching text!
                if (UdonVarViewerUtility.DrawVariableEntry(v, searchFilter))
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
            searchMatchIndices.Clear();
            cardRects.Clear();

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
            UpdateSearchMatches();

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
                UpdateSearchMatches();
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

        private void UpdateSearchMatches()
        {
            searchMatchIndices.Clear();
            currentMatchIndex = -1;

            if (string.IsNullOrEmpty(searchFilter)) return;

            for (int i = 0; i < behaviourSets.Count; i++)
            {
                var set = behaviourSets[i];

                // Match behaviour name or path
                if (UdonVarViewerUtility.MatchesFilter(set.DisplayName, searchFilter) ||
                    UdonVarViewerUtility.MatchesFilter(set.GameObjectPath, searchFilter))
                {
                    searchMatchIndices.Add(i);
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
                        searchMatchIndices.Add(i);
                    }
                }
            }

            if (searchMatchIndices.Count > 0)
            {
                currentMatchIndex = 0;
                JumpToMatch(0);
            }
        }

        private void JumpToMatch(int matchListIndex)
        {
            if (matchListIndex < 0 || matchListIndex >= searchMatchIndices.Count) return;
            
            int globalItemIndex = searchMatchIndices[matchListIndex];
            behaviourSets[globalItemIndex].IsExpanded = true;
            
            int targetPage = globalItemIndex / itemsPerPage;
            if (currentPage != targetPage)
            {
                currentPage = targetPage;
            }
            
            scrollToCurrentMatch = true;
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
