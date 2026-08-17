using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GeometryReference))]
public class GeometryReferenceEditor : Editor
{
    private enum SelectionMode
    {
        None,
        SurfaceA,
        SurfaceB
    }

    private SelectionMode selectionMode = SelectionMode.None;

    private const float DefaultPlaneDistanceTolerance = 0.0001f;
    private const float DefaultNormalAngleTolerance = 1.0f;

    private float planeDistanceTolerance = DefaultPlaneDistanceTolerance;
    private float normalAngleTolerance = DefaultNormalAngleTolerance;

    private GeometryReference reference;

    private void OnEnable()
    {
        reference = (GeometryReference)target;
        SceneView.duringSceneGui += HandleSceneViewGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= HandleSceneViewGUI;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.LabelField("Planar Surface Selection", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        normalAngleTolerance =
            EditorGUILayout.FloatField("Normal Angle Tolerance (deg)", normalAngleTolerance);

        planeDistanceTolerance =
            EditorGUILayout.FloatField("Plane Distance Tolerance (m)", planeDistanceTolerance);

        normalAngleTolerance = Mathf.Max(0.001f, normalAngleTolerance);
        planeDistanceTolerance = Mathf.Max(0.0000001f, planeDistanceTolerance);

        EditorGUILayout.Space();

        if (GUILayout.Button("Select Surface A"))
        {
            selectionMode = SelectionMode.SurfaceA;
            SceneView.RepaintAll();
        }

        if (GUILayout.Button("Select Surface B"))
        {
            selectionMode = SelectionMode.SurfaceB;
            SceneView.RepaintAll();
        }

        if (GUILayout.Button("Cancel Selection"))
        {
            selectionMode = SelectionMode.None;
            SceneView.RepaintAll();
        }

        EditorGUILayout.Space();
        DrawSelectionInfo();
        EditorGUILayout.Space();

        EditorGUILayout.LabelField("Reference Frame", EditorStyles.boldLabel);

        reference.referenceFrameName =
            EditorGUILayout.TextField(
                "Frame Name",
                reference.referenceFrameName
            );

        if (string.IsNullOrWhiteSpace(reference.referenceFrameName))
            reference.referenceFrameName = "ReferenceFrame";

        EditorGUILayout.HelpBox(
            "같은 오브젝트에 GeometryReference를 여러 개 추가하고, " +
            "각 컴포넌트마다 서로 다른 Frame Name을 지정하면 " +
            "독립된 ReferenceFrame을 생성할 수 있습니다. " +
            "생성 후 Transform Rotation은 Unity Inspector에서 수동 보정해도 됩니다.",
            MessageType.Info
        );

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(
            !reference.hasSelectionA ||
            !reference.hasSelectionB))
        {
            if (GUILayout.Button("Create / Update Reference Frame"))
            {
                CreateOrUpdateReferenceFrame();
            }
        }

        if (GUI.changed)
        {
            EditorUtility.SetDirty(reference);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSelectionInfo()
    {
        EditorGUILayout.LabelField("Surface A", EditorStyles.boldLabel);

        if (reference.hasSelectionA)
        {
            EditorGUILayout.LabelField(
                "Mesh",
                reference.selectedMeshFilterA != null
                    ? reference.selectedMeshFilterA.name
                    : "None"
            );

            EditorGUILayout.LabelField(
                "Seed Triangle",
                reference.seedTriangleIndexA.ToString()
            );

            EditorGUILayout.LabelField(
                "Region Triangles",
                reference.regionTriangleIndicesA != null
                    ? reference.regionTriangleIndicesA.Length.ToString()
                    : "0"
            );

            EditorGUILayout.Vector3Field("World Point", reference.WorldPointA);
            EditorGUILayout.Vector3Field("World Normal", reference.WorldNormalA);
        }
        else
        {
            EditorGUILayout.LabelField("Not Selected");
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Surface B", EditorStyles.boldLabel);

        if (reference.hasSelectionB)
        {
            EditorGUILayout.LabelField(
                "Mesh",
                reference.selectedMeshFilterB != null
                    ? reference.selectedMeshFilterB.name
                    : "None"
            );

            EditorGUILayout.LabelField(
                "Seed Triangle",
                reference.seedTriangleIndexB.ToString()
            );

            EditorGUILayout.LabelField(
                "Region Triangles",
                reference.regionTriangleIndicesB != null
                    ? reference.regionTriangleIndicesB.Length.ToString()
                    : "0"
            );

            EditorGUILayout.Vector3Field("World Point", reference.WorldPointB);
            EditorGUILayout.Vector3Field("World Normal", reference.WorldNormalB);
        }
        else
        {
            EditorGUILayout.LabelField("Not Selected");
        }

        if (reference.hasSelectionA && reference.hasSelectionB)
        {
            EditorGUILayout.Space();
            EditorGUILayout.Vector3Field("Calculated World Axis", reference.WorldAxis);
        }
    }

    private void CreateOrUpdateReferenceFrame()
    {
        if (!reference.hasSelectionA || !reference.hasSelectionB)
        {
            Debug.LogWarning("Surface A와 Surface B를 먼저 선택해야 합니다.");
            return;
        }

        Vector3 normalA = reference.WorldNormalA.normalized;
        Vector3 normalB = reference.WorldNormalB.normalized;

        Vector3 axis = Vector3.Cross(normalA, normalB);

        if (axis.sqrMagnitude < 0.000001f)
        {
            Debug.LogWarning(
                "Surface A와 Surface B가 서로 평행하거나 거의 평행하여 " +
                "Reference Frame을 만들 수 없습니다."
            );
            return;
        }

        axis.Normalize();

        float dA = Vector3.Dot(normalA, reference.WorldPointA);
        float dB = Vector3.Dot(normalB, reference.WorldPointB);

        Vector3 origin =
        (
            dA * Vector3.Cross(normalB, axis)
            +
            dB * Vector3.Cross(axis, normalA)
        )
        / axis.sqrMagnitude;

        Quaternion baseRotation =
            Quaternion.LookRotation(normalB, normalA);

        Quaternion rotation = baseRotation;

        string frameName =
            string.IsNullOrWhiteSpace(reference.referenceFrameName)
                ? "ReferenceFrame"
                : reference.referenceFrameName.Trim();

        Transform existingFrame =
            reference.transform.Find(frameName);

        Transform frame;

        if (existingFrame == null)
        {
            GameObject frameObject =
                new GameObject(frameName);

            Undo.RegisterCreatedObjectUndo(
                frameObject,
                "Create Reference Frame"
            );

            frame = frameObject.transform;
            frame.SetParent(reference.transform, true);
        }
        else
        {
            frame = existingFrame;
            Undo.RecordObject(frame, "Update Reference Frame");
        }

        frame.position = origin;
        frame.rotation = rotation;
        frame.localScale = Vector3.one;

        EditorUtility.SetDirty(frame);

        Selection.activeGameObject = frame.gameObject;
        SceneView.RepaintAll();

        Debug.Log(
            $"ReferenceFrame created/updated. " +
            $"Name = {frameName}, " +
            $"Position = {frame.position}, " +
            $"X Axis = {frame.right}"
        );
    }

    private void HandleSceneViewGUI(SceneView sceneView)
    {
        DrawStoredRegion(
            reference.selectedMeshFilterA,
            reference.regionTriangleIndicesA,
            reference.hasSelectionA
        );

        DrawStoredRegion(
            reference.selectedMeshFilterB,
            reference.regionTriangleIndicesB,
            reference.hasSelectionB
        );

        DrawReferenceNormal(
            reference.WorldPointA,
            reference.WorldNormalA,
            reference.hasSelectionA
        );

        DrawReferenceNormal(
            reference.WorldPointB,
            reference.WorldNormalB,
            reference.hasSelectionB
        );

        if (reference.hasSelectionA && reference.hasSelectionB)
        {
            DrawCalculatedAxis(reference.WorldPointA, reference.WorldAxis);
        }

        if (selectionMode == SelectionMode.None)
            return;

        Event e = Event.current;

        HandleUtility.AddDefaultControl(
            GUIUtility.GetControlID(FocusType.Passive)
        );

        if (
            e.type == EventType.MouseDown &&
            e.button == 0 &&
            !e.alt
        )
        {
            Ray worldRay =
                HandleUtility.GUIPointToWorldRay(e.mousePosition);

            if (
                FindClosestTriangle(
                    worldRay,
                    out MeshFilter meshFilter,
                    out int triangleIndex,
                    out Vector3 localHitPoint
                )
            )
            {
                SelectPlanarRegion(
                    meshFilter,
                    triangleIndex,
                    localHitPoint
                );

                selectionMode = SelectionMode.None;

                e.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }
    }

    private bool FindClosestTriangle(
        Ray worldRay,
        out MeshFilter bestMeshFilter,
        out int bestTriangleIndex,
        out Vector3 bestLocalHitPoint
    )
    {
        bestMeshFilter = null;
        bestTriangleIndex = -1;
        bestLocalHitPoint = Vector3.zero;

        float bestWorldDistance = float.MaxValue;

        MeshFilter[] meshFilters =
            reference.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter meshFilter in meshFilters)
        {
            if (
                meshFilter == null ||
                meshFilter.sharedMesh == null
            )
            {
                continue;
            }

            Mesh mesh = meshFilter.sharedMesh;

            if (!mesh.isReadable)
                continue;

            Renderer renderer =
                meshFilter.GetComponent<Renderer>();

            if (
                renderer != null &&
                !renderer.bounds.IntersectRay(worldRay)
            )
            {
                continue;
            }

            Matrix4x4 worldToLocal =
                meshFilter.transform.worldToLocalMatrix;

            Vector3 localOrigin =
                worldToLocal.MultiplyPoint(worldRay.origin);

            Vector3 localDirection =
                worldToLocal.MultiplyVector(worldRay.direction).normalized;

            Ray localRay =
                new Ray(localOrigin, localDirection);

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 v0 = vertices[triangles[i]];
                Vector3 v1 = vertices[triangles[i + 1]];
                Vector3 v2 = vertices[triangles[i + 2]];

                if (
                    !RayTriangleIntersection(
                        localRay,
                        v0,
                        v1,
                        v2,
                        out float distance
                    )
                )
                {
                    continue;
                }

                Vector3 localHit =
                    localRay.GetPoint(distance);

                Vector3 worldHit =
                    meshFilter.transform.TransformPoint(localHit);

                float worldDistance =
                    Vector3.Distance(worldRay.origin, worldHit);

                if (worldDistance < bestWorldDistance)
                {
                    bestWorldDistance = worldDistance;
                    bestMeshFilter = meshFilter;
                    bestTriangleIndex = i / 3;
                    bestLocalHitPoint = localHit;
                }
            }
        }

        return bestMeshFilter != null;
    }

    private void SelectPlanarRegion(
        MeshFilter meshFilter,
        int seedTriangleIndex,
        Vector3 localHitPoint
    )
    {
        Mesh mesh = meshFilter.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        int seedStart = seedTriangleIndex * 3;

        if (
            seedStart < 0 ||
            seedStart + 2 >= triangles.Length
        )
        {
            return;
        }

        Vector3 seedV0 = vertices[triangles[seedStart]];
        Vector3 seedV1 = vertices[triangles[seedStart + 1]];
        Vector3 seedV2 = vertices[triangles[seedStart + 2]];

        Vector3 seedNormal =
            CalculateTriangleNormal(seedV0, seedV1, seedV2);

        if (seedNormal.sqrMagnitude < 0.000001f)
            return;

        Plane seedPlane =
            new Plane(seedNormal, seedV0);

        HashSet<int> planarCandidates =
            new HashSet<int>();

        int triangleCount = triangles.Length / 3;

        for (
            int triangleIndex = 0;
            triangleIndex < triangleCount;
            triangleIndex++
        )
        {
            int start = triangleIndex * 3;

            Vector3 v0 = vertices[triangles[start]];
            Vector3 v1 = vertices[triangles[start + 1]];
            Vector3 v2 = vertices[triangles[start + 2]];

            Vector3 normal =
                CalculateTriangleNormal(v0, v1, v2);

            if (normal.sqrMagnitude < 0.000001f)
                continue;

            float angle =
                Vector3.Angle(seedNormal, normal);

            if (angle > normalAngleTolerance)
                continue;

            float d0 =
                Mathf.Abs(seedPlane.GetDistanceToPoint(v0));

            float d1 =
                Mathf.Abs(seedPlane.GetDistanceToPoint(v1));

            float d2 =
                Mathf.Abs(seedPlane.GetDistanceToPoint(v2));

            if (
                d0 > planeDistanceTolerance ||
                d1 > planeDistanceTolerance ||
                d2 > planeDistanceTolerance
            )
            {
                continue;
            }

            planarCandidates.Add(triangleIndex);
        }

        Dictionary<EdgeKey, List<int>> edgeToTriangles =
            new Dictionary<EdgeKey, List<int>>();

        foreach (int triangleIndex in planarCandidates)
        {
            int start = triangleIndex * 3;

            Vector3 v0 = vertices[triangles[start]];
            Vector3 v1 = vertices[triangles[start + 1]];
            Vector3 v2 = vertices[triangles[start + 2]];

            AddEdge(
                edgeToTriangles,
                new EdgeKey(v0, v1),
                triangleIndex
            );

            AddEdge(
                edgeToTriangles,
                new EdgeKey(v1, v2),
                triangleIndex
            );

            AddEdge(
                edgeToTriangles,
                new EdgeKey(v2, v0),
                triangleIndex
            );
        }

        HashSet<int> region =
            new HashSet<int>();

        Queue<int> queue =
            new Queue<int>();

        region.Add(seedTriangleIndex);
        queue.Enqueue(seedTriangleIndex);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int start = current * 3;

            Vector3 v0 = vertices[triangles[start]];
            Vector3 v1 = vertices[triangles[start + 1]];
            Vector3 v2 = vertices[triangles[start + 2]];

            ExpandAcrossEdge(
                new EdgeKey(v0, v1),
                edgeToTriangles,
                planarCandidates,
                region,
                queue
            );

            ExpandAcrossEdge(
                new EdgeKey(v1, v2),
                edgeToTriangles,
                planarCandidates,
                region,
                queue
            );

            ExpandAcrossEdge(
                new EdgeKey(v2, v0),
                edgeToTriangles,
                planarCandidates,
                region,
                queue
            );
        }

        Vector3 accumulatedNormal = Vector3.zero;
        Vector3 accumulatedPoint = Vector3.zero;
        float accumulatedArea = 0.0f;

        foreach (int triangleIndex in region)
        {
            int start = triangleIndex * 3;

            Vector3 v0 = vertices[triangles[start]];
            Vector3 v1 = vertices[triangles[start + 1]];
            Vector3 v2 = vertices[triangles[start + 2]];

            Vector3 cross =
                Vector3.Cross(v1 - v0, v2 - v0);

            float doubleArea = cross.magnitude;

            if (doubleArea < 0.0000001f)
                continue;

            Vector3 triangleNormal = cross.normalized;

            Vector3 centroid =
                (v0 + v1 + v2) / 3.0f;

            float area = doubleArea * 0.5f;

            accumulatedNormal +=
                triangleNormal * area;

            accumulatedPoint +=
                centroid * area;

            accumulatedArea += area;
        }

        Vector3 representativeNormal = seedNormal;
        Vector3 representativePoint = localHitPoint;

        if (accumulatedArea > 0.0000001f)
        {
            representativePoint =
                accumulatedPoint / accumulatedArea;

            representativeNormal =
                accumulatedNormal.normalized;
        }

        Undo.RecordObject(
            reference,
            "Select Planar Surface"
        );

        int[] regionArray =
            new int[region.Count];

        region.CopyTo(regionArray);
        Array.Sort(regionArray);

        if (selectionMode == SelectionMode.SurfaceA)
        {
            reference.selectedMeshFilterA = meshFilter;
            reference.seedTriangleIndexA = seedTriangleIndex;
            reference.regionTriangleIndicesA = regionArray;
            reference.localPointA = representativePoint;
            reference.localNormalA = representativeNormal;
            reference.hasSelectionA = true;
        }
        else if (selectionMode == SelectionMode.SurfaceB)
        {
            reference.selectedMeshFilterB = meshFilter;
            reference.seedTriangleIndexB = seedTriangleIndex;
            reference.regionTriangleIndicesB = regionArray;
            reference.localPointB = representativePoint;
            reference.localNormalB = representativeNormal;
            reference.hasSelectionB = true;
        }

        EditorUtility.SetDirty(reference);

        Debug.Log(
            $"Planar region selected: " +
            $"{region.Count} triangles, " +
            $"Mesh = {meshFilter.name}"
        );
    }

    private static Vector3 CalculateTriangleNormal(
        Vector3 v0,
        Vector3 v1,
        Vector3 v2
    )
    {
        Vector3 cross =
            Vector3.Cross(v1 - v0, v2 - v0);

        if (cross.sqrMagnitude < 0.0000001f)
            return Vector3.zero;

        return cross.normalized;
    }

    private static bool RayTriangleIntersection(
        Ray ray,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        out float distance
    )
    {
        distance = 0.0f;

        const float epsilon = 0.0000001f;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;

        Vector3 pVector =
            Vector3.Cross(ray.direction, edge2);

        float determinant =
            Vector3.Dot(edge1, pVector);

        if (Mathf.Abs(determinant) < epsilon)
            return false;

        float inverseDeterminant =
            1.0f / determinant;

        Vector3 tVector =
            ray.origin - v0;

        float u =
            Vector3.Dot(tVector, pVector) *
            inverseDeterminant;

        if (u < 0.0f || u > 1.0f)
            return false;

        Vector3 qVector =
            Vector3.Cross(tVector, edge1);

        float v =
            Vector3.Dot(ray.direction, qVector) *
            inverseDeterminant;

        if (v < 0.0f || u + v > 1.0f)
            return false;

        float t =
            Vector3.Dot(edge2, qVector) *
            inverseDeterminant;

        if (t <= epsilon)
            return false;

        distance = t;
        return true;
    }

    private static void AddEdge(
        Dictionary<EdgeKey, List<int>> dictionary,
        EdgeKey edge,
        int triangleIndex
    )
    {
        if (
            !dictionary.TryGetValue(
                edge,
                out List<int> triangles
            )
        )
        {
            triangles = new List<int>();
            dictionary.Add(edge, triangles);
        }

        triangles.Add(triangleIndex);
    }

    private static void ExpandAcrossEdge(
        EdgeKey edge,
        Dictionary<EdgeKey, List<int>> edgeToTriangles,
        HashSet<int> planarCandidates,
        HashSet<int> region,
        Queue<int> queue
    )
    {
        if (
            !edgeToTriangles.TryGetValue(
                edge,
                out List<int> neighbors
            )
        )
        {
            return;
        }

        foreach (int neighbor in neighbors)
        {
            if (!planarCandidates.Contains(neighbor))
                continue;

            if (region.Add(neighbor))
                queue.Enqueue(neighbor);
        }
    }

    private void DrawStoredRegion(
        MeshFilter meshFilter,
        int[] triangleIndices,
        bool valid
    )
    {
        if (
            !valid ||
            meshFilter == null ||
            triangleIndices == null ||
            triangleIndices.Length == 0
        )
        {
            return;
        }

        Mesh mesh = meshFilter.sharedMesh;

        if (mesh == null || !mesh.isReadable)
            return;

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        Matrix4x4 localToWorld =
            meshFilter.transform.localToWorldMatrix;

        Handles.color =
            new Color(
                1.0f,
                0.65f,
                0.0f,
                0.15f
            );

        foreach (int triangleIndex in triangleIndices)
        {
            int start = triangleIndex * 3;

            if (
                start < 0 ||
                start + 2 >= triangles.Length
            )
            {
                continue;
            }

            Vector3 v0 =
                localToWorld.MultiplyPoint3x4(
                    vertices[triangles[start]]
                );

            Vector3 v1 =
                localToWorld.MultiplyPoint3x4(
                    vertices[triangles[start + 1]]
                );

            Vector3 v2 =
                localToWorld.MultiplyPoint3x4(
                    vertices[triangles[start + 2]]
                );

            Handles.DrawAAConvexPolygon(v0, v1, v2);
        }
    }

    private static void DrawReferenceNormal(
        Vector3 worldPoint,
        Vector3 worldNormal,
        bool valid
    )
    {
        if (
            !valid ||
            worldNormal.sqrMagnitude < 0.000001f
        )
        {
            return;
        }

        float size =
            HandleUtility.GetHandleSize(worldPoint) *
            0.35f;

        Handles.ArrowHandleCap(
            0,
            worldPoint,
            Quaternion.LookRotation(worldNormal),
            size,
            EventType.Repaint
        );
    }

    private static void DrawCalculatedAxis(
        Vector3 worldPoint,
        Vector3 worldAxis
    )
    {
        if (worldAxis.sqrMagnitude < 0.000001f)
            return;

        float size =
            HandleUtility.GetHandleSize(worldPoint) *
            0.5f;

        Handles.ArrowHandleCap(
            0,
            worldPoint,
            Quaternion.LookRotation(worldAxis),
            size,
            EventType.Repaint
        );
    }

    private struct EdgeKey :
        IEquatable<EdgeKey>
    {
        private readonly QuantizedVector3 a;
        private readonly QuantizedVector3 b;

        public EdgeKey(
            Vector3 p0,
            Vector3 p1
        )
        {
            QuantizedVector3 q0 =
                new QuantizedVector3(p0);

            QuantizedVector3 q1 =
                new QuantizedVector3(p1);

            if (q0.CompareTo(q1) <= 0)
            {
                a = q0;
                b = q1;
            }
            else
            {
                a = q1;
                b = q0;
            }
        }

        public bool Equals(EdgeKey other)
        {
            return a.Equals(other.a) &&
                   b.Equals(other.b);
        }

        public override bool Equals(object obj)
        {
            return obj is EdgeKey other &&
                   Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return
                    (a.GetHashCode() * 397) ^
                    b.GetHashCode();
            }
        }
    }

    private struct QuantizedVector3 :
        IEquatable<QuantizedVector3>,
        IComparable<QuantizedVector3>
    {
        private const float Quantization =
            1000000.0f;

        private readonly int x;
        private readonly int y;
        private readonly int z;

        public QuantizedVector3(Vector3 value)
        {
            x = Mathf.RoundToInt(
                value.x * Quantization
            );

            y = Mathf.RoundToInt(
                value.y * Quantization
            );

            z = Mathf.RoundToInt(
                value.z * Quantization
            );
        }

        public int CompareTo(
            QuantizedVector3 other
        )
        {
            int result =
                x.CompareTo(other.x);

            if (result != 0)
                return result;

            result =
                y.CompareTo(other.y);

            if (result != 0)
                return result;

            return z.CompareTo(other.z);
        }

        public bool Equals(
            QuantizedVector3 other
        )
        {
            return
                x == other.x &&
                y == other.y &&
                z == other.z;
        }

        public override bool Equals(
            object obj
        )
        {
            return
                obj is QuantizedVector3 other &&
                Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = x;
                hash = (hash * 397) ^ y;
                hash = (hash * 397) ^ z;
                return hash;
            }
        }
    }
}
