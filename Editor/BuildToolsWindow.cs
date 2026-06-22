using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace BuildTools
{
    /// <summary>
    /// BuildTools 主编辑器窗口
    /// 功能：在场景中画多边形 → 沿边缘随机排列 Prefab 建筑
    /// </summary>
    public class BuildToolsWindow : EditorWindow
    {
        // ============================================================
        // Static
        // ============================================================

        private static readonly int s_BuildToolsHash = "BuildToolsEditor".GetHashCode();
        private static BuildToolsWindow s_activeWindow;
        private static Material s_zebraMaterial;
        private static Mesh s_fillMesh;

        [MenuItem("Window/Build Tools")]
        public static void Init()
        {
            BuildToolsWindow window = (BuildToolsWindow)GetWindow(typeof(BuildToolsWindow));
            window.titleContent = new GUIContent("Build Tools");
            window.minSize = new Vector2(320f, 440f);
            window.Show();
        }

        // ============================================================
        // Tool Mode
        // ============================================================

        private enum ToolMode
        {
            Disabled,   // 其他工具激活 → 不交互
            Add,        // Q (View) → 点击地面添加顶点
            Edit        // W (Move) → PositionHandle 编辑顶点
        }

        // ============================================================
        // Instance Fields
        // ============================================================

        // 设置
        private BuildToolsSettings m_settings;
        private BuildToolsSceneSettings m_sceneSettings;

        // 场景视图状态
        private bool m_isActive = true;  // 工具是否激活
        private ToolMode m_currentMode = ToolMode.Add;
        private Vector3 m_lastMouseWorldPos = Vector3.zero;

        // 实时预览
        private bool m_livePreview = false;
        private bool m_livePreviewDirty = false;
        private float m_livePreviewDebounceTime;
        private int m_nearestEdgeInsertIndex = -1;  // Shift 悬停时最近边的插入位置
        private Vector3 m_nearestEdgePoint = Vector3.zero;  // Shift 悬停时边上最近点
        private int m_nearestVertexIndex = -1;  // Ctrl 悬停时最近的顶点索引

        // GUI 折叠状态（持久化到 EditorPrefs）
        private bool m_polygonFoldout = true;
        private bool m_prefabsFoldout = true;
        private bool m_placementFoldout = true;
        private bool m_visualsFoldout = true;

        // 滚动位置
        private Vector2 m_scrollPos;

        // 生成的建筑跟踪
        private List<GameObject> m_generatedBuildings = new List<GameObject>();

        // ============================================================
        // Lifecycle
        // ============================================================

        private void OnEnable()
        {
            if (s_activeWindow != null && s_activeWindow != this)
            {
                Close();
                return;
            }

            s_activeWindow = this;
            hideFlags = HideFlags.HideAndDontSave;

            LoadSettings();
            LoadSceneSettings();
            LoadEditorPrefs();

            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += EditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= EditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;

            SaveEditorPrefs();

            if (s_activeWindow == this)
                s_activeWindow = null;
        }

        private void EditorUpdate()
        {
            // 实时预览防抖再生
            if (m_livePreview && m_livePreviewDirty && CanGenerate())
            {
                if (Time.realtimeSinceStartup - m_livePreviewDebounceTime > 0.3f)
                {
                    m_livePreviewDirty = false;
                    GenerateBuildings();
                }
            }

            if (!m_isActive)
            {
                m_currentMode = ToolMode.Disabled;
                return;
            }

            // 通过 Unity 原生工具栏快捷键切换工作模式：
            //   Q → View 工具   → 添加模式（点击地面添加顶点）
            //   W → Move 工具   → 编辑模式（顶点出现坐标轴可拖拽）
            //
            //   注意：按 W 后 Unity 会自动切换到 Move 工具，但我们必须
            //   立即重置为 None，否则 Unity 自带的变换手柄会拦截鼠标事件，
            //   导致我们自定义的 PositionHandle 无法响应拖拽。
            Tool currentTool = Tools.current;

            if (currentTool == Tool.View)
            {
                m_currentMode = ToolMode.Add;
            }
            else if (currentTool == Tool.Move)
            {
                // 用户按了 W → 进入编辑模式，但不要用 Unity 的 Move 工具
                m_currentMode = ToolMode.Edit;
                Tools.current = Tool.None;
            }
            else if (currentTool == Tool.None)
            {
                // 保持当前模式不变（可能是 Edit 模式重置后的状态）
            }
            else
            {
                // 其他工具 (Rotate/Scale/Rect/Transform) → 禁用
                m_currentMode = ToolMode.Disabled;
            }
        }

        private void OnUndoRedo()
        {
            Repaint();
        }

        private void OnFocus()
        {
            s_activeWindow = this;
        }

        private void OnDestroy()
        {
            if (s_activeWindow == this)
                s_activeWindow = null;
        }

        // ============================================================
        // Settings Management
        // ============================================================

        private void LoadSettings()
        {
            // 确保 Settings 目录存在
            if (!AssetDatabase.IsValidFolder(BuildToolsSettings.k_SettingsFolder))
            {
                string parent = "Assets/BuildTools";
                if (!AssetDatabase.IsValidFolder(parent))
                    AssetDatabase.CreateFolder("Assets", "BuildTools");
                AssetDatabase.CreateFolder(parent, "Settings");
            }

            // 加载或创建设置资源
            m_settings = AssetDatabase.LoadAssetAtPath<BuildToolsSettings>(BuildToolsSettings.k_SettingsPath);

            if (m_settings == null)
            {
                m_settings = CreateInstance<BuildToolsSettings>();
                m_settings.ResetAll();
                AssetDatabase.CreateAsset(m_settings, BuildToolsSettings.k_SettingsPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            BuildToolsSettings.current = m_settings;
        }

        private void LoadSceneSettings()
        {
            // 在场景中查找或创建场景设置 GameObject
            const string kSceneSettingsName = "BuildToolsSceneSettings";

            GameObject go = GameObject.Find(kSceneSettingsName);
            if (go == null)
            {
                go = new GameObject(kSceneSettingsName);
                go.hideFlags = HideFlags.HideInHierarchy;
                m_sceneSettings = go.AddComponent<BuildToolsSceneSettings>();
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            }
            else
            {
                m_sceneSettings = go.GetComponent<BuildToolsSceneSettings>();
                if (m_sceneSettings == null)
                    m_sceneSettings = go.AddComponent<BuildToolsSceneSettings>();
            }

            // 恢复已生成建筑列表
            if (m_sceneSettings != null && m_sceneSettings.generatedBuildings != null)
            {
                m_generatedBuildings = m_sceneSettings.generatedBuildings;
            }
        }

        private void LoadEditorPrefs()
        {
            m_polygonFoldout = EditorPrefs.GetBool("BuildTools_PolygonFoldout", true);
            m_prefabsFoldout = EditorPrefs.GetBool("BuildTools_PrefabsFoldout", true);
            m_placementFoldout = EditorPrefs.GetBool("BuildTools_PlacementFoldout", true);
            m_visualsFoldout = EditorPrefs.GetBool("BuildTools_VisualsFoldout", true);
        }

        private void SaveEditorPrefs()
        {
            EditorPrefs.SetBool("BuildTools_PolygonFoldout", m_polygonFoldout);
            EditorPrefs.SetBool("BuildTools_PrefabsFoldout", m_prefabsFoldout);
            EditorPrefs.SetBool("BuildTools_PlacementFoldout", m_placementFoldout);
            EditorPrefs.SetBool("BuildTools_VisualsFoldout", m_visualsFoldout);
        }

        // ============================================================
        // Undo Helper
        // ============================================================

        private void RegisterSettingsUndo(string message = "BT: Change Setting")
        {
            if (m_settings != null)
            {
                Undo.RecordObject(m_settings, message);
            }
            MarkLivePreviewDirty();
        }

        private void MarkLivePreviewDirty()
        {
            if (m_livePreview)
            {
                m_livePreviewDirty = true;
                m_livePreviewDebounceTime = Time.realtimeSinceStartup;
            }
        }

        // ============================================================
        // OnGUI - 窗口 UI
        // ============================================================

        private void OnGUI()
        {
            if (m_settings == null)
            {
                EditorGUILayout.HelpBox("设置文件未加载，请重新打开窗口。", MessageType.Error);
                if (GUILayout.Button("重新加载"))
                    LoadSettings();
                return;
            }

            m_scrollPos = EditorGUILayout.BeginScrollView(m_scrollPos);

            DrawHeader();
            EditorGUILayout.Space(4f);
            DrawPolygonSection();
            EditorGUILayout.Space(4f);
            DrawPrefabSlotsSection();
            EditorGUILayout.Space(4f);
            DrawPlacementSection();
            EditorGUILayout.Space(4f);
            DrawVisualsSection();
            EditorGUILayout.Space(8f);
            DrawActionButtons();
            EditorGUILayout.Space(4f);

            EditorGUILayout.EndScrollView();

            // 处理键盘事件
            HandleKeyboardEvents();
        }

        // --------------------------------------------------------
        // Header
        // --------------------------------------------------------

        private void DrawHeader()
        {
            GUIStyle headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField("Build Tools", headerStyle, GUILayout.Height(24f));

            // 激活 / 退出按钮
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = m_isActive ? new Color(0.5f, 0.9f, 0.5f) : new Color(0.7f, 0.7f, 0.7f);
            if (GUILayout.Button(m_isActive ? "● 编辑中（点击退出）" : "○ 已退出（点击进入）", GUILayout.Height(28f)))
            {
                m_isActive = !m_isActive;
                if (!m_isActive)
                {
                    Tools.current = Tool.Move; // 恢复 Unity 默认工具
                    m_currentMode = ToolMode.Disabled;
                }
                Repaint();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            string status;
            if (!m_isActive)
            {
                status = "⚫ 工具已退出 —— 点击上方按钮进入编辑";
            }
            else switch (m_currentMode)
            {
                case ToolMode.Add:
                    status = "🟢 添加模式（Q）—— 点击地面添加顶点";
                    break;
                case ToolMode.Edit:
                    status = "🟡 编辑模式（W）—— 拖拽坐标轴移动顶点";
                    break;
                default:
                    status = "⚪ 已暂停 —— 按 Q 添加 / 按 W 编辑";
                    break;
            }

            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

            // 多边形状态信息
            int pointCount = m_settings.polygonPoints.Count;
            int edgeCount = pointCount > 0 ? m_settings.isPolygonClosed ? pointCount : pointCount - 1 : 0;
            float perimeter = CalculatePerimeter();
            string info = $"顶点: {pointCount} | 边: {Mathf.Max(0, edgeCount)} | ";
            info += m_settings.isPolygonClosed ? "已闭合" : "未闭合";
            info += $" | 周长: {perimeter:F2}m";
            EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
        }

        // --------------------------------------------------------
        // Polygon Section
        // --------------------------------------------------------

        private void DrawPolygonSection()
        {
            m_polygonFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(m_polygonFoldout, "多边形");
            if (m_polygonFoldout)
            {
                EditorGUI.indentLevel++;

                // 顶点列表
                int deleteIndex = -1;
                for (int i = 0; i < m_settings.polygonPoints.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    Vector3 pt = m_settings.polygonPoints[i];
                    EditorGUILayout.LabelField($"顶点 {i}:", GUILayout.Width(50f));
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPt = EditorGUILayout.Vector3Field(GUIContent.none, pt);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo("BT: Edit Polygon Point");
                        m_settings.polygonPoints[i] = newPt;
                        EditorUtility.SetDirty(m_settings);
                    }
                    if (GUILayout.Button("×", GUILayout.Width(24f), GUILayout.Height(18f)))
                    {
                        deleteIndex = i;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (deleteIndex >= 0)
                {
                    RegisterSettingsUndo("BT: Delete Polygon Point");
                    m_settings.polygonPoints.RemoveAt(deleteIndex);
                    // 如果顶点数不足，自动打开多边形
                    if (m_settings.polygonPoints.Count < 3 && m_settings.isPolygonClosed)
                    {
                        m_settings.isPolygonClosed = false;
                    }
                    EditorUtility.SetDirty(m_settings);
                    Repaint();
                }

                if (m_settings.polygonPoints.Count == 0)
                {
                    EditorGUILayout.HelpBox("在场景视图中点击以添加多边形顶点。\nEnter = 闭合 | Esc = 撤销 | Delete = 删除最后一个", MessageType.Info);
                }

                EditorGUILayout.Space(4f);

                // 操作按钮
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(m_settings.polygonPoints.Count < 3);
                if (GUILayout.Button(m_settings.isPolygonClosed ? "打开多边形" : "闭合多边形"))
                {
                    RegisterSettingsUndo("BT: Toggle Polygon Closed");
                    m_settings.isPolygonClosed = !m_settings.isPolygonClosed;
                    EditorUtility.SetDirty(m_settings);
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(m_settings.polygonPoints.Count == 0);
                if (GUILayout.Button("清空顶点"))
                {
                    if (EditorUtility.DisplayDialog("确认清空", "确定要删除所有多边形顶点吗？", "确定", "取消"))
                    {
                        RegisterSettingsUndo("BT: Clear Polygon");
                        m_settings.polygonPoints.Clear();
                        m_settings.isPolygonClosed = false;
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                if (GUILayout.Button("撤销上一个"))
                {
                    if (m_settings.polygonPoints.Count > 0)
                    {
                        RegisterSettingsUndo("BT: Remove Last Point");
                        m_settings.polygonPoints.RemoveAt(m_settings.polygonPoints.Count - 1);
                        if (m_settings.polygonPoints.Count < 3 && m_settings.isPolygonClosed)
                            m_settings.isPolygonClosed = false;
                        EditorUtility.SetDirty(m_settings);
                    }
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // --------------------------------------------------------
        // Prefab Slots Section
        // --------------------------------------------------------

        private void DrawPrefabSlotsSection()
        {
            m_prefabsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(m_prefabsFoldout, "建筑 Prefabs");
            if (m_prefabsFoldout)
            {
                EditorGUI.indentLevel++;

                // ── 拖拽批量添加 Prefab ──
                DrawPrefabDropZone();

                int deleteSlot = -1;

                for (int i = 0; i < m_settings.prefabSlots.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();

                    var slot = m_settings.prefabSlots[i];

                    // Prefab 对象字段
                    EditorGUI.BeginChangeCheck();
                    GameObject newPrefab = (GameObject)EditorGUILayout.ObjectField(
                        $"槽位 {i}",
                        slot.prefab,
                        typeof(GameObject),
                        false
                    );
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo("BT: Change Prefab");
                        slot.prefab = newPrefab;
                        m_settings.prefabSlots[i] = slot;
                        EditorUtility.SetDirty(m_settings);
                    }

                    // 权重滑块
                    EditorGUI.BeginChangeCheck();
                    float newWeight = EditorGUILayout.Slider(slot.weight, 0f, 1f, GUILayout.Width(80f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo("BT: Change Prefab Weight");
                        slot.weight = newWeight;
                        m_settings.prefabSlots[i] = slot;
                        EditorUtility.SetDirty(m_settings);
                    }

                    // 启用/禁用切换
                    EditorGUI.BeginChangeCheck();
                    bool newEnabled = EditorGUILayout.Toggle(slot.enabled, GUILayout.Width(20f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo("BT: Toggle Prefab Slot");
                        slot.enabled = newEnabled;
                        m_settings.prefabSlots[i] = slot;
                        EditorUtility.SetDirty(m_settings);
                    }

                    // 删除按钮
                    if (GUILayout.Button("×", GUILayout.Width(24f), GUILayout.Height(18f)))
                    {
                        deleteSlot = i;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                if (deleteSlot >= 0)
                {
                    RegisterSettingsUndo("BT: Delete Prefab Slot");
                    m_settings.prefabSlots.RemoveAt(deleteSlot);
                    EditorUtility.SetDirty(m_settings);
                }

                if (m_settings.prefabSlots.Count == 0)
                {
                    EditorGUILayout.HelpBox("添加建筑 Prefab 到槽位中。权重越高，被选中的概率越大。", MessageType.Info);
                }

                EditorGUILayout.Space(2f);

                if (GUILayout.Button("+ 添加 Prefab 槽位"))
                {
                    RegisterSettingsUndo("BT: Add Prefab Slot");
                    m_settings.prefabSlots.Add(new PrefabSlot(null, 1f, true));
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // --------------------------------------------------------
        // Prefab Drop Zone（拖拽批量添加）
        // --------------------------------------------------------

        private void DrawPrefabDropZone()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0f, 38f, GUILayout.ExpandWidth(true));
            Event evt = Event.current;

            // 统计拖拽中的有效 Prefab 数量
            int prefabCount = 0;
            bool isDraggingOver = dropArea.Contains(evt.mousePosition);

            if (isDraggingOver && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                foreach (var obj in DragAndDrop.objectReferences)
                {
                    if (obj is GameObject)
                        prefabCount++;
                }
            }

            // 处理拖拽事件
            if (isDraggingOver)
            {
                switch (evt.type)
                {
                    case EventType.DragUpdated:
                        DragAndDrop.visualMode = prefabCount > 0
                            ? DragAndDropVisualMode.Copy
                            : DragAndDropVisualMode.Rejected;
                        evt.Use();
                        break;

                    case EventType.DragPerform:
                        if (prefabCount > 0)
                        {
                            DragAndDrop.AcceptDrag();
                            RegisterSettingsUndo("BT: Batch Add Prefab Slots");
                            foreach (var obj in DragAndDrop.objectReferences)
                            {
                                if (obj is GameObject prefab)
                                {
                                    m_settings.prefabSlots.Add(new PrefabSlot(prefab, 1f, true));
                                }
                            }
                            EditorUtility.SetDirty(m_settings);
                            Repaint();
                        }
                        evt.Use();
                        break;
                }
            }

            // 绘制区域背景 + 边框
            Color bgColor = isDraggingOver && prefabCount > 0
                ? new Color(0.3f, 0.7f, 0.3f, 0.35f)
                : new Color(0.45f, 0.45f, 0.45f, 0.2f);

            Color restoreColor = GUI.color;
            GUI.color = bgColor;
            GUI.Box(dropArea, "", GUI.skin.box);
            GUI.color = restoreColor;

            // 文字提示
            string hintText;
            if (isDraggingOver && prefabCount > 0)
                hintText = $"释放鼠标添加 {prefabCount} 个 Prefab 槽位";
            else
                hintText = "📁 从 Project 视图拖拽 Prefab 到此处，自动批量添加槽位";

            GUIStyle labelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = isDraggingOver && prefabCount > 0
                    ? new Color(0.2f, 0.7f, 0.2f)
                    : new Color(0.6f, 0.6f, 0.6f) }
            };
            GUI.Label(dropArea, hintText, labelStyle);

            EditorGUILayout.Space(2f);
        }

        // --------------------------------------------------------
        // Placement Settings Section
        // --------------------------------------------------------

        private void DrawPlacementSection()
        {
            m_placementFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(m_placementFoldout, "放置参数");
            if (m_placementFoldout)
            {
                EditorGUI.indentLevel++;

                PlacementSettings ps = m_settings.placement;

                // 排列模式（中文显示）
                string[] modeNames = { "随机排列", "等距排列", "顺序排列" };
                EditorGUI.BeginChangeCheck();
                int newModeIndex = EditorGUILayout.Popup("排列模式", (int)ps.placementMode, modeNames);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.placementMode = (PlacementMode)newModeIndex;
                    EditorUtility.SetDirty(m_settings);
                }

                // 等距 / 顺序模式：间距
                if (ps.placementMode == PlacementMode.Equidistant || ps.placementMode == PlacementMode.Sequential)
                {
                    EditorGUI.BeginChangeCheck();
                    float newEqSpacing = EditorGUILayout.FloatField("间距", ps.spacing);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.spacing = Mathf.Max(0f, newEqSpacing);
                        EditorUtility.SetDirty(m_settings);
                    }
                    if (ps.spacing < 0.001f)
                    {
                        string hint = ps.placementMode == PlacementMode.Sequential
                            ? "间距为 0 ：模型边缘紧挨（A.max 贴合 B.min）"
                            : "间距为 0 ：自动按「建筑数量」等分周长";
                        EditorGUILayout.HelpBox(hint, MessageType.Info);
                    }
                    else if (ps.placementMode == PlacementMode.Sequential)
                    {
                        EditorGUILayout.HelpBox("模型边缘到边缘的空隙", MessageType.Info);
                    }
                }

                // 建筑数量
                EditorGUI.BeginChangeCheck();
                int newCount = EditorGUILayout.IntField("建筑数量", ps.buildingCount);
                if (EditorGUI.EndChangeCheck() && newCount >= 1)
                {
                    RegisterSettingsUndo();
                    ps.buildingCount = newCount;
                    EditorUtility.SetDirty(m_settings);
                }

                // 随机种子（仅随机模式）
                if (ps.placementMode == PlacementMode.Random)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginChangeCheck();
                    int newSeed = EditorGUILayout.IntField("随机种子", ps.randomSeed);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.randomSeed = newSeed;
                        EditorUtility.SetDirty(m_settings);
                    }
                    if (GUILayout.Button("随机", GUILayout.Width(50f)))
                    {
                        RegisterSettingsUndo();
                        ps.randomSeed = UnityEngine.Random.Range(0, 999999);
                        EditorUtility.SetDirty(m_settings);
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // 边缘偏移
                EditorGUI.BeginChangeCheck();
                float newOffset = EditorGUILayout.FloatField("边缘偏移", ps.edgeOffset);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.edgeOffset = newOffset;
                    EditorUtility.SetDirty(m_settings);
                }

                // 随机边缘偏移
                EditorGUI.BeginChangeCheck();
                bool newRandOffset = EditorGUILayout.Toggle("随机边缘偏移", ps.randomizeEdgeOffset);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.randomizeEdgeOffset = newRandOffset;
                    EditorUtility.SetDirty(m_settings);
                }

                if (ps.randomizeEdgeOffset)
                {
                    EditorGUI.BeginChangeCheck();
                    float newRandRange = EditorGUILayout.FloatField("随机偏移范围 (±)", ps.randomEdgeOffsetRange);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.randomEdgeOffsetRange = Mathf.Max(0f, newRandRange);
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                // 最小间距
                EditorGUI.BeginChangeCheck();
                float newSpacing = EditorGUILayout.FloatField("最小间距", ps.minSpacing);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.minSpacing = Mathf.Max(0f, newSpacing);
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUILayout.Space(2f);

                // 朝向模式
                EditorGUI.BeginChangeCheck();
                string[] orientationNames = { "面向中心", "沿边缘", "世界方向", "随机" };
                int newOrientIndex = EditorGUILayout.Popup("朝向模式", (int)ps.orientationMode, orientationNames);
                OrientationMode newMode = (OrientationMode)newOrientIndex;
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.orientationMode = newMode;
                    EditorUtility.SetDirty(m_settings);
                }

                // 世界方向（仅当 WorldDirection 模式时显示）
                if (ps.orientationMode == OrientationMode.WorldDirection)
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 newDir = EditorGUILayout.Vector3Field("世界方向", ps.worldDirection);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.worldDirection = newDir;
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                // 沿边缘旋转偏移（仅当 AlongEdge 模式时显示）
                if (ps.orientationMode == OrientationMode.AlongEdge)
                {
                    EditorGUI.BeginChangeCheck();
                    float newEdgeRot = EditorGUILayout.Slider("边缘旋转偏移", ps.edgeRotationOffset, -180f, 180f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.edgeRotationOffset = newEdgeRot;
                        EditorUtility.SetDirty(m_settings);
                    }

                    EditorGUILayout.LabelField("边对齐");
                    EditorGUILayout.BeginHorizontal();
                    string[] alignNames = { "居中", "左对齐", "右对齐" };
                    for (int a = 0; a < alignNames.Length; a++)
                    {
                        bool isActive = ((int)ps.edgeAlignment == a);
                        GUI.backgroundColor = isActive ? new Color(0.4f, 0.7f, 1f) : Color.white;
                        if (GUILayout.Button(alignNames[a], GUILayout.Height(24f)))
                        {
                            RegisterSettingsUndo();
                            ps.edgeAlignment = (EdgeAlignment)a;
                            EditorUtility.SetDirty(m_settings);
                        }
                    }
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();
                }

                // 随机旋转
                EditorGUI.BeginChangeCheck();
                bool newRandRot = EditorGUILayout.Toggle("随机旋转", ps.randomizeRotation);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.randomizeRotation = newRandRot;
                    EditorUtility.SetDirty(m_settings);
                }

                if (ps.randomizeRotation)
                {
                    EditorGUI.BeginChangeCheck();
                    float newRange = EditorGUILayout.Slider("旋转范围 (±°)", ps.randomRotationRange, 0f, 180f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.randomRotationRange = newRange;
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                EditorGUILayout.Space(2f);

                // 覆盖图层
                EditorGUI.BeginChangeCheck();
                bool newOverwrite = EditorGUILayout.Toggle("覆盖图层", ps.overwriteLayer);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.overwriteLayer = newOverwrite;
                    EditorUtility.SetDirty(m_settings);
                }

                if (ps.overwriteLayer)
                {
                    EditorGUI.BeginChangeCheck();
                    int newLayer = EditorGUILayout.LayerField("目标图层", ps.placeLayer);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.placeLayer = newLayer;
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                // 分组
                EditorGUI.BeginChangeCheck();
                bool newGroup = EditorGUILayout.Toggle("分组建筑", ps.groupPrefabs);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    ps.groupPrefabs = newGroup;
                    EditorUtility.SetDirty(m_settings);
                }

                if (ps.groupPrefabs)
                {
                    EditorGUI.BeginChangeCheck();
                    string newName = EditorGUILayout.TextField("分组名称", ps.groupName);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        ps.groupName = newName;
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // --------------------------------------------------------
        // Visuals Section
        // --------------------------------------------------------

        private void DrawVisualsSection()
        {
            m_visualsFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(m_visualsFoldout, "可视化设置");
            if (m_visualsFoldout)
            {
                EditorGUI.indentLevel++;

                EditorGUI.BeginChangeCheck();
                Color newOutline = EditorGUILayout.ColorField("轮廓颜色", m_settings.polygonOutlineColor);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.polygonOutlineColor = newOutline;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.BeginChangeCheck();
                Color newFill = EditorGUILayout.ColorField("填充颜色", m_settings.polygonFillColor);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.polygonFillColor = newFill;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.BeginChangeCheck();
                Color newHandle = EditorGUILayout.ColorField("手柄颜色", m_settings.pointHandleColor);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.pointHandleColor = newHandle;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.BeginChangeCheck();
                float newSize = EditorGUILayout.Slider("手柄大小", m_settings.pointHandleSize, 0.1f, 2f);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.pointHandleSize = newSize;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.BeginChangeCheck();
                Color newPreview = EditorGUILayout.ColorField("预览线颜色", m_settings.previewLineColor);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.previewLineColor = newPreview;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUILayout.Space(2f);

                // 圆角转折
                EditorGUI.BeginChangeCheck();
                bool newRounded = EditorGUILayout.Toggle("圆角转折", m_settings.useRoundedCorners);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.useRoundedCorners = newRounded;
                    EditorUtility.SetDirty(m_settings);
                }

                if (m_settings.useRoundedCorners)
                {
                    EditorGUI.BeginChangeCheck();
                    float newRadius = EditorGUILayout.FloatField("圆角半径", m_settings.cornerRadius);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RegisterSettingsUndo();
                        m_settings.cornerRadius = Mathf.Max(0f, newRadius);
                        EditorUtility.SetDirty(m_settings);
                    }
                }

                EditorGUI.BeginChangeCheck();
                LayerMask newMask = EditorGUILayout.MaskField("射线检测图层", (int)m_settings.paintLayers,
                    UnityEditorInternal.InternalEditorUtility.layers);
                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo();
                    m_settings.paintLayers = newMask;
                    EditorUtility.SetDirty(m_settings);
                }

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // --------------------------------------------------------
        // Action Buttons
        // --------------------------------------------------------

        private void DrawActionButtons()
        {
            // 实时预览开关
            EditorGUI.BeginChangeCheck();
            m_livePreview = EditorGUILayout.Toggle("实时预览", m_livePreview);
            if (EditorGUI.EndChangeCheck())
            {
                if (m_livePreview && CanGenerate())
                {
                    GenerateBuildings(); // 开启时立即生成一次
                }
            }

            // 生成按钮（实时预览开启时隐藏）
            if (!m_livePreview)
            {
                EditorGUI.BeginDisabledGroup(!CanGenerate());
                GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
                if (GUILayout.Button("🎲 生成建筑", GUILayout.Height(36f)))
                {
                    GenerateBuildings();
                }
                GUI.backgroundColor = Color.white;
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.HelpBox("实时预览已开启，调节参数时自动重新生成", MessageType.Info);
            }

            EditorGUILayout.Space(4f);

            // 清除按钮
            EditorGUI.BeginDisabledGroup(m_generatedBuildings.Count == 0);
            GUI.backgroundColor = new Color(0.9f, 0.4f, 0.4f);
            if (GUILayout.Button("🗑 清除生成的建筑", GUILayout.Height(28f)))
            {
                ClearGeneratedBuildings();
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();

            if (m_generatedBuildings.Count > 0)
            {
                // 过滤掉已销毁的引用
                m_generatedBuildings.RemoveAll(g => g == null);
                EditorGUILayout.LabelField($"当前已生成 {m_generatedBuildings.Count} 个建筑", EditorStyles.miniLabel);
            }
        }

        private bool CanGenerate()
        {
            if (m_settings == null) return false;
            if (m_settings.polygonPoints.Count < 2) return false;

            // 检查是否有可用的 Prefab 槽位
            bool hasPrefab = false;
            foreach (var slot in m_settings.prefabSlots)
            {
                if (slot.enabled && slot.prefab != null)
                {
                    hasPrefab = true;
                    break;
                }
            }
            return hasPrefab;
        }

        // ============================================================
        // Generate & Clear
        // ============================================================

        private void GenerateBuildings()
        {
            if (!CanGenerate()) return;

            // 清除之前的生成结果
            ClearGeneratedBuildingsSilent();

            System.Random rng = new System.Random(m_settings.placement.randomSeed);

            // 生成放置位置（传入 Prefab 槽位以支持边缘到边缘间距计算）
            List<PlacementResult> placements = BuildToolsPlacement.GeneratePlacements(
                m_settings.polygonPoints,
                m_settings.isPolygonClosed,
                m_settings.placement,
                rng,
                m_settings.prefabSlots
            );

            if (placements.Count == 0)
            {
                EditorUtility.DisplayDialog("Build Tools", "未能生成任何建筑放置位置。\n请检查多边形是否有效或参数是否合理。", "确定");
                return;
            }

            // 实例化 Prefab
            m_generatedBuildings = BuildToolsPlacement.InstantiatePlacements(
                placements,
                m_settings.prefabSlots,
                m_sceneSettings,
                m_settings.placement.overwriteLayer,
                m_settings.placement.placeLayer,
                m_settings.placement.groupPrefabs,
                m_settings.placement.groupName,
                rng
            );

            // 更新场景设置中的引用
            if (m_sceneSettings != null)
            {
                Undo.RecordObject(m_sceneSettings, "BT: Track Generated Buildings");
                m_sceneSettings.generatedBuildings = m_generatedBuildings;
                EditorUtility.SetDirty(m_sceneSettings);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log($"BuildTools: 成功生成 {m_generatedBuildings.Count} 个建筑（请求 {m_settings.placement.buildingCount} 个）。");

            Repaint();
        }

        private void ClearGeneratedBuildings()
        {
            if (m_generatedBuildings.Count == 0) return;

            if (!EditorUtility.DisplayDialog("确认清除",
                $"确定要删除所有 {m_generatedBuildings.Count} 个生成的建筑吗？\n此操作可以撤销 (Ctrl+Z)。",
                "确定", "取消"))
            {
                return;
            }

            ClearGeneratedBuildingsSilent();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Repaint();
        }

        private void ClearGeneratedBuildingsSilent()
        {
            // 移除已被销毁的引用
            m_generatedBuildings.RemoveAll(g => g == null);

            foreach (var go in m_generatedBuildings)
            {
                if (go != null)
                {
                    Undo.DestroyObjectImmediate(go);
                }
            }

            m_generatedBuildings.Clear();

            if (m_sceneSettings != null)
            {
                Undo.RecordObject(m_sceneSettings, "BT: Clear Generated Buildings");
                m_sceneSettings.generatedBuildings = m_generatedBuildings;
                EditorUtility.SetDirty(m_sceneSettings);
            }
        }

        // ============================================================
        // Scene View Interaction
        // ============================================================

        private void OnSceneGUI(SceneView sceneView)
        {
            if (m_settings == null) return;
            if (m_currentMode == ToolMode.Disabled) return;

            // ── 编辑模式：PositionHandle 必须在所有事件类型中被调用 ──
            // 这样它才能在 Layout 注册控件、MouseDown 捕获焦点、
            // MouseDrag 更新位置、Repaint 绘制坐标轴。
            if (m_currentMode == ToolMode.Edit)
            {
                OnSceneGUIEditMode(sceneView);
                return;
            }

            // ── 添加模式：标准事件分发 ──
            int controlID = GUIUtility.GetControlID(s_BuildToolsHash, FocusType.Passive);
            Event e = Event.current;
            EventType eventType = e.GetTypeForControl(controlID);

            switch (eventType)
            {
                case EventType.MouseDown:
                    HandleMouseDown(e, controlID);
                    break;

                case EventType.MouseMove:
                    HandleMouseMove(e);
                    break;

                case EventType.Repaint:
                    HandleRepaintAddMode(e);
                    break;

                case EventType.Layout:
                    HandleUtility.AddDefaultControl(controlID);
                    break;
            }

            // 键盘事件（场景视图有独立的事件流，需在此处理）
            HandleKeyboardEvents();
        }

        /// <summary>
        /// 编辑模式下的场景视图处理。
        /// PositionHandle 在所有事件类型中调用，确保完整的交互生命周期。
        /// </summary>
        private void OnSceneGUIEditMode(SceneView sceneView)
        {
            // PositionHandle 需要在 Layout / MouseDown / MouseDrag / Repaint 各阶段都被调用
            DrawEditModeHandlesWithEvents();

            // 仅 Repaint 阶段绘制多边形视觉元素
            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                DrawPolygonEdges();
                DrawPolygonFill();
                DrawSceneOverlay();
            }

            // 允许场景导航
            if (e.type == EventType.Layout)
            {
                int controlID = GUIUtility.GetControlID(s_BuildToolsHash, FocusType.Passive);
                HandleUtility.AddDefaultControl(controlID);
            }

            HandleKeyboardEvents();
        }

        // --------------------------------------------------------
        // Mouse Handlers
        // --------------------------------------------------------

        private void HandleMouseDown(Event e, int controlID)
        {
            // 仅在添加模式下响应鼠标点击
            if (m_currentMode != ToolMode.Add)
                return;

            // 仅处理左键，忽略 Alt（Alt+Click 是导航）
            if (e.button != 0 || e.alt || GUIUtility.hotControl != 0)
                return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            Vector3 worldPoint;
            bool gotPoint = false;

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, Mathf.Infinity, m_settings.paintLayers))
            {
                worldPoint = hit.point;
                gotPoint = true;
            }
            else if (Mathf.Abs(ray.direction.y) > 0.0001f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t > 0f)
                {
                    worldPoint = ray.origin + ray.direction * t;
                    gotPoint = true;
                }
                else
                {
                    return;
                }
            }
            else
            {
                return;
            }

            // ── Ctrl + 点击：删除最近的顶点 ──
            if (e.control && m_settings.polygonPoints.Count > 0)
            {
                int deleteIndex = FindClosestVertexIndex(worldPoint);
                if (deleteIndex >= 0)
                {
                    RegisterSettingsUndo("BT: Delete Polygon Point");
                    m_settings.polygonPoints.RemoveAt(deleteIndex);
                    if (m_settings.polygonPoints.Count < 3 && m_settings.isPolygonClosed)
                        m_settings.isPolygonClosed = false;
                    EditorUtility.SetDirty(m_settings);
                    e.Use();
                    Repaint();
                    return;
                }
            }

            // ── Shift + 点击：在已有线段上插入新顶点 ──
            if (e.shift && m_settings.polygonPoints.Count >= 2)
            {
                int insertIndex = FindClosestEdgeInsertIndex(worldPoint);
                if (insertIndex >= 0)
                {
                    RegisterSettingsUndo("BT: Insert Polygon Point");
                    // 将新顶点的 Y 投影到边的 Y 坐标
                    Vector3 snappedPoint = SnapPointToEdgeY(worldPoint, insertIndex);
                    m_settings.polygonPoints.Insert(insertIndex, snappedPoint);
                    EditorUtility.SetDirty(m_settings);
                    e.Use();
                    Repaint();
                    return;
                }
            }

            // ── 普通点击：在末尾添加顶点 ──
            RegisterSettingsUndo("BT: Add Polygon Point");
            m_settings.polygonPoints.Add(worldPoint);
            EditorUtility.SetDirty(m_settings);
            e.Use();
            Repaint();
        }

        private void HandleMouseMove(Event e)
        {
            // 计算鼠标在地面上的投影位置（用于预览线）
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, Mathf.Infinity, m_settings.paintLayers))
            {
                m_lastMouseWorldPos = hit.point;
            }
            else if (Mathf.Abs(ray.direction.y) > 0.0001f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t > 0f)
                    m_lastMouseWorldPos = ray.origin + ray.direction * t;
            }

            // ── Ctrl 悬停检测：找到鼠标最近的顶点 ──
            m_nearestVertexIndex = -1;
            if (e.control && m_settings.polygonPoints.Count > 0)
            {
                m_nearestVertexIndex = FindClosestVertexIndex(m_lastMouseWorldPos);
            }

            // ── Shift 悬停检测：找到鼠标最近的边，用于视觉提示 ──
            m_nearestEdgeInsertIndex = -1;
            if (e.shift && m_settings.polygonPoints.Count >= 2)
            {
                m_nearestEdgeInsertIndex = FindClosestEdgeInsertIndex(m_lastMouseWorldPos);
                if (m_nearestEdgeInsertIndex >= 0)
                {
                    // 计算边上最近点的准确位置
                    int n = m_settings.polygonPoints.Count;
                    int prev = (m_nearestEdgeInsertIndex - 1 + n) % n;
                    if (m_nearestEdgeInsertIndex == 0 && !m_settings.isPolygonClosed)
                        prev = 0; // 开放多边形的第一条边
                    m_nearestEdgePoint = ClosestPointOnSegmentXZ(
                        m_lastMouseWorldPos,
                        m_settings.polygonPoints[prev],
                        m_settings.polygonPoints[m_nearestEdgeInsertIndex % n]
                    );
                }
            }

            if (m_settings.polygonPoints.Count > 0 || m_nearestEdgeInsertIndex >= 0 || m_nearestVertexIndex >= 0)
                SceneView.RepaintAll();
        }

        // --------------------------------------------------------
        // Repaint Handler - Draw Handles
        // --------------------------------------------------------

        private void HandleRepaintAddMode(Event e)
        {
            DrawPolygonEdges();
            DrawShiftHoverHighlight();
            DrawPreviewLine();
            DrawPolygonFill();
            DrawVertexHandles();
            DrawSceneOverlay();
        }

        private void DrawPolygonEdges()
        {
            if (m_settings.useRoundedCorners && m_settings.cornerRadius > 0.001f)
            {
                DrawRoundedPolygonEdges();
                return;
            }

            int n = m_settings.polygonPoints.Count;
            if (n < 2) return;

            Handles.color = m_settings.polygonOutlineColor;

            // 边线（连续顶点之间）
            for (int i = 0; i < n - 1; i++)
            {
                Handles.DrawAAPolyLine(4f, m_settings.polygonPoints[i], m_settings.polygonPoints[i + 1]);
            }

            // 闭合边（最后一个顶点到第一个顶点）
            if (m_settings.isPolygonClosed && n >= 2)
            {
                Handles.DrawAAPolyLine(4f, m_settings.polygonPoints[n - 1], m_settings.polygonPoints[0]);
            }
        }

        /// <summary>
        /// 绘制带圆角转折的多边形边线。
        /// 在每个顶点处用圆弧替代尖角，半径受相邻边长约束。
        /// </summary>
        private void DrawRoundedPolygonEdges()
        {
            int n = m_settings.polygonPoints.Count;
            if (n < 2) return;

            List<Vector3> pts = m_settings.polygonPoints;
            bool closed = m_settings.isPolygonClosed;
            float r = m_settings.cornerRadius;

            Handles.color = m_settings.polygonOutlineColor;

            // 为每个顶点预计算圆角数据：切线点 T_before / T_after、圆弧圆心、半径
            var corners = new (bool valid, Vector3 T_before, Vector3 T_after, Vector3 arcCenter, float arcR)[n];
            for (int i = 0; i < n; i++)
            {
                if (closed || (i > 0 && i < n - 1)) // 仅内部顶点/闭合多边形的所有顶点
                {
                    int prev = (i - 1 + n) % n;
                    int next = (i + 1) % n;

                    Vector3 dIn = XZNormalized(pts[i] - pts[prev]);   // 入边方向（指向顶点）
                    Vector3 dOut = XZNormalized(pts[next] - pts[i]); // 出边方向（离开顶点）

                    // 限制半径不超过相邻边的一半
                    float maxR = Mathf.Min(
                        XZDistance(pts[prev], pts[i]) * 0.5f,
                        XZDistance(pts[i], pts[next]) * 0.5f);
                    float clampedR = Mathf.Min(r, maxR);

                    if (clampedR > 0.001f)
                    {
                        // 根据转弯方向确定法向（左转→弧在左侧，右转→弧在右侧）
                        // 2D cross: dIn.x * dOut.z - dIn.z * dOut.x
                        float cross = dIn.x * dOut.z - dIn.z * dOut.x;

                        Vector3 nIn, nOut;
                        if (cross > 0.0001f) // 左转 → 逆时针法向
                        {
                            nIn  = new Vector3(-dIn.z,  0f, dIn.x);
                            nOut = new Vector3(-dOut.z, 0f, dOut.x);
                        }
                        else if (cross < -0.0001f) // 右转 → 顺时针法向
                        {
                            nIn  = new Vector3(dIn.z,  0f, -dIn.x);
                            nOut = new Vector3(dOut.z, 0f, -dOut.x);
                        }
                        else // 共线，无需圆角
                        {
                            continue;
                        }

                        // 偏移线交点 = 圆弧圆心
                        // 入边偏移线: O = pts[i] - dIn * clampedR + nIn * clampedR + dIn * t  ...
                        // 实际: O = pts[i] + dIn * t_in + nIn * clampedR
                        //        O = pts[i] + dOut * t_out + nOut * clampedR
                        // 其中 t_in, t_out 是未知参数
                        Vector3 P_in = pts[i] + nIn * clampedR;
                        Vector3 P_out = pts[i] + nOut * clampedR;

                        // 求交点: P_in + dIn * t = P_out + dOut * s
                        if (TryLineIntersectXZ(P_in, dIn, P_out, dOut, out Vector3 arcCenter, out float _, out float _))
                        {
                            Vector3 T_before = archCenter - nIn * clampedR;
                            Vector3 T_after = arcCenter - nOut * clampedR;

                            corners[i] = (true, T_before, T_after, arcCenter, clampedR);
                        }
                    }
                }
            }

            // 绘制线段 + 圆弧
            int edgeCount = closed ? n : n - 1;
            for (int i = 0; i < edgeCount; i++)
            {
                int curr = i;
                int next = (i + 1) % n;

                // 从上一顶点弧的终点（若无弧则为顶点本身）
                Vector3 segStart = corners[curr].valid ? corners[curr].T_after : pts[curr];
                // 到下一顶点弧的起点（若无弧则为顶点本身）
                Vector3 segEnd = corners[next].valid ? corners[next].T_before : pts[next];

                // 绘制直线段（确保不反向）
                if (Vector3.Dot(XZNormalized(segEnd - segStart), XZNormalized(pts[next] - pts[curr])) >= 0f
                    && XZSqrDistance(segStart, segEnd) > 0.0001f)
                {
                    Handles.DrawAAPolyLine(4f, segStart, segEnd);
                }

                // 在 next 顶点处绘制圆弧
                if (corners[next].valid)
                {
                    var c = corners[next];
                    DrawWireArcXZ(c.arcCenter, c.arcR, c.T_before, c.T_after);
                }
            }
        }

        // ============================================================
        // 圆角辅助方法
        // ============================================================

        /// <summary>XZ 平面上点到点的距离</summary>
        private static float XZDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float XZSqrDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        /// <summary>XZ 平面上的单位方向（忽略 Y）</summary>
        private static Vector3 XZNormalized(Vector3 v)
        {
            Vector3 flat = new Vector3(v.x, 0f, v.z);
            float mag = flat.magnitude;
            return mag > 0.0001f ? flat / mag : Vector3.forward;
        }

        /// <summary>XZ 平面上旋转 90° 的垂直向量</summary>
        private static Vector3 XZPerp(Vector3 v)
        {
            return new Vector3(v.z, 0f, -v.x);
        }

        /// <summary>
        /// 求 XZ 平面上两条线的交点：P1 + s*D1 = P2 + t*D2
        /// </summary>
        private static bool TryLineIntersectXZ(Vector3 P1, Vector3 D1, Vector3 P2, Vector3 D2,
            out Vector3 intersection, out float s, out float t)
        {
            // 2D cross product: D1.x * D2.z - D1.z * D2.x
            float cross = D1.x * D2.z - D1.z * D2.x;
            if (Mathf.Abs(cross) < 0.0001f)
            {
                intersection = Vector3.zero;
                s = t = 0f;
                return false;
            }

            Vector3 delta = P2 - P1;
            s = (delta.x * D2.z - delta.z * D2.x) / cross;
            t = (delta.x * D1.z - delta.z * D1.x) / cross;

            intersection = P1 + s * D1;
            // Y 取两个起点的平均值
            intersection.y = (P1.y + P2.y) * 0.5f;
            return true;
        }

        /// <summary>在 XZ 平面上绘制圆弧（Scene View handles）</summary>
        private static void DrawWireArcXZ(Vector3 center, float radius, Vector3 fromPoint, Vector3 toPoint)
        {
            Vector3 fromDir = XZNormalized(fromPoint - center);
            Vector3 toDir = XZNormalized(toPoint - center);
            float angle = Vector3.SignedAngle(fromDir, toDir, Vector3.up);

            if (Mathf.Abs(angle) < 0.01f) return;

            Handles.DrawWireArc(center, Vector3.up, fromDir, angle, radius);
        }

        private void DrawPreviewLine()
        {
            // 仅在添加模式下显示预览线
            if (m_currentMode != ToolMode.Add) return;

            int n = m_settings.polygonPoints.Count;
            if (n == 0 || m_settings.isPolygonClosed) return;

            // Shift 悬停时隐藏普通预览线，用边高亮替代
            if (m_nearestEdgeInsertIndex >= 0) return;

            // 从最后一个顶点到当前鼠标位置的虚线预览
            Vector3 lastPoint = m_settings.polygonPoints[n - 1];
            Handles.color = m_settings.previewLineColor;

            // 使用 Handles.DrawDottedLine 绘制虚线
            Handles.DrawDottedLine(lastPoint, m_lastMouseWorldPos, 4f);
        }

        /// <summary>
        /// Shift 悬停时高亮最近的边 + 显示插入点预览
        /// </summary>
        private void DrawShiftHoverHighlight()
        {
            if (m_nearestEdgeInsertIndex < 0) return;

            int n = m_settings.polygonPoints.Count;
            int prev = m_nearestEdgeInsertIndex - 1;
            if (prev < 0) prev = n - 1;
            int next = m_nearestEdgeInsertIndex % n;

            Vector3 edgeStart = m_settings.polygonPoints[prev];
            Vector3 edgeEnd = m_settings.polygonPoints[next];

            // 高亮边线
            Handles.color = new Color(0f, 1f, 0.5f, 0.8f);
            Handles.DrawAAPolyLine(6f, edgeStart, edgeEnd);

            // 插入点位置预览
            float handleSize = HandleUtility.GetHandleSize(m_nearestEdgePoint) * m_settings.pointHandleSize * 0.7f;
            Handles.color = new Color(0f, 1f, 0.5f, 0.9f);
            Handles.DrawSolidDisc(m_nearestEdgePoint, Vector3.up, handleSize);

            // 标签
            Handles.Label(
                m_nearestEdgePoint + Vector3.up * handleSize * 2f,
                "Shift+Click 插入",
                EditorStyles.miniLabel
            );
        }

        private void DrawPolygonFill()
        {
            int n = m_settings.polygonPoints.Count;
            if (n < 3 || !m_settings.isPolygonClosed) return;

            // 初始化 Zebra 材质（只做一次）
            if (s_zebraMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/BuildTools/ZebraFill");
                if (shader != null)
                {
                    s_zebraMaterial = new Material(shader);
                    s_zebraMaterial.hideFlags = HideFlags.HideAndDontSave;
                }
                else
                {
                    // 回退：使用 Internal-Colored（纯色填充）
                    Shader fallback = Shader.Find("Hidden/Internal-Colored");
                    if (fallback != null)
                        s_zebraMaterial = new Material(fallback);
                    else
                        return;
                }
            }

            // 设置斑马纹参数
            s_zebraMaterial.SetColor("_Color1", m_settings.polygonFillColor);
            Color color2 = m_settings.polygonFillColor;
            color2.a *= 0.3f; // 第二种颜色更透明，形成斑马纹对比
            s_zebraMaterial.SetColor("_Color2", color2);
            s_zebraMaterial.SetFloat("_StripeWidth", 8f);

            // 构建三角形 Mesh（扇形三角剖分）
            if (s_fillMesh == null)
            {
                s_fillMesh = new Mesh();
                s_fillMesh.hideFlags = HideFlags.HideAndDontSave;
            }

            int triCount = n;
            Vector3[] vertices = new Vector3[n + 1];
            int[] triangles = new int[triCount * 3];

            // 质心（世界坐标）
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < n; i++)
                centroid += m_settings.polygonPoints[i];
            centroid /= n;
            vertices[0] = centroid;

            for (int i = 0; i < n; i++)
            {
                vertices[i + 1] = m_settings.polygonPoints[i];
                int next = (i + 1) % n;
                triangles[i * 3 + 0] = 0;         // 质心
                triangles[i * 3 + 1] = i + 1;     // 当前顶点
                triangles[i * 3 + 2] = next + 1;  // 下一个顶点
            }

            s_fillMesh.Clear();
            s_fillMesh.vertices = vertices;
            s_fillMesh.triangles = triangles;

            // 使用斑马纹材质绘制
            s_zebraMaterial.SetPass(0);
            Graphics.DrawMeshNow(s_fillMesh, Matrix4x4.identity);
        }

        private void DrawVertexHandles()
        {
            // 仅在添加模式下在此绘制（编辑模式由 OnSceneGUIEditMode 直接调用）
            if (m_currentMode == ToolMode.Add)
                DrawAddModeVertices();
        }

        /// <summary>
        /// 编辑模式：在所有事件类型中调用 PositionHandle，确保完整的交互生命周期。
        /// Layout → 注册控件 | MouseDown → 捕获焦点 | MouseDrag → 更新位置 | Repaint → 绘制坐标轴
        /// </summary>
        private void DrawEditModeHandlesWithEvents()
        {
            int n = m_settings.polygonPoints.Count;
            if (n == 0) return;

            for (int i = 0; i < n; i++)
            {
                // PositionHandle —— 必须在所有事件类型中被调用
                EditorGUI.BeginChangeCheck();
                Vector3 newPos = Handles.PositionHandle(m_settings.polygonPoints[i], Quaternion.identity);

                if (EditorGUI.EndChangeCheck())
                {
                    RegisterSettingsUndo("BT: Move Polygon Point");
                    m_settings.polygonPoints[i] = newPos;

                    // 重新投射 Y 坐标到地面
                    RaycastHit hit;
                    Ray ray = new Ray(newPos + Vector3.up * 100f, Vector3.down);
                    if (Physics.Raycast(ray, out hit, 200f, m_settings.paintLayers))
                    {
                        m_settings.polygonPoints[i] = new Vector3(newPos.x, hit.point.y, newPos.z);
                    }

                    EditorUtility.SetDirty(m_settings);
                }
            }

            // 顶点标签仅在 Repaint 阶段有效
            if (Event.current.type == EventType.Repaint)
            {
                for (int i = 0; i < n; i++)
                {
                    float handleSize = HandleUtility.GetHandleSize(m_settings.polygonPoints[i]) * m_settings.pointHandleSize;
                    Handles.Label(
                        m_settings.polygonPoints[i] + Vector3.up * handleSize * 2f,
                        i.ToString(),
                        EditorStyles.boldLabel
                    );
                }
            }
        }

        /// <summary>
        /// 添加模式（Q / View Tool）：顶点显示为静态圆点（仅可视，无交互）
        /// </summary>
        private void DrawAddModeVertices()
        {
            int n = m_settings.polygonPoints.Count;
            if (n == 0) return;

            for (int i = 0; i < n; i++)
            {
                float handleSize = HandleUtility.GetHandleSize(m_settings.polygonPoints[i]) * m_settings.pointHandleSize * 0.6f;

                // Ctrl 悬停的顶点：放大并变红
                bool isHovered = (i == m_nearestVertexIndex);
                float size = isHovered ? handleSize * 2f : handleSize;
                Color color = isHovered ? new Color(1f, 0.2f, 0.2f, 0.9f) : m_settings.pointHandleColor;

                // 顶点编号标签
                Handles.Label(
                    m_settings.polygonPoints[i] + Vector3.up * size * 2f,
                    isHovered ? $"Ctrl+Click 删除 {i}" : i.ToString(),
                    EditorStyles.boldLabel
                );

                // 圆点
                Handles.color = color;
                Handles.DrawSolidDisc(m_settings.polygonPoints[i], Vector3.up, size);
            }
        }

        private void DrawSceneOverlay()
        {
            Handles.BeginGUI();

            GUIStyle boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };

            string text;

            if (!m_isActive)
            {
                text = "Build Tools 已退出 —— 在窗口中点击「进入编辑」重新激活";
            }
            else switch (m_currentMode)
            {
                case ToolMode.Add:
                    text = "Build Tools [添加模式] —— 点击添加 | Shift+边=插入 | Ctrl+点=删除 | Enter=闭合 | Esc=撤销 | C=切换 | F=框选";
                    if (m_settings.polygonPoints.Count >= 2)
                        text += m_settings.isPolygonClosed
                            ? "\n✅ 多边形已闭合 —— 可生成建筑"
                            : "\n📐 多边形未闭合 —— 沿已有边线生成建筑";
                    break;

                case ToolMode.Edit:
                    text = "Build Tools [编辑模式] —— 拖拽坐标轴移动顶点 | 按 Q 返回添加模式";
                    break;

                default:
                    text = "Build Tools —— 按 Q 进入添加模式 | 按 W 进入编辑模式";
                    break;
            }

            GUIContent content = new GUIContent(text);
            Vector2 size = boxStyle.CalcSize(content);
            Rect rect = new Rect(10f, SceneView.lastActiveSceneView.position.height - size.y - 40f, size.x + 20f, size.y + 12f);

            GUI.Box(rect, content, boxStyle);

            Handles.EndGUI();
        }

        // ============================================================
        // Keyboard Events
        // ============================================================

        private void HandleKeyboardEvents()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    // Enter: 闭合/打开多边形
                    if (m_settings.polygonPoints.Count >= 3)
                    {
                        RegisterSettingsUndo("BT: Toggle Polygon Closed");
                        m_settings.isPolygonClosed = !m_settings.isPolygonClosed;
                        EditorUtility.SetDirty(m_settings);
                        e.Use();
                        Repaint();
                    }
                    break;

                case KeyCode.Escape:
                    // Esc: 撤销最后一个顶点，如果为空则不做操作
                    if (m_settings.polygonPoints.Count > 0)
                    {
                        RegisterSettingsUndo("BT: Remove Last Point");
                        m_settings.polygonPoints.RemoveAt(m_settings.polygonPoints.Count - 1);
                        if (m_settings.polygonPoints.Count < 3 && m_settings.isPolygonClosed)
                            m_settings.isPolygonClosed = false;
                        EditorUtility.SetDirty(m_settings);
                        e.Use();
                        Repaint();
                    }
                    break;

                case KeyCode.Delete:
                case KeyCode.Backspace:
                    // Delete/Backspace: 删除最后一个顶点
                    if (m_settings.polygonPoints.Count > 0)
                    {
                        RegisterSettingsUndo("BT: Remove Last Point");
                        m_settings.polygonPoints.RemoveAt(m_settings.polygonPoints.Count - 1);
                        if (m_settings.polygonPoints.Count < 3 && m_settings.isPolygonClosed)
                            m_settings.isPolygonClosed = false;
                        EditorUtility.SetDirty(m_settings);
                        e.Use();
                        Repaint();
                    }
                    break;

                case KeyCode.F:
                    // F: 框选多边形
                    if (m_settings.polygonPoints.Count > 0 && SceneView.lastActiveSceneView != null)
                    {
                        Vector3 center = GetPolygonCenter();
                        SceneView.lastActiveSceneView.LookAt(center, SceneView.lastActiveSceneView.rotation, 30f);
                        e.Use();
                    }
                    break;

                case KeyCode.C:
                    // C: 切换闭合状态
                    if (m_settings.polygonPoints.Count >= 3)
                    {
                        RegisterSettingsUndo("BT: Toggle Polygon Closed");
                        m_settings.isPolygonClosed = !m_settings.isPolygonClosed;
                        EditorUtility.SetDirty(m_settings);
                        e.Use();
                        Repaint();
                    }
                    break;
            }
        }

        // ============================================================
        // Utility
        // ============================================================

        private Vector3 GetPolygonCenter()
        {
            if (m_settings.polygonPoints.Count == 0)
                return Vector3.zero;

            Vector3 center = Vector3.zero;
            foreach (var pt in m_settings.polygonPoints)
                center += pt;
            return center / m_settings.polygonPoints.Count;
        }

        private float CalculatePerimeter()
        {
            int n = m_settings.polygonPoints.Count;
            if (n < 2) return 0f;

            float perimeter = 0f;
            int edgeCount = m_settings.isPolygonClosed ? n : n - 1;

            for (int i = 0; i < edgeCount; i++)
            {
                int next = (i + 1) % n;
                Vector3 a = m_settings.polygonPoints[i];
                Vector3 b = m_settings.polygonPoints[next];
                // XZ 平面距离
                perimeter += Vector3.Distance(
                    new Vector3(a.x, 0f, a.z),
                    new Vector3(b.x, 0f, b.z)
                );
            }

            return perimeter;
        }

        // ============================================================
        // Ctrl-Click: Delete vertex / Shift-Click: Insert on edge
        // ============================================================

        /// <summary>
        /// 查找距离给定点最近的顶点索引。返回 -1 表示没有顶点在吸附阈值内。
        /// </summary>
        private int FindClosestVertexIndex(Vector3 point)
        {
            int n = m_settings.polygonPoints.Count;
            if (n == 0) return -1;

            float bestDist = float.MaxValue;
            int bestIndex = -1;
            float snapThreshold = HandleUtility.GetHandleSize(point) * m_settings.pointHandleSize * 2f;

            for (int i = 0; i < n; i++)
            {
                float dist = Vector3.Distance(
                    new Vector3(point.x, 0f, point.z),
                    new Vector3(m_settings.polygonPoints[i].x, 0f, m_settings.polygonPoints[i].z)
                );

                if (dist < bestDist && dist < snapThreshold)
                {
                    bestDist = dist;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// 查找距离给定点最近的边，返回应插入新顶点的索引。
        /// 返回 -1 表示没有边在吸附阈值内。
        /// </summary>
        private int FindClosestEdgeInsertIndex(Vector3 point)
        {
            int n = m_settings.polygonPoints.Count;
            int edgeCount = m_settings.isPolygonClosed ? n : n - 1;
            if (edgeCount <= 0) return -1;

            float bestDist = float.MaxValue;
            int bestIndex = -1;
            float snapThreshold = HandleUtility.GetHandleSize(point) * m_settings.pointHandleSize * 3f;

            for (int i = 0; i < edgeCount; i++)
            {
                int next = (i + 1) % n;
                Vector3 start = m_settings.polygonPoints[i];
                Vector3 end = m_settings.polygonPoints[next];

                Vector3 closest = ClosestPointOnSegmentXZ(point, start, end);
                float dist = Vector3.Distance(
                    new Vector3(point.x, 0f, point.z),
                    new Vector3(closest.x, 0f, closest.z)
                );

                if (dist < bestDist && dist < snapThreshold)
                {
                    bestDist = dist;
                    // 插入索引为边的终点索引（即 next）
                    // 对于闭合多边形 edge (n-1, 0)，next=0，在末尾插入（实际插入在 n-1 和 0 之间）
                    bestIndex = (next == 0 && m_settings.isPolygonClosed) ? n : next;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// 将新顶点的 Y 坐标对齐到所在边
        /// </summary>
        private Vector3 SnapPointToEdgeY(Vector3 point, int insertIndex)
        {
            int n = m_settings.polygonPoints.Count;
            int prev = insertIndex - 1;
            if (prev < 0) prev = n - 1;
            int next = insertIndex % n;

            Vector3 start = m_settings.polygonPoints[prev];
            Vector3 end = m_settings.polygonPoints[next];

            Vector3 closest = ClosestPointOnSegmentXZ(point, start, end);

            // 在 XZ 投影边上线性插值 Y
            Vector3 segXZ = new Vector3(end.x - start.x, 0f, end.z - start.z);
            float segLen = segXZ.magnitude;
            if (segLen < 0.001f)
                return new Vector3(closest.x, start.y, closest.z);

            Vector3 toClosestXZ = new Vector3(closest.x - start.x, 0f, closest.z - start.z);
            float t = Vector3.Dot(toClosestXZ, segXZ.normalized) / segLen;
            t = Mathf.Clamp01(t);

            float y = Mathf.Lerp(start.y, end.y, t);
            return new Vector3(closest.x, y, closest.z);
        }

        /// <summary>
        /// 计算 XZ 平面上点到线段的最远投影点
        /// </summary>
        private Vector3 ClosestPointOnSegmentXZ(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 seg = new Vector3(end.x - start.x, 0f, end.z - start.z);
            float segLenSq = seg.sqrMagnitude;
            if (segLenSq < 0.000001f)
                return start;

            Vector3 toPoint = new Vector3(point.x - start.x, 0f, point.z - start.z);
            float t = Vector3.Dot(toPoint, seg) / segLenSq;
            t = Mathf.Clamp01(t);

            return new Vector3(
                start.x + t * seg.x,
                0f, // Y will be handled by SnapPointToEdgeY
                start.z + t * seg.z
            );
        }
    }
}
