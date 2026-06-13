#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace UdonVarViewer
{
    /// <summary>
    /// Reverse-reference finder — given a target object (GameObject, Component, etc.),
    /// finds all UdonBehaviours in the scene whose public variables reference it.
    /// </summary>
    public class UdonReferenceFinderWindow : EditorWindow
    {
        // ─────────────────────────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────────────────────────

        private UnityEngine.Object targetObject;
        private List<ReferenceResult> results = new List<ReferenceResult>();
        private bool    hasSearched  = false;
        private bool    showDebugLog = false;

        // UI
        private Vector2 resultScroll;
        private Vector2 debugScroll;
        private string  statusMessage = "Drag a target object to begin.";

        // Log
        private List<LogEntry> logEntries = new List<LogEntry>();

        // Cached styles
        private GUIStyle _resultBoxStyle;

        // ─────────────────────────────────────────────────────────────────
        //  Inner types
        // ─────────────────────────────────────────────────────────────────

        private class ReferenceResult
        {
            public UdonBehaviour Behaviour;
            public string        Details;      // which variables reference the target
            public string        ObjectPath;   // full hierarchy path
        }

        // ─────────────────────────────────────────────────────────────────
        //  Menu
        // ─────────────────────────────────────────────────────────────────

        [MenuItem("Udon Var Viewer/Reference Finder")]
        public static void ShowWindow()
        {
            var w = GetWindow<UdonReferenceFinderWindow>("Udon Ref Finder");
            w.minSize = new Vector2(380, 400);
        }

        // ─────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────

        private void OnDisable()
        {
            results.Clear();
        }

        // ─────────────────────────────────────────────────────────────────
        //  OnGUI
        // ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            var styles = UdonVarViewerUtility.Styles;
            EnsureLocalStyles();

            // ── Status bar ──
            UdonVarViewerUtility.DrawStatusBar(
                hasSearched ? ToolState.Loaded : ToolState.Idle,
                statusMessage, results.Count, ref showDebugLog);

            // ── Target section ──
            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.Space(6);

            GUILayout.Label("REFERENCE FINDER", styles.SectionHeader);
            EditorGUILayout.Space(2);

            EditorGUILayout.HelpBox(
                "Drag a GameObject or Component below, then press 'Find References' to discover which UdonBehaviours in the scene reference it.",
                MessageType.Info);

            EditorGUILayout.Space(4);

            EditorGUI.BeginChangeCheck();
            var newTarget = EditorGUILayout.ObjectField(
                "Target Object", targetObject, typeof(UnityEngine.Object), true);
            if (EditorGUI.EndChangeCheck())
            {
                targetObject = newTarget;
                if (targetObject == null)
                {
                    results.Clear();
                    hasSearched = false;
                    statusMessage = "Drag a target object to begin.";
                }
            }

            EditorGUILayout.Space(6);

            // ── Find button ──
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.6f, 1.0f, 1f);

            EditorGUI.BeginDisabledGroup(targetObject == null);
            if (GUILayout.Button("🔍  Find References", GUILayout.Height(30)))
            {
                FindReferences();
            }
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = prevColor;

            EditorGUILayout.Space(6);
            EditorGUILayout.EndVertical();

            // ── Results ──
            DrawResultHeader();
            DrawResults();

            // ── Log ──
            if (showDebugLog)
                UdonVarViewerUtility.DrawDebugLog(logEntries, ref debugScroll);
        }

        private void DrawResultHeader()
        {
            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label($"RESULTS ({results.Count})", styles.SectionHeader);
            GUILayout.FlexibleSpace();

            if (results.Count > 0 && GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                results.Clear();
                hasSearched = false;
                statusMessage = "Results cleared.";
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawResults()
        {
            if (!hasSearched) return;

            float topUsed = 260f;
            float listH   = Mathf.Max(100f, position.height - topUsed - (showDebugLog ? 130f : 10f));

            resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.Height(listH));

            if (results.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No UdonBehaviours reference this object.",
                    EditorStyles.centeredGreyMiniLabel);
            }

            foreach (var r in results)
            {
                DrawResultCard(r);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResultCard(ReferenceResult r)
        {
            var styles = UdonVarViewerUtility.Styles;

            EditorGUILayout.BeginVertical(_resultBoxStyle);

            // ── Header ──
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical();
            GUILayout.Label(r.Behaviour != null ? r.Behaviour.name : "(destroyed)", styles.VarName);
            GUILayout.Label(r.ObjectPath, styles.PathLabel);
            GUILayout.Label($"Variables: {r.Details}", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            // ── Buttons ──
            EditorGUILayout.BeginVertical(GUILayout.Width(70));

            if (r.Behaviour != null)
            {
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Height(20)))
                {
                    Selection.activeGameObject = r.Behaviour.gameObject;
                    EditorGUIUtility.PingObject(r.Behaviour.gameObject);
                }

                if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Height(20)))
                {
                    var editor = UdonVariableEditorWindow.ShowWindow();
                    editor.SetTargetAndLoad(r.Behaviour);
                }
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────────────────────────
        //  Core Logic
        // ─────────────────────────────────────────────────────────────────

        private void FindReferences()
        {
            results.Clear();

            if (targetObject == null)
            {
                statusMessage = "Please drag a target object first.";
                return;
            }

            // Determine the target GameObject for matching
            GameObject targetGO = null;
            if (targetObject is GameObject go)
                targetGO = go;
            else if (targetObject is Component comp)
                targetGO = comp.gameObject;

            Log($"Searching for references to '{targetObject.name}' ({targetObject.GetType().Name})…");

            List<UdonBehaviour> allUdons;
            try
            {
                allUdons = UdonVarViewerUtility.GetAllSceneUdonBehaviours();
            }
            catch (Exception ex)
            {
                statusMessage = $"Scan failed: {ex.Message}";
                Log(statusMessage, LogLevel.Error);
                hasSearched = true;
                return;
            }

            Log($"Scanning {allUdons.Count} UdonBehaviours…");

            for (int i = 0; i < allUdons.Count; i++)
            {
                var udon = allUdons[i];
                if (udon == null) continue;

                // Show progress for large scenes
                if (allUdons.Count > 30)
                {
                    EditorUtility.DisplayProgressBar("Finding References",
                        $"Checking {udon.name}… ({i + 1}/{allUdons.Count})",
                        (float)(i + 1) / allUdons.Count);
                }

                try
                {
                    SearchBehaviourForReferences(udon, targetGO);
                }
                catch (Exception ex)
                {
                    Log($"Error checking '{udon.name}': {ex.Message}", LogLevel.Warning);
                }
            }

            if (allUdons.Count > 30)
                EditorUtility.ClearProgressBar();

            hasSearched = true;
            statusMessage = $"Found {results.Count} reference(s) across {allUdons.Count} behaviours.";
            Log(statusMessage);
            Repaint();
        }

        private void SearchBehaviourForReferences(UdonBehaviour udon, GameObject targetGO)
        {
            // Try the runtime publicVariables API first
            if (udon.publicVariables == null) return;

            IEnumerable<string> symbols;
            try
            {
                symbols = udon.publicVariables.VariableSymbols;
            }
            catch
            {
                return; // publicVariables not accessible
            }

            if (symbols == null) return;

            List<string> varsFound = new List<string>();

            foreach (string symbol in symbols)
            {
                object value;
                try
                {
                    if (!udon.publicVariables.TryGetVariableValue(symbol, out value))
                        continue;
                }
                catch
                {
                    continue;
                }

                if (value == null) continue;

                // ── Direct match: exact object ──
                if (value is UnityEngine.Object uObj && uObj == targetObject)
                {
                    varsFound.Add($"{value.GetType().Name} [{symbol}]");
                    continue;
                }

                // ── Match via GameObject: the target is a GO and value is
                //    that GO, or value is a Component on that GO ──
                if (targetGO != null)
                {
                    if (value is GameObject goVal && goVal == targetGO)
                    {
                        varsFound.Add($"GameObject [{symbol}]");
                        continue;
                    }
                    if (value is Component compVal && compVal.gameObject == targetGO)
                    {
                        varsFound.Add($"{compVal.GetType().Name} [{symbol}]");
                        continue;
                    }
                }

                // ── Array / list match ──
                if (value is IEnumerable enumerable && !(value is string))
                {
                    int index = 0;
                    try
                    {
                        foreach (var item in enumerable)
                        {
                            if (item == null) { index++; continue; }

                            if (item is UnityEngine.Object uItem && uItem == targetObject)
                            {
                                varsFound.Add($"Array [{symbol}[{index}]]");
                            }
                            else if (targetGO != null)
                            {
                                if (item is GameObject gItem && gItem == targetGO)
                                    varsFound.Add($"Array [{symbol}[{index}]] (GameObject)");
                                else if (item is Component cItem && cItem.gameObject == targetGO)
                                    varsFound.Add($"Array [{symbol}[{index}]] ({cItem.GetType().Name})");
                            }

                            index++;
                        }
                    }
                    catch
                    {
                        // Some enumerables may throw; skip gracefully
                    }
                }
            }

            if (varsFound.Count > 0)
            {
                results.Add(new ReferenceResult
                {
                    Behaviour  = udon,
                    Details    = string.Join(", ", varsFound),
                    ObjectPath = GetFullPath(udon.transform),
                });
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────

        private void EnsureLocalStyles()
        {
            if (_resultBoxStyle == null)
            {
                _resultBoxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 8, 8),
                    margin  = new RectOffset(0, 0, 2, 2),
                };
            }
        }

        private static string GetFullPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetFullPath(t.parent) + "/" + t.name;
        }

        private void Log(string msg, LogLevel level = LogLevel.Info)
        {
            UdonVarViewerUtility.AddLog(logEntries, msg, level);
        }
    }
}
#endif
