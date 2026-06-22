using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace BuildTools
{
    /// <summary>
    /// 单个建筑的放置结果
    /// </summary>
    public struct PlacementResult
    {
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public int prefabSlotIndex;
        public int edgeIndex;
        public float normalizedEdgeT; // 0..1 along edge
    }

    /// <summary>
    /// 多边形的一条边
    /// </summary>
    internal struct PolygonEdge
    {
        public int index;
        public Vector3 start;
        public Vector3 end;
        public float length;           // XZ 平面上的长度
        public Vector3 direction;      // XZ 平面上的单位方向
        public float cumulativeStart;  // 累计周长起点

        /// <summary>边缘在 XZ 平面上的内向法线（指向多边形内部）</summary>
        public Vector3 inwardNormal;
    }

    // ============================================================
    // 放置算法（纯静态工具类）
    // ============================================================

    /// <summary>
    /// 建筑放置算法 —— 沿多边形边缘随机分布 Prefab
    /// </summary>
    public static class BuildToolsPlacement
    {
        private const int kMaxRetryPerPlacement = 10;

        /// <summary>
        /// 根据多边形和放置参数生成放置结果列表
        /// </summary>
        public static List<PlacementResult> GeneratePlacements(
            List<Vector3> polygonPoints,
            bool isClosed,
            PlacementSettings settings,
            System.Random rng,
            List<PrefabSlot> prefabSlots = null)
        {
            // 验证输入（开放或闭合均可，至少 2 个点 = 1 条边）
            if (polygonPoints == null || polygonPoints.Count < 2)
                return new List<PlacementResult>();

            // 构建边列表
            List<PolygonEdge> edges = BuildEdgeList(polygonPoints, isClosed);
            if (edges.Count == 0)
                return new List<PlacementResult>();

            float totalPerimeter = 0f;
            foreach (var edge in edges)
                totalPerimeter += edge.length;

            if (totalPerimeter < 0.01f)
                return new List<PlacementResult>();

            // 根据排列模式分发
            switch (settings.placementMode)
            {
                case PlacementMode.Equidistant:
                    return GenerateEquidistant(edges, totalPerimeter, polygonPoints, settings, rng, prefabSlots);
                case PlacementMode.Sequential:
                    return GenerateSequential(edges, totalPerimeter, polygonPoints, settings, rng, prefabSlots);
                default:
                    return GenerateRandom(edges, totalPerimeter, polygonPoints, settings, rng, prefabSlots);
            }
        }

        /// <summary>
        /// 随机排列：沿边缘加权随机分布
        /// </summary>
        private static List<PlacementResult> GenerateRandom(
            List<PolygonEdge> edges, float totalPerimeter,
            List<Vector3> polygonPoints, PlacementSettings settings, System.Random rng,
            List<PrefabSlot> prefabSlots = null)
        {
            List<PlacementResult> results = new List<PlacementResult>();
            int buildingCount = Mathf.Max(1, settings.buildingCount);
            float avgHalfExtentPerp = GetAverageHalfExtentPerpendicular(prefabSlots, edges[0].direction);

            for (int i = 0; i < buildingCount; i++)
            {
                bool placed = false;

                for (int retry = 0; retry < kMaxRetryPerPlacement; retry++)
                {
                    int edgeIndex = PickWeightedEdge(edges, totalPerimeter, rng);
                    PolygonEdge edge = edges[edgeIndex];

                    float tEdge = (float)rng.NextDouble() * edge.length;
                    float tNorm = edge.length > 0.001f ? tEdge / edge.length : 0f;
                    Vector3 pos = Vector3.Lerp(edge.start, edge.end, tNorm);

                    float totalOffset = GetTotalEdgeOffset(settings, edge, avgHalfExtentPerp, settings.edgeOffset);
                    if (Mathf.Abs(totalOffset) > 0.001f)
                        pos += edge.inwardNormal * totalOffset;

                    Quaternion rotation = CalculateRotation(pos, edge, polygonPoints, settings, rng);

                    if (settings.minSpacing > 0.001f)
                    {
                        if (!MeetsSpacingRequirement(pos, results, settings.minSpacing))
                            continue;
                    }

                    results.Add(new PlacementResult
                    {
                        worldPosition = pos,
                        worldRotation = rotation,
                        prefabSlotIndex = -1,
                        edgeIndex = edgeIndex,
                        normalizedEdgeT = tNorm
                    });

                    placed = true;
                    break;
                }

                if (!placed)
                    Debug.LogWarning($"BuildTools: 无法为第 {i + 1} 个建筑找到合适位置（间距约束过严？）");
            }

            return results;
        }

        /// <summary>
        /// 等距排列：沿周长均匀分布，间距指模型边缘到边缘的空隙
        /// spacing == 0 → 自动根据 buildingCount 等分周长（中心到中心）
        /// spacing > 0  → 中心步长 = 2×平均半宽 + spacing
        /// </summary>
        private static List<PlacementResult> GenerateEquidistant(
            List<PolygonEdge> edges, float totalPerimeter,
            List<Vector3> polygonPoints, PlacementSettings settings, System.Random rng,
            List<PrefabSlot> prefabSlots)
        {
            List<PlacementResult> results = new List<PlacementResult>();

            float step;
            int count;

            if (settings.spacing > 0.001f)
            {
                // 用平均模型尺寸计算中心到中心步长
                float avgHalfExtent = GetAverageHalfExtent(prefabSlots, edges[0].direction);
                step = avgHalfExtent * 2f + settings.spacing;
                if (step < 0.01f) step = 1f;
                count = Mathf.FloorToInt(totalPerimeter / step);
                if (count < 1) count = 1;
            }
            else
            {
                count = Mathf.Max(1, settings.buildingCount);
                step = totalPerimeter / count;
            }

            float startOffset = (float)rng.NextDouble() * step;
            float avgHalfExtentPerp = GetAverageHalfExtentPerpendicular(prefabSlots, edges[0].direction);

            for (int i = 0; i < count; i++)
            {
                float distAlongPerimeter = startOffset + i * step;
                if (distAlongPerimeter >= totalPerimeter)
                    distAlongPerimeter -= totalPerimeter;

                var (edge, edgeIndex, tNorm) = LocateOnEdges(edges, distAlongPerimeter);
                if (edgeIndex < 0) continue;

                Vector3 pos = Vector3.Lerp(edge.start, edge.end, tNorm);
                float totalOffset = GetTotalEdgeOffset(settings, edge, avgHalfExtentPerp, settings.edgeOffset);
                if (Mathf.Abs(totalOffset) > 0.001f)
                    pos += edge.inwardNormal * totalOffset;

                Quaternion rotation = CalculateRotation(pos, edge, polygonPoints, settings, rng);

                if (settings.minSpacing > 0.001f && !MeetsSpacingRequirement(pos, results, settings.minSpacing))
                    continue;

                results.Add(new PlacementResult
                {
                    worldPosition = pos, worldRotation = rotation,
                    prefabSlotIndex = -1, edgeIndex = edgeIndex, normalizedEdgeT = tNorm
                });
            }

            return results;
        }

        /// <summary>
        /// 顺序排列：从第一个顶点出发，放置 buildingCount 个模型。
        /// spacing 始终指模型边缘到边缘的空隙：
        ///   spacing == 0 → 模型紧挨（A.max 贴合 B.min）
        ///   spacing > 0  → 模型之间有 spacing 单位空隙
        /// </summary>
        private static List<PlacementResult> GenerateSequential(
            List<PolygonEdge> edges, float totalPerimeter,
            List<Vector3> polygonPoints, PlacementSettings settings, System.Random rng,
            List<PrefabSlot> prefabSlots)
        {
            List<PlacementResult> results = new List<PlacementResult>();

            if (edges.Count == 0) return results;

            float avgHalfExtent = GetAverageHalfExtent(prefabSlots, edges[0].direction);
            if (avgHalfExtent < 0.01f) avgHalfExtent = 0.5f;

            int count = Mathf.Max(1, settings.buildingCount);

            float distFromStart = 0f;
            float prevLeading = 0f;
            float avgHalfExtentPerp = GetAverageHalfExtentPerpendicular(prefabSlots, edges[0].direction);

            for (int i = 0; i < count; i++)
            {
                // 随机选 Prefab 获取真实 trailing/leading
                float curTrailing = avgHalfExtent;
                float curLeading = avgHalfExtent;
                float curHalfExtentPerp = avgHalfExtentPerp;
                GameObject curPrefab = null;
                if (prefabSlots != null)
                {
                    var (slotIdx, prefab) = PickPrefabForPlacement(prefabSlots, rng);
                    curPrefab = prefab;
                    if (prefab != null)
                    {
                        GetPrefabExtentsAlongEdge(prefab, edges[0].direction, out curTrailing, out curLeading);
                        curHalfExtentPerp = GetPrefabHalfExtentPerpendicular(prefab, edges[0].direction);
                    }
                }

                if (i == 0)
                {
                    // 第一个模型：后缘对齐顶点0
                    // center = vertex0 + trailing * edgeDir
                    distFromStart = curTrailing;
                }
                else
                {
                    // 后续模型：prev_leading_edge + spacing = cur_trailing_edge
                    // prev_leading_edge = prev_center + prev_leading
                    // cur_trailing_edge = cur_center - cur_trailing
                    // cur_center = prev_center + prev_leading + spacing + cur_trailing
                    distFromStart += prevLeading + settings.spacing + curTrailing;
                }

                if (distFromStart >= totalPerimeter)
                {
                    Debug.LogWarning($"BuildTools: 顺序排列 —— 第 {i + 1} 个模型超出周长（{distFromStart:F2} > {totalPerimeter:F2}），已停止。");
                    break;
                }

                var (edge, edgeIndex, tNorm) = LocateOnEdges(edges, distFromStart);
                if (edgeIndex < 0) break;

                Vector3 pos = Vector3.Lerp(edge.start, edge.end, tNorm);
                float totalOffset = GetTotalEdgeOffset(settings, edge, curHalfExtentPerp, settings.edgeOffset);
                if (Mathf.Abs(totalOffset) > 0.001f)
                    pos += edge.inwardNormal * totalOffset;

                Quaternion rotation = CalculateRotation(pos, edge, polygonPoints, settings, rng);

                if (settings.minSpacing > 0.001f && !MeetsSpacingRequirement(pos, results, settings.minSpacing))
                    continue;

                results.Add(new PlacementResult
                {
                    worldPosition = pos, worldRotation = rotation,
                    prefabSlotIndex = -1, edgeIndex = edgeIndex, normalizedEdgeT = tNorm
                });

                prevLeading = curLeading;
            }

            return results;
        }

        /// <summary>在边列表中定位周长距离对应的边和位置</summary>
        private static (PolygonEdge edge, int edgeIndex, float tNorm) LocateOnEdges(
            List<PolygonEdge> edges, float distAlongPerimeter)
        {
            for (int j = 0; j < edges.Count; j++)
            {
                if (distAlongPerimeter < edges[j].cumulativeStart + edges[j].length)
                {
                    float localDist = distAlongPerimeter - edges[j].cumulativeStart;
                    float tNorm = edges[j].length > 0.001f ? localDist / edges[j].length : 0f;
                    return (edges[j], j, tNorm);
                }
            }
            // 超出总周长，回到起点
            if (edges.Count > 0)
                return (edges[0], 0, 0f);
            return (default, -1, 0f);
        }

        /// <summary>按权重随机选一个 Prefab，返回其槽位索引和 GameObject</summary>
        private static (int slotIndex, GameObject prefab) PickPrefabForPlacement(
            List<PrefabSlot> prefabSlots, System.Random rng)
        {
            if (prefabSlots == null || prefabSlots.Count == 0)
                return (-1, null);

            List<int> enabledIndices = new List<int>();
            List<float> weights = new List<float>();
            float totalW = 0f;
            for (int i = 0; i < prefabSlots.Count; i++)
            {
                if (prefabSlots[i].enabled && prefabSlots[i].prefab != null)
                {
                    enabledIndices.Add(i);
                    weights.Add(prefabSlots[i].weight);
                    totalW += prefabSlots[i].weight;
                }
            }

            if (enabledIndices.Count == 0)
                return (-1, null);

            float t = (float)rng.NextDouble() * totalW;
            float cumulative = 0f;
            for (int i = 0; i < enabledIndices.Count; i++)
            {
                cumulative += weights[i];
                if (t <= cumulative)
                    return (enabledIndices[i], prefabSlots[enabledIndices[i]].prefab);
            }

            int last = enabledIndices[enabledIndices.Count - 1];
            return (last, prefabSlots[last].prefab);
        }

        /// <summary>
        /// 实例化放置结果到场景中
        /// </summary>
        /// <returns>创建的 GameObject 列表（用于撤销/清除跟踪）</returns>
        public static List<GameObject> InstantiatePlacements(
            List<PlacementResult> placements,
            List<PrefabSlot> prefabSlots,
            BuildToolsSceneSettings sceneSettings,
            bool overwriteLayer,
            int placeLayer,
            bool groupPrefabs,
            string groupName,
            System.Random rng)
        {
            List<GameObject> createdInstances = new List<GameObject>();

            if (placements == null || placements.Count == 0)
                return createdInstances;

            if (prefabSlots == null || prefabSlots.Count == 0)
                return createdInstances;

            // 构建加权 Prefab 槽位列表
            List<int> enabledSlots = new List<int>();
            List<float> enabledWeights = new List<float>();
            float totalWeight = 0f;

            for (int i = 0; i < prefabSlots.Count; i++)
            {
                if (prefabSlots[i].enabled && prefabSlots[i].prefab != null)
                {
                    enabledSlots.Add(i);
                    enabledWeights.Add(prefabSlots[i].weight);
                    totalWeight += prefabSlots[i].weight;
                }
            }

            if (enabledSlots.Count == 0)
            {
                Debug.LogWarning("BuildTools: 没有可用的 Prefab 槽位（所有槽位都已禁用或 Prefab 为空）。");
                return createdInstances;
            }

            // 获取或创建父节点
            Transform parent = GetOrCreateParent(sceneSettings, groupPrefabs, groupName);
            if (parent == null && groupPrefabs)
            {
                // 创建分组的根 GameObject
                GameObject groupRoot = new GameObject(groupName);
                Undo.RegisterCreatedObjectUndo(groupRoot, "BT: Create Group");
                parent = groupRoot.transform;
                if (sceneSettings != null)
                    sceneSettings.parentForBuildings = groupRoot;
            }

            for (int p = 0; p < placements.Count; p++)
            {
                var placement = placements[p];

                // 加权随机选择 Prefab 槽位
                int slotIndex = PickWeightedSlot(enabledSlots, enabledWeights, totalWeight, rng);
                if (slotIndex < 0 || slotIndex >= prefabSlots.Count)
                    continue;

                GameObject prefab = prefabSlots[slotIndex].prefab;
                if (prefab == null)
                    continue;

                // 实例化
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null)
                    continue;

                instance.transform.position = placement.worldPosition;
                instance.transform.rotation = placement.worldRotation;

                if (parent != null)
                    instance.transform.SetParent(parent, true);

                if (overwriteLayer)
                    SetLayerRecursive(instance, placeLayer);

                Undo.RegisterCreatedObjectUndo(instance, "BT: Generate Buildings");

                // 更新 PlacementResult 的 prefabSlotIndex
                var updated = placement;
                updated.prefabSlotIndex = slotIndex;
                placements[p] = updated;

                createdInstances.Add(instance);
            }

            if (sceneSettings != null)
            {
                Undo.RecordObject(sceneSettings, "BT: Track Generated Buildings");
                sceneSettings.generatedBuildings = createdInstances;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            return createdInstances;
        }

        // ============================================================
        // Internal: Edge Calculation
        // ============================================================

        /// <summary>
        /// 从顶点列表构建边列表，包含内向法线等衍生数据
        /// </summary>
        private static List<PolygonEdge> BuildEdgeList(List<Vector3> points, bool isClosed)
        {
            List<PolygonEdge> edges = new List<PolygonEdge>();
            int n = points.Count;
            int edgeCount = isClosed ? n : n - 1;
            if (edgeCount <= 0) return edges;

            // 计算中心（XZ 平面）
            Vector3 center = Vector3.zero;
            for (int i = 0; i < n; i++)
                center += points[i];
            center /= n;

            float cumulativeLen = 0f;

            for (int i = 0; i < edgeCount; i++)
            {
                Vector3 start = points[i];
                Vector3 end = points[(i + 1) % n];

                // 在 XZ 平面上计算
                Vector3 startXZ = new Vector3(start.x, 0f, start.z);
                Vector3 endXZ = new Vector3(end.x, 0f, end.z);

                float len = Vector3.Distance(startXZ, endXZ);
                Vector3 dir = len > 0.0001f ? (endXZ - startXZ).normalized : Vector3.forward;

                // 计算 XZ 平面上的垂直向量（顺时针旋转90度）
                Vector3 perp = new Vector3(dir.z, 0f, -dir.x);

                // 选择指向多边形中心的方向作为内向法线
                Vector3 edgeMid = (startXZ + endXZ) * 0.5f;
                Vector3 toCenter = new Vector3(center.x - edgeMid.x, 0f, center.z - edgeMid.z).normalized;
                if (Vector3.Dot(perp, toCenter) < 0f)
                    perp = -perp;

                edges.Add(new PolygonEdge
                {
                    index = i,
                    start = start,
                    end = end,
                    length = len,
                    direction = dir,
                    cumulativeStart = cumulativeLen,
                    inwardNormal = perp
                });

                cumulativeLen += len;
            }

            return edges;
        }

        // ============================================================
        // Internal: Selection Helpers
        // ============================================================

        /// <summary>
        /// 按边长加权随机选择一条边
        /// </summary>
        private static int PickWeightedEdge(List<PolygonEdge> edges, float totalPerimeter, System.Random rng)
        {
            float t = (float)rng.NextDouble() * totalPerimeter;
            foreach (var edge in edges)
            {
                if (t < edge.cumulativeStart + edge.length)
                    return edge.index;
            }
            return edges[edges.Count - 1].index;
        }

        /// <summary>
        /// 按权重加权随机选择一个 Prefab 槽位
        /// </summary>
        private static int PickWeightedSlot(List<int> slotIndices, List<float> weights, float totalWeight, System.Random rng)
        {
            if (slotIndices.Count == 0) return -1;
            if (totalWeight <= 0f) return slotIndices[0];

            float t = (float)rng.NextDouble() * totalWeight;
            float cumulative = 0f;

            for (int i = 0; i < slotIndices.Count; i++)
            {
                cumulative += weights[i];
                if (t <= cumulative)
                    return slotIndices[i];
            }

            return slotIndices[slotIndices.Count - 1];
        }

        // ============================================================
        // Internal: Rotation Calculation
        // ============================================================

        private static Quaternion CalculateRotation(
            Vector3 position,
            PolygonEdge edge,
            List<Vector3> polygonPoints,
            PlacementSettings settings,
            System.Random rng)
        {
            Vector3 forward;

            switch (settings.orientationMode)
            {
                case OrientationMode.FacePolygonCenter:
                    // 朝向多边形中心
                    Vector3 center = Vector3.zero;
                    foreach (var pt in polygonPoints)
                        center += pt;
                    center /= polygonPoints.Count;
                    forward = new Vector3(center.x - position.x, 0f, center.z - position.z).normalized;
                    if (forward.sqrMagnitude < 0.001f)
                        forward = Vector3.forward;
                    break;

                case OrientationMode.AlongEdge:
                    // 沿边缘方向 + 可调旋转偏移
                    forward = edge.direction;
                    if (Mathf.Abs(settings.edgeRotationOffset) > 0.01f)
                        forward = Quaternion.Euler(0f, settings.edgeRotationOffset, 0f) * forward;
                    break;

                case OrientationMode.WorldDirection:
                    // 固定世界方向
                    forward = settings.worldDirection.normalized;
                    if (forward.sqrMagnitude < 0.001f)
                        forward = Vector3.forward;
                    break;

                case OrientationMode.Random:
                    // 完全随机
                    forward = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f) * Vector3.forward;
                    break;

                default:
                    forward = Vector3.forward;
                    break;
            }

            Quaternion baseRotation = Quaternion.LookRotation(forward, Vector3.up);

            // 随机旋转偏移
            if (settings.randomizeRotation && settings.orientationMode != OrientationMode.Random)
            {
                float yaw = (float)(rng.NextDouble() * 2f - 1f) * settings.randomRotationRange;
                baseRotation *= Quaternion.Euler(0f, yaw, 0f);
            }

            return baseRotation;
        }

        // ============================================================
        // Internal: Spacing Check
        // ============================================================

        private static bool MeetsSpacingRequirement(Vector3 candidate, List<PlacementResult> placed, float minDist)
        {
            Vector3 candXZ = new Vector3(candidate.x, 0f, candidate.z);

            foreach (var p in placed)
            {
                Vector3 pXZ = new Vector3(p.worldPosition.x, 0f, p.worldPosition.z);
                if (Vector3.Distance(candXZ, pXZ) < minDist)
                    return false;
            }
            return true;
        }

        // ============================================================
        // Internal: Prefab Extent Calculation
        // ============================================================

        /// <summary>
        /// 获取 Prefab 沿边方向，从 pivot 到包围盒前缘的半宽度（XZ 平面投影）
        /// 前缘 = 模型在边方向上 pivot 到最远点的距离
        /// 后缘 = 模型在边反方向上 pivot 到最远点的距离
        /// 两者可能不同（pivot 不在几何中心时）
        /// </summary>
        private static void GetPrefabExtentsAlongEdge(GameObject prefab, Vector3 edgeDirXZ,
            out float trailingFromPivot, out float leadingFromPivot)
        {
            trailingFromPivot = 0.5f;
            leadingFromPivot = 0.5f;

            if (prefab == null) return;

            // 用 localBounds 计算（相对 pivot 的包围盒）
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();
            Bounds localBounds = new Bounds();
            bool hasBounds = false;

            if (renderers.Length > 0)
            {
                localBounds = TransformBoundsToLocal(renderers[0]);
                hasBounds = true;
                for (int i = 1; i < renderers.Length; i++)
                    localBounds.Encapsulate(TransformBoundsToLocal(renderers[i]));
            }
            else
            {
                // 尝试 Collider
                Collider[] colliders = prefab.GetComponentsInChildren<Collider>();
                if (colliders.Length > 0)
                {
                    localBounds = TransformBoundsToLocal(colliders[0]);
                    hasBounds = true;
                    for (int i = 1; i < colliders.Length; i++)
                        localBounds.Encapsulate(TransformBoundsToLocal(colliders[i]));
                }
            }

            if (!hasBounds) return;

            // 将包围盒的8个顶点投影到边方向，取 min/max
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;

            // 8个角点投影
            float projMin = float.MaxValue;
            float projMax = float.MinValue;
            for (int ix = 0; ix <= 1; ix++)
            for (int iy = 0; iy <= 1; iy++)
            for (int iz = 0; iz <= 1; iz++)
            {
                Vector3 corner = new Vector3(
                    ix == 0 ? min.x : max.x,
                    iy == 0 ? min.y : max.y,
                    iz == 0 ? min.z : max.z
                );
                float proj = corner.x * edgeDirXZ.x + corner.z * edgeDirXZ.z;
                if (proj < projMin) projMin = proj;
                if (proj > projMax) projMax = proj;
            }

            // 后缘 = pivot(0) 到最小投影的距离
            trailingFromPivot = -projMin;  // projMin 为负时，trailing 为正
            // 前缘 = 最大投影到 pivot(0) 的距离
            leadingFromPivot = projMax;

            // 确保非负
            if (trailingFromPivot < 0.01f) trailingFromPivot = leadingFromPivot * 0.5f;
            if (leadingFromPivot < 0.01f) leadingFromPivot = trailingFromPivot * 0.5f;
            if (trailingFromPivot < 0.01f) trailingFromPivot = 0.5f;
            if (leadingFromPivot < 0.01f) leadingFromPivot = 0.5f;
        }

        /// <summary>
        /// 将 Renderer/Collider 的世界 bounds 转换到 Prefab 根节点的本地空间
        /// </summary>
        private static Bounds TransformBoundsToLocal(Renderer r)
        {
            // bounds 是 world space，需要转回 local
            Bounds worldBounds = r.bounds;
            Transform root = r.transform.root;
            Vector3 localCenter = root.InverseTransformPoint(worldBounds.center);
            Vector3 localExtents = root.InverseTransformVector(worldBounds.extents);
            // 取绝对值（忽略旋转后的符号）
            localExtents = new Vector3(Mathf.Abs(localExtents.x), Mathf.Abs(localExtents.y), Mathf.Abs(localExtents.z));
            return new Bounds(localCenter, localExtents * 2f);
        }

        private static Bounds TransformBoundsToLocal(Collider c)
        {
            Bounds worldBounds = c.bounds;
            Transform root = c.transform.root;
            Vector3 localCenter = root.InverseTransformPoint(worldBounds.center);
            Vector3 localExtents = root.InverseTransformVector(worldBounds.extents);
            localExtents = new Vector3(Mathf.Abs(localExtents.x), Mathf.Abs(localExtents.y), Mathf.Abs(localExtents.z));
            return new Bounds(localCenter, localExtents * 2f);
        }

        /// <summary>
        /// 获取 Prefab 沿边方向的半宽度（前后平均值，用于等距排列等估算场景）
        /// </summary>
        private static float GetPrefabHalfExtentAlongEdge(GameObject prefab, Vector3 edgeDirXZ)
        {
            GetPrefabExtentsAlongEdge(prefab, edgeDirXZ, out float trailing, out float leading);
            return (trailing + leading) * 0.5f;
        }

        /// <summary>
        /// 获取 Prefab 沿边垂方向的半宽度（XZ 平面，垂直于 edgeDir）
        /// </summary>
        private static float GetPrefabHalfExtentPerpendicular(GameObject prefab, Vector3 edgeDirXZ)
        {
            // 垂方向（XZ 平面内旋转 90°）
            Vector3 perpDir = new Vector3(-edgeDirXZ.z, 0f, edgeDirXZ.x);
            GetPrefabExtentsAlongEdge(prefab, perpDir, out float trailing, out float leading);
            return (trailing + leading) * 0.5f;
        }

        /// <summary>
        /// 根据对齐方式计算最终边缘偏移量
        /// </summary>
        private static float GetTotalEdgeOffset(PlacementSettings settings, PolygonEdge edge,
            float halfExtentPerp, float baseEdgeOffset)
        {
            if (settings.orientationMode != OrientationMode.AlongEdge)
                return baseEdgeOffset;

            switch (settings.edgeAlignment)
            {
                case EdgeAlignment.Left:
                    return halfExtentPerp + baseEdgeOffset;
                case EdgeAlignment.Right:
                    return -halfExtentPerp + baseEdgeOffset;
                default:
                    return baseEdgeOffset;
            }
        }

        /// <summary>
        /// 计算所有可用 Prefab 的平均半宽度（沿边方向）
        /// </summary>
        private static float GetAverageHalfExtent(List<PrefabSlot> prefabSlots, Vector3 edgeDirXZ)
        {
            if (prefabSlots == null || prefabSlots.Count == 0) return 0.5f;

            float sum = 0f;
            int count = 0;
            foreach (var slot in prefabSlots)
            {
                if (slot.enabled && slot.prefab != null)
                {
                    sum += GetPrefabHalfExtentAlongEdge(slot.prefab, edgeDirXZ);
                    count++;
                }
            }
            return count > 0 ? sum / count : 0.5f;
        }

        /// <summary>
        /// 计算所有可用 Prefab 的平均半宽度（垂直于边方向）
        /// </summary>
        private static float GetAverageHalfExtentPerpendicular(List<PrefabSlot> prefabSlots, Vector3 edgeDirXZ)
        {
            if (prefabSlots == null || prefabSlots.Count == 0) return 0.5f;

            Vector3 perpDir = new Vector3(-edgeDirXZ.z, 0f, edgeDirXZ.x);
            float sum = 0f;
            int count = 0;
            foreach (var slot in prefabSlots)
            {
                if (slot.enabled && slot.prefab != null)
                {
                    GetPrefabExtentsAlongEdge(slot.prefab, perpDir, out float trailing, out float leading);
                    sum += (trailing + leading) * 0.5f;
                    count++;
                }
            }
            return count > 0 ? sum / count : 0.5f;
        }

        // ============================================================
        // Internal: Parent & Layer Helpers
        // ============================================================

        private static Transform GetOrCreateParent(BuildToolsSceneSettings sceneSettings, bool groupPrefabs, string groupName)
        {
            if (!groupPrefabs)
                return null;

            // 先尝试从场景设置中获取
            if (sceneSettings != null && sceneSettings.parentForBuildings != null)
                return sceneSettings.parentForBuildings.transform;

            // 尝试在场景中查找
            GameObject existing = GameObject.Find(groupName);
            if (existing != null)
            {
                if (sceneSettings != null)
                    sceneSettings.parentForBuildings = existing;
                return existing.transform;
            }

            return null;
        }

        private static void SetLayerRecursive(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform)
            {
                SetLayerRecursive(child.gameObject, layer);
            }
        }
    }
}
