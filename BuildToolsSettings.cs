using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildTools
{
    // ============================================================
    // Enums
    // ============================================================

    /// <summary>
    /// 建筑朝向模式
    /// </summary>
    public enum OrientationMode
    {
        FacePolygonCenter,  // 物体前方朝向多边形内部中心
        AlongEdge,          // 物体前方沿边缘切线方向
        WorldDirection,     // 固定世界方向
        Random              // 完全随机 Y 轴旋转
    }

    /// <summary>
    /// 沿边对齐方式
    /// </summary>
    public enum EdgeAlignment
    {
        Center, // 居中
        Left,   // 左对齐（模型完全在边内侧）
        Right   // 右对齐（模型完全在边外侧）
    }

    /// <summary>
    /// 排列模式
    /// </summary>
    public enum PlacementMode
    {
        Random,         // 随机排列
        Equidistant,    // 等距排列（绕周长均匀分布）
        Sequential      // 顺序排列（从第一个顶点出发，固定间距沿边排列）
    }

    // ============================================================
    // Data Structures
    // ============================================================

    /// <summary>
    /// Prefab 槽位 —— 存储一个建筑 Prefab 及其选择权重
    /// </summary>
    [System.Serializable]
    public struct PrefabSlot
    {
        [Tooltip("建筑 Prefab")]
        public GameObject prefab;

        [Tooltip("选择权重 (0-1)，值越大被选中的概率越高")]
        [Range(0f, 1f)]
        public float weight;

        [Tooltip("是否启用此槽位")]
        public bool enabled;

        public PrefabSlot(GameObject prefab, float weight = 1f, bool enabled = true)
        {
            this.prefab = prefab;
            this.weight = weight;
            this.enabled = enabled;
        }
    }

    /// <summary>
    /// 放置参数
    /// </summary>
    [System.Serializable]
    public class PlacementSettings
    {
        [Tooltip("排列模式：随机 或 等距")]
        public PlacementMode placementMode = PlacementMode.Random;

        [Tooltip("要放置的建筑数量")]
        [Min(1)]
        public int buildingCount = 10;

        [Tooltip("间距（0 = 自动按数量等分）。等距/顺序模式生效")]
        [Min(0f)]
        public float spacing = 0f;

        [Tooltip("随机种子，相同种子产生相同的放置结果。仅随机模式生效")]
        public int randomSeed = 0;

        [Tooltip("边缘偏移距离（正数=向内偏移，负数=向外偏移）")]
        public float edgeOffset = 0f;

        [Tooltip("建筑之间的最小间距（0 = 不检测）")]
        [Min(0f)]
        public float minSpacing = 0f;

        [Tooltip("建筑朝向模式")]
        public OrientationMode orientationMode = OrientationMode.FacePolygonCenter;

        [Tooltip("世界方向（仅当朝向模式为 WorldDirection 时生效）")]
        public Vector3 worldDirection = Vector3.forward;

        [Tooltip("沿边缘方向额外旋转角度（仅沿边缘模式生效，180=反向）")]
        [Range(-180f, 180f)]
        public float edgeRotationOffset = 0f;

        [Tooltip("沿边对齐方式（仅沿边缘模式生效）")]
        public EdgeAlignment edgeAlignment = EdgeAlignment.Center;

        [Tooltip("是否添加随机旋转偏移")]
        public bool randomizeRotation = false;

        [Tooltip("随机旋转范围（±角度）")]
        [Range(0f, 180f)]
        public float randomRotationRange = 15f;

        [Tooltip("覆盖生成的建筑图层")]
        public bool overwriteLayer = false;

        [Tooltip("目标图层")]
        public int placeLayer = 0;

        [Tooltip("将生成的建筑分组到同一个父 GameObject 下")]
        public bool groupPrefabs = true;

        [Tooltip("分组父 GameObject 的名称")]
        public string groupName = "_Buildings";
    }

    // ============================================================
    // Main Settings ScriptableObject
    // ============================================================

    /// <summary>
    /// BuildTools 的持久化设置，存储为 ScriptableObject 资源
    /// 自动创建于 Assets/BuildTools/Settings/BuildToolsSettings.asset
    /// </summary>
    public class BuildToolsSettings : ScriptableObject
    {
        // --------------------------------------------------------
        // Static
        // --------------------------------------------------------

        /// <summary>当前活动的设置实例</summary>
        public static BuildToolsSettings current;

        /// <summary>设置资源在项目中的路径</summary>
        public const string k_SettingsPath = "Assets/BuildTools/Settings/BuildToolsSettings.asset";
        public const string k_SettingsFolder = "Assets/BuildTools/Settings";

        // --------------------------------------------------------
        // Polygon Data
        // --------------------------------------------------------

        [Header("多边形数据")]
        [Tooltip("多边形顶点列表（世界坐标）")]
        public List<Vector3> polygonPoints = new List<Vector3>();

        [Tooltip("多边形是否闭合")]
        public bool isPolygonClosed = false;

        // --------------------------------------------------------
        // Prefab Slots
        // --------------------------------------------------------

        [Header("建筑 Prefab 槽位")]
        [Tooltip("可用于放置的 Prefab 列表")]
        public List<PrefabSlot> prefabSlots = new List<PrefabSlot>();

        // --------------------------------------------------------
        // Placement Settings
        // --------------------------------------------------------

        [Header("放置参数")]
        public PlacementSettings placement = new PlacementSettings();

        // --------------------------------------------------------
        // Visual Settings
        // --------------------------------------------------------

        [Header("可视化")]
        [Tooltip("多边形轮廓颜色")]
        public Color polygonOutlineColor = Color.yellow;

        [Tooltip("多边形填充颜色")]
        public Color polygonFillColor = new Color(1f, 1f, 0f, 0.15f);

        [Tooltip("顶点手柄颜色")]
        public Color pointHandleColor = Color.green;

        [Tooltip("顶点手柄大小")]
        [Range(0.1f, 2f)]
        public float pointHandleSize = 0.3f;

        [Tooltip("预览线的颜色（未闭合时从最后一个顶点到鼠标位置）")]
        public Color previewLineColor = new Color(1f, 1f, 1f, 0.4f);

        // --------------------------------------------------------
        // Scene Settings
        // --------------------------------------------------------

        [Header("场景设置")]
        [Tooltip("射线检测的图层遮罩")]
        public LayerMask paintLayers = ~0;

        /// <summary>
        /// 重置所有设置为默认值
        /// </summary>
        public void ResetAll()
        {
            polygonPoints.Clear();
            isPolygonClosed = false;
            prefabSlots.Clear();
            placement = new PlacementSettings();
            polygonOutlineColor = Color.yellow;
            polygonFillColor = new Color(1f, 1f, 0f, 0.15f);
            pointHandleColor = Color.green;
            pointHandleSize = 0.3f;
            previewLineColor = new Color(1f, 1f, 1f, 0.4f);
        }
    }

    // ============================================================
    // Scene Settings MonoBehaviour
    // ============================================================

    /// <summary>
    /// 场景级别的 BuildTools 设置，挂在一个隐藏的 GameObject 上
    /// 用于存储生成建筑的引用等场景特定数据
    /// </summary>
    public class BuildToolsSceneSettings : MonoBehaviour
    {
        /// <summary>生成建筑的父 GameObject（如果使用分组模式）</summary>
        public GameObject parentForBuildings;

        /// <summary>上一次生成的建筑实例列表（用于清除操作）</summary>
        public List<GameObject> generatedBuildings = new List<GameObject>();

        private void Awake()
        {
            hideFlags = HideFlags.HideInHierarchy;
        }
    }
}
