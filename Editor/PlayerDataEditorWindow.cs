#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UdonVarViewer
{
    public class PlayerDataEditorWindow : EditorWindow
    {
        // ── 路径与数据 ──────────────────────────────────────────────────────────
        private string playerDataPath = "";
        private List<PlayerDataRecord> records = new List<PlayerDataRecord>();
        private PlayerDataRecord selectedRecord;
        private bool autoScanDone;

        // ── 滚动视图 ────────────────────────────────────────────────────────────
        private Vector2 fileListScroll;
        private Vector2 variableScroll;
        private Vector2 debugScroll;

        // ── 搜索过滤 ────────────────────────────────────────────────────────────
        private string fileSearchFilter = "";
        private string varSearchFilter  = "";

        // ── 排序 & 分组 ─────────────────────────────────────────────────────────
        private bool groupByWorld   = true;
        private bool fileSortByTime = true;
        private bool varSortByTime  = true;

        // ── 新增变量 ────────────────────────────────────────────────────────────
        private string newVarName   = "";
        private int    newVarTypeIdx;
        private static readonly string[] VarTypeNames = { "Int", "String", "Bool", "Float" };

        // ── 显示选项 ────────────────────────────────────────────────────────────
        private bool showRawJson;
        private bool showDebugLog;

        // ── 状态 ────────────────────────────────────────────────────────────────
        private List<LogEntry> logEntries = new List<LogEntry>();
        private ToolState      toolState  = ToolState.Idle;
        private string         statusMessage = "No data loaded.";

        // 文件列表固定宽度
        private const float SidebarWidth = 300f;

        // String 变量输入框样式（懒加载，避免每帧 GC）
        private GUIStyle _wordWrapTextArea;

        // ═══════════════════════════════════════════════════════════════════════
        [MenuItem("Udon Var Viewer/PlayerData Editor")]
        public static PlayerDataEditorWindow ShowWindow()
        {
            var w = GetWindow<PlayerDataEditorWindow>("PlayerData Editor");
            w.minSize = new Vector2(860, 520);
            return w;
        }

        private void OnEnable()
        {
            playerDataPath = EditorPrefs.GetString("UdonVarViewer_PlayerDataPath", "");
            if (string.IsNullOrEmpty(playerDataPath) || !Directory.Exists(playerDataPath))
            {
                string def = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", "ClientSimStorage", "PlayerData"));
                if (Directory.Exists(def))
                    playerDataPath = def;
            }
        }

        private void OnDisable()
        {
            records.Clear();
            selectedRecord = null;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Main GUI
        // ═══════════════════════════════════════════════════════════════════════
        private void OnGUI()
        {
            UdonVarViewerUtility.Styles.EnsureBuilt();

            if (!autoScanDone && Directory.Exists(playerDataPath))
            {
                ScanDirectory();
                autoScanDone = true;
            }

            DrawStatusBar();
            DrawDirectorySection();
            GUILayout.Space(2);

            // ── 主体：左右分栏 ──────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(SidebarWidth));
            DrawFileBrowser();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            DrawVariableEditor();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            if (showDebugLog)
                UdonVarViewerUtility.DrawDebugLog(logEntries, ref debugScroll);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  状态栏
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawStatusBar()
        {
            int varCount = selectedRecord?.VariableCount ?? 0;
            UdonVarViewerUtility.DrawStatusBar(toolState, statusMessage, varCount, ref showDebugLog);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  目录栏
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawDirectorySection()
        {
            var s = UdonVarViewerUtility.Styles;
            EditorGUILayout.BeginVertical(EditorStyles.inspectorFullWidthMargins);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("DATA DIR", s.SectionHeader, GUILayout.Width(60));

            EditorGUI.BeginChangeCheck();
            playerDataPath = EditorGUILayout.TextField(playerDataPath);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString("UdonVarViewer_PlayerDataPath", playerDataPath);
                ResetState();
            }

            if (GUILayout.Button("Browse", EditorStyles.miniButtonLeft, GUILayout.Width(55)))
            {
                string p = EditorUtility.OpenFolderPanel("PlayerData Folder", playerDataPath, "");
                if (!string.IsNullOrEmpty(p))
                {
                    playerDataPath = p;
                    EditorPrefs.SetString("UdonVarViewer_PlayerDataPath", p);
                    ResetState();
                }
            }

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.6f, 1f, 1f);
            if (GUILayout.Button("Refresh", EditorStyles.miniButtonRight, GUILayout.Width(58)))
            {
                ScanDirectory();
                autoScanDone = true;
            }
            GUI.backgroundColor = prevBg;

            EditorGUILayout.EndHorizontal();

            if (!Directory.Exists(playerDataPath))
                EditorGUILayout.HelpBox(
                    "Directory not found. Browse to ClientSimStorage/PlayerData or paste the path.",
                    MessageType.Warning);

            EditorGUILayout.EndVertical();
        }

        // ── 辅助：重置状态 ──────────────────────────────────────────────────────
        private void ResetState()
        {
            records.Clear();
            selectedRecord = null;
            autoScanDone   = false;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  左侧文件浏览器
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawFileBrowser()
        {
            if (!Directory.Exists(playerDataPath)) return;

            // ── 工具栏 ──────────────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label($"FILES ({records.Count})", GUILayout.Width(72));

            groupByWorld   = DrawToolbarToggle(groupByWorld,   "By World",  62);
            fileSortByTime = DrawToolbarToggle(fileSortByTime, "Time Sort", 65);

            GUILayout.FlexibleSpace();
            fileSearchFilter = DrawToolbarSearch(fileSearchFilter, 100);

            EditorGUILayout.EndHorizontal();

            // ── 文件列表 ────────────────────────────────────────────────────────
            EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
            fileListScroll = EditorGUILayout.BeginScrollView(fileListScroll);
            DrawGroupedFileList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // ── 分组文件列表 ────────────────────────────────────────────────────────
        private void DrawGroupedFileList()
        {
            // 过滤
            IEnumerable<PlayerDataRecord> filtered = records;
            if (!string.IsNullOrEmpty(fileSearchFilter))
                filtered = records.Where(f => MatchesFileFilter(f, fileSearchFilter));

            // 分组 & 排序
            IEnumerable<FileGroup> groups = BuildFileGroups(filtered);

            bool anyVisible = false;
            foreach (var grp in groups)
            {
                if (grp.Items.Count == 0) continue;

                int varSum = grp.Items.Sum(f => f.VariableCount);
                GUILayout.Label(
                    $" {grp.Label}  ({grp.Items.Count} files · {varSum} vars)",
                    EditorStyles.boldLabel);

                foreach (var file in grp.Items)
                {
                    anyVisible = true;
                    DrawFileRow(file);
                }
                GUILayout.Space(4);
            }

            if (!anyVisible && records.Count > 0)
                EditorGUILayout.LabelField(
                    "No files match filter.", EditorStyles.centeredGreyMiniLabel);
        }

        private IEnumerable<FileGroup> BuildFileGroups(IEnumerable<PlayerDataRecord> filtered)
        {
            IEnumerable<FileGroup> groups;
            if (groupByWorld)
            {
                groups = filtered
                    .GroupBy(f => f.WorldName)
                    .Select(g => new FileGroup {
                        Label   = $"World: {g.Key}",
                        Items   = SortItems(g, byWorld: false),
                        MaxTime = g.Max(f => f.LastWriteTime)
                    });
            }
            else
            {
                groups = filtered
                    .GroupBy(f => f.PlayerId)
                    .Select(g => new FileGroup {
                        Label   = $"Player {g.Key}",
                        Items   = SortItems(g, byWorld: true),
                        MaxTime = g.Max(f => f.LastWriteTime)
                    });
            }

            return fileSortByTime
                ? groups.OrderByDescending(g => g.MaxTime)
                : groups.OrderBy(g => g.Label);
        }

        private List<PlayerDataRecord> SortItems(
            IEnumerable<PlayerDataRecord> src, bool byWorld)
        {
            if (fileSortByTime)
                return src.OrderByDescending(f => f.LastWriteTime).ToList();
            return byWorld
                ? src.OrderBy(f => f.WorldName).ToList()
                : src.OrderBy(f => f.PlayerId).ToList();
        }

        // ── 单行文件项 ──────────────────────────────────────────────────────────
        private void DrawFileRow(PlayerDataRecord file)
        {
            bool isSel  = selectedRecord == file;
            var  prevBg = GUI.backgroundColor;

            if (isSel)
                GUI.backgroundColor = new Color(0.25f, 0.45f, 0.75f);

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // ── 左：文件信息 ────────────────────────────────────────────────────
            EditorGUILayout.BeginVertical();

            string dirtyMark = file.IsDirty  ? "* " : "";
            string loadMark  = file.IsLoaded ? $" [{file.VariableCount} vars]" : "";
            string label     = $"{dirtyMark}{file.WorldName}{loadMark}";

            if (GUILayout.Button(label, EditorStyles.label, GUILayout.ExpandWidth(true)))
                SelectRecord(file);

            string timeStr = file.LastWriteTime.ToString("MM-dd HH:mm");
            GUILayout.Label(
                $"P{file.PlayerId} | {timeStr}",
                EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.EndVertical();

            // ── 右：操作按钮 ────────────────────────────────────────────────────
            EditorGUILayout.BeginVertical(GUILayout.Width(26));
            GUILayout.Space(2);

            if (file.IsDirty)
                DrawTinyButton("S", () => SaveRecord(file),
                    new Color(0.3f, 0.8f, 0.3f));

            DrawTinyButton("X", () =>
            {
                if (EditorUtility.DisplayDialog("Clear Data?",
                    $"Delete all data in \"{file.FileName}\"?\nThis cannot be undone.",
                    "Delete", "Cancel"))
                    ClearRecordData(file);
            }, new Color(0.9f, 0.3f, 0.3f));

            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
            GUI.backgroundColor = prevBg;
        }

        // ── 小按钮辅助 ──────────────────────────────────────────────────────────
        private static void DrawTinyButton(string label, Action onClick, Color tint)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = tint;
            if (GUILayout.Button(label, EditorStyles.miniButton,
                GUILayout.Width(22), GUILayout.Height(16)))
                onClick?.Invoke();
            GUI.backgroundColor = prev;
        }

        // ── 文件过滤匹配 ────────────────────────────────────────────────────────
        private static bool MatchesFileFilter(PlayerDataRecord f, string filter)
        {
            if (f.FileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (f.WorldName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (f.PlayerId.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return RawFileContains(f, filter);
        }

        // ── FileGroup 结构 ──────────────────────────────────────────────────────
        private struct FileGroup
        {
            public string              Label;
            public List<PlayerDataRecord> Items;
            public DateTime            MaxTime;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  右侧变量编辑器
        // ═══════════════════════════════════════════════════════════════════════
        private void DrawVariableEditor()
        {
            // ── 未选中状态 ──────────────────────────────────────────────────────
            if (selectedRecord == null || !selectedRecord.IsLoaded)
            {
                DrawEmptyEditor();
                return;
            }

            var vars = selectedRecord.JsonFields;

            // ── 顶部工具栏 ──────────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label($"VARIABLES ({vars.Count})", GUILayout.Width(110));
            varSortByTime = DrawToolbarToggle(varSortByTime, "Time Sort", 65);

            GUILayout.FlexibleSpace();
            varSearchFilter = DrawToolbarSearch(varSearchFilter, 140);
            showRawJson     = DrawToolbarToggle(showRawJson, "Raw JSON", 65);

            if (selectedRecord.IsDirty)
            {
                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.3f, 0.75f, 0.3f);
                if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    SaveRecord(selectedRecord);
                GUI.backgroundColor = prevBg;
            }

            EditorGUILayout.EndHorizontal();

            // ── 新增变量栏（Raw 模式时隐藏）─────────────────────────────────────
            if (!showRawJson)
                DrawAddVariableBar();

            // ── Raw JSON 视图 ────────────────────────────────────────────────────
            if (showRawJson)
            {
                DrawRawJsonView();
                return;
            }

            // ── 变量列表 ────────────────────────────────────────────────────────
            DrawVariableList(vars);
        }

        private void DrawEmptyEditor()
        {
            EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                "Select a file from the left to view variables.",
                EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void DrawAddVariableBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Add:", GUILayout.Width(30));
            newVarName   = EditorGUILayout.TextField(newVarName,
                EditorStyles.toolbarTextField, GUILayout.Width(160));
            newVarTypeIdx = EditorGUILayout.Popup(newVarTypeIdx, VarTypeNames,
                EditorStyles.toolbarPopup, GUILayout.Width(65));

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(newVarName));
            if (GUILayout.Button("+ Add", EditorStyles.toolbarButton, GUILayout.Width(48)))
                AddVariable();
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRawJsonView()
        {
            EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
            variableScroll = EditorGUILayout.BeginScrollView(variableScroll);
            string raw = selectedRecord.RawJson?.ToString(Formatting.Indented) ?? "{}";
            EditorGUILayout.TextArea(raw, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawVariableList(Dictionary<string, JObject> vars)
        {
            EditorGUILayout.BeginVertical("box", GUILayout.ExpandHeight(true));
            variableScroll = EditorGUILayout.BeginScrollView(
                variableScroll, false, false);

            // 构建带时间的排序序列
            var sorted = vars
                .Select(kvp => new {
                    Key  = kvp.Key,
                    Data = kvp.Value,
                    Time = ParseVarTime(kvp.Value)
                });

            sorted = varSortByTime
                ? sorted.OrderByDescending(v => v.Time).ThenBy(v => v.Key)
                : sorted.OrderBy(v => v.Key);

            bool anyVisible = false;
            foreach (var v in sorted)
            {
                if (!PassesVarFilter(v.Key, v.Data)) continue;
                anyVisible = true;
                DrawVariableEntry(v.Key, v.Data, v.Time);
            }

            if (!anyVisible)
            {
                string hint = vars.Count == 0
                    ? "No variables. Use the Add bar above to create one."
                    : $"No variables match \"{varSearchFilter}\".";
                EditorGUILayout.LabelField(hint, EditorStyles.centeredGreyMiniLabel);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // ── 单条变量 ────────────────────────────────────────────────────────────
        private void DrawVariableEntry(string varName, JObject varData, DateTime updateTime)
        {
            var    s        = UdonVarViewerUtility.Styles;
            string type     = varData["Value"]?["type"]?.ToString() ?? "?";
            JToken valToken = varData["Value"]?["value"];

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // 头部：变量名 | 类型 | 时间 | 删除按钮
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label(
                UdonVarViewerUtility.HighlightText(varName, varSearchFilter),
                s.VarName, GUILayout.MaxWidth(180));

            GUILayout.FlexibleSpace();

            if (updateTime != DateTime.MinValue)
                GUILayout.Label(
                    updateTime.ToLocalTime().ToString("MM-dd HH:mm"),
                    EditorStyles.centeredGreyMiniLabel);

            GUILayout.Label(type, s.TypeBadge, GUILayout.Width(48));

            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
            {
                if (EditorUtility.DisplayDialog(
                    "Delete Variable?", $"Delete \"{varName}\"?", "Delete", "Cancel"))
                {
                    DoDeleteVariable(varName);
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.EndHorizontal();

            // 值编辑区（带左缩进）
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            EditorGUI.BeginChangeCheck();
            object newVal = DrawTypedField(valToken, type);
            if (EditorGUI.EndChangeCheck())
                ApplyVariableChange(varName, type, newVal);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  类型字段渲染
        // ═══════════════════════════════════════════════════════════════════════
        private object DrawTypedField(JToken token, string type)
        {
            // 懒加载：GUIStyle 只能在 OnGUI 阶段构造，缓存避免每帧 GC
            if (_wordWrapTextArea == null)
                _wordWrapTextArea = new GUIStyle(EditorStyles.textArea) { wordWrap = true };

            object cur = TokenToObject(token);
            switch (type)
            {
                case "Int":
                    int iv = cur is int i ? i : cur is long l ? (int)l : 0;
                    return EditorGUILayout.IntField(iv);
                case "Float":
                    float fv = cur is float f ? f : cur is double d ? (float)d : 0f;
                    return EditorGUILayout.FloatField(fv);
                case "Bool":
                    return EditorGUILayout.Toggle(cur is bool b && b);
                case "String":
                    string sv = cur as string ?? "";
                    int lines = 1 + sv.Count(c => c == '\n');
                    // 最少 3 行，随换行数增高，上限 300px
                    float h = Mathf.Clamp(lines * 18f, 54f, 300f);
                    return EditorGUILayout.TextArea(sv, _wordWrapTextArea,
                        GUILayout.MinHeight(h), GUILayout.MaxHeight(300));
                default:
                    EditorGUILayout.LabelField(
                        token?.ToString() ?? "null", EditorStyles.wordWrappedLabel);
                    return cur;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  JSON 类型转换工具
        // ═══════════════════════════════════════════════════════════════════════
        private static object TokenToObject(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            switch (token.Type)
            {
                case JTokenType.Integer: return (int)token;
                case JTokenType.Float:   return (float)token;
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.String:  return (string)token;
                default:                 return token.ToString();
            }
        }

        private static JToken ObjectToToken(object val, string type)
        {
            switch (type)
            {
                case "Int":    return new JValue(val is int    i ? i : Convert.ToInt32(val));
                case "Float":  return new JValue(val is float  f ? f : Convert.ToSingle(val));
                case "Bool":   return new JValue(val is bool   b ? b : Convert.ToBoolean(val));
                case "String": return new JValue(val?.ToString() ?? "");
                default:       return new JValue(val?.ToString() ?? "");
            }
        }

        private static JToken DefaultToken(string type)
        {
            switch (type)
            {
                case "Int":    return new JValue(0);
                case "Float":  return new JValue(0f);
                case "Bool":   return new JValue(false);
                case "String": return new JValue("");
                default:       return new JValue("");
            }
        }

        private DateTime ParseVarTime(JObject varData)
        {
            string t = varData["LastUpdated"]?.ToString();
            if (!string.IsNullOrEmpty(t) &&
                DateTimeOffset.TryParse(t, out var dto))
                return dto.UtcDateTime;
            return DateTime.MinValue;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  操作逻辑
        // ═══════════════════════════════════════════════════════════════════════
        private void ScanDirectory()
        {
            records.Clear();
            selectedRecord = null;

            if (!Directory.Exists(playerDataPath))
            {
                Log("Directory not found: " + playerDataPath, LogLevel.Warning);
                SetStatus(ToolState.Error, "Directory not found.");
                return;
            }

            foreach (var f in Directory.GetFiles(playerDataPath, "PlayerData_*.json"))
            {
                string name  = Path.GetFileNameWithoutExtension(f);
                string[] parts = name.Split('_');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[1], out int pid)) continue;

                records.Add(new PlayerDataRecord
                {
                    FilePath      = f,
                    FileName      = Path.GetFileName(f),
                    PlayerId      = pid,
                    WorldName     = string.Join("_", parts.Skip(2)),
                    LastWriteTime = File.GetLastWriteTime(f)
                });
            }

            SetStatus(ToolState.Loaded, $"Found {records.Count} data files.");
            Log($"Scanned {records.Count} valid files.", LogLevel.Info);
        }

        private void SelectRecord(PlayerDataRecord rec)
        {
            selectedRecord  = rec;
            varSearchFilter = "";

            if (!rec.IsLoaded)
            {
                rec.Load();
                if (rec.LoadError != null)
                    Log(rec.LoadError, LogLevel.Error);
            }
            MarkStatus();
        }

        private void SaveRecord(PlayerDataRecord rec)
        {
            if (rec?.RawJson == null) return;
            try
            {
                File.WriteAllText(rec.FilePath, rec.RawJson.ToString(Formatting.Indented));
                rec.LastWriteTime = File.GetLastWriteTime(rec.FilePath);
                rec.IsDirty       = false;
                rec.ReloadFields();
                Log($"Saved: {rec.FileName}", LogLevel.Info);
                MarkStatus();
                Repaint();
            }
            catch (Exception ex)
            {
                Log($"Save failed: {ex.Message}", LogLevel.Error);
                SetStatus(ToolState.Error, "Save failed.");
            }
        }

        private void ClearRecordData(PlayerDataRecord rec)
        {
            if (rec?.RawJson == null) return;
            rec.RawJson = new JObject();
            rec.ReloadFields();
            rec.IsDirty = true;
            MarkStatus();
        }

        private void ApplyVariableChange(string varName, string type, object newVal)
        {
            if (selectedRecord?.RawJson == null) return;
            var entry = selectedRecord.RawJson[varName] as JObject;
            if (entry == null) return;

            var valObj = entry["Value"] as JObject ?? new JObject();
            valObj["type"]  = type;
            valObj["value"] = ObjectToToken(newVal, type);
            entry["Value"]       = valObj;
            entry["LastUpdated"] = DateTimeOffset.Now.ToString("o");

            selectedRecord.IsDirty = true;
            MarkStatus();
            Repaint();
        }

        private void DoDeleteVariable(string varName)
        {
            if (selectedRecord?.RawJson == null) return;
            selectedRecord.RawJson.Remove(varName);
            selectedRecord.IsDirty = true;
            selectedRecord.ReloadFields();
            MarkStatus();
        }

        private void AddVariable()
        {
            if (selectedRecord?.RawJson == null || string.IsNullOrWhiteSpace(newVarName))
                return;

            string vName = newVarName.Trim();
            string vType = VarTypeNames[newVarTypeIdx];
            newVarName   = "";

            var entry = new JObject
            {
                ["LastUpdated"] = DateTimeOffset.Now.ToString("o"),
                ["Key"]         = vName,
                ["Value"]       = new JObject
                {
                    ["type"]  = vType,
                    ["value"] = DefaultToken(vType)
                }
            };

            selectedRecord.RawJson[vName] = entry;
            selectedRecord.IsDirty        = true;
            selectedRecord.ReloadFields();
            MarkStatus();
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  工具栏控件辅助
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>绘制工具栏切换按钮，返回新状态。</summary>
        private static bool DrawToolbarToggle(bool current, string label, float width) =>
            GUILayout.Toggle(current, label, EditorStyles.toolbarButton, GUILayout.Width(width));

        /// <summary>绘制工具栏搜索框（含 ×清除按钮），返回新过滤文本。</summary>
        private static string DrawToolbarSearch(string current, float width)
        {
            string next = EditorGUILayout.TextField(
                current, EditorStyles.toolbarSearchField, GUILayout.Width(width));
            if (!string.IsNullOrEmpty(next) &&
                GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(18)))
                return "";
            return next;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  过滤辅助
        // ═══════════════════════════════════════════════════════════════════════
        private bool PassesVarFilter(string key, JObject varData)
        {
            if (string.IsNullOrEmpty(varSearchFilter)) return true;
            if (key.IndexOf(varSearchFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return VarValueContains(varData, varSearchFilter);
        }

        private static bool VarValueContains(JObject varData, string filter)
        {
            if (varData == null) return false;
            var val = varData["Value"];
            if (val == null) return false;
            return val.ToString()
                .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RawFileContains(PlayerDataRecord rec, string filter)
        {
            if (rec == null || string.IsNullOrEmpty(rec.FilePath)) return false;
            try
            {
                return File.ReadAllText(rec.FilePath)
                    .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  状态管理
        // ═══════════════════════════════════════════════════════════════════════
        private void MarkStatus()
        {
            if (selectedRecord == null)
            {
                SetStatus(ToolState.Idle, "No file selected.");
                return;
            }
            int n = selectedRecord.VariableCount;
            if (selectedRecord.IsDirty)
                SetStatus(ToolState.Modified,
                    $"\"{selectedRecord.WorldName}\" — unsaved. ({n} vars)");
            else
                SetStatus(ToolState.Loaded,
                    $"\"{selectedRecord.WorldName}\" — {n} variables.");
        }

        private void SetStatus(ToolState state, string msg)
        {
            toolState     = state;
            statusMessage = msg;
            Repaint();
        }

        private void Log(string msg, LogLevel level = LogLevel.Info) =>
            UdonVarViewerUtility.AddLog(logEntries, msg, level);

        // ═══════════════════════════════════════════════════════════════════════
        //  数据模型
        // ═══════════════════════════════════════════════════════════════════════
        public class PlayerDataRecord
        {
            public string   FilePath;
            public string   FileName;
            public int      PlayerId;
            public string   WorldName;
            public DateTime LastWriteTime;

            public JObject                       RawJson;
            public Dictionary<string, JObject>   JsonFields   = new Dictionary<string, JObject>();
            public bool                          IsLoaded;
            public bool                          IsDirty;
            public string                        LoadError;

            public int VariableCount => JsonFields.Count;

            public void Load()
            {
                try
                {
                    string content = File.ReadAllText(FilePath);
                    RawJson   = JObject.Parse(content);
                    ReloadFields();
                    IsLoaded  = true;
                    LoadError = null;
                }
                catch (Exception ex)
                {
                    LoadError = $"Failed to load {FileName}: {ex.Message}";
                    RawJson   = new JObject();
                    IsLoaded  = false;
                }
            }

            public void ReloadFields()
            {
                JsonFields.Clear();
                if (RawJson == null) return;
                foreach (var prop in RawJson.Properties())
                    if (prop.Value is JObject jo)
                        JsonFields[prop.Name] = jo;
            }
        }
    }
}
#endif