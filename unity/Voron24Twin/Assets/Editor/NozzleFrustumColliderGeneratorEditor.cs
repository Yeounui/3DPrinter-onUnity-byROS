using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(NozzleFrustumColliderGenerator))]
public class NozzleFrustumColliderGeneratorEditor : Editor
{
    private const int SideCount = 8;
    private const float MinimumRadius = 0.000001f;
    private NozzleFrustumColliderGenerator generator;

    private struct Geometry
    {
        public int axis;
        public float axialMinimum;
        public float axialMaximum;
        public Vector2 minimumCenter;
        public Vector2 maximumCenter;
        public float minimumCircleRadius;
        public float maximumCircleRadius;
        public int topGroup;
        public int bottomGroup;
        public bool minimumFallback;
        public bool maximumFallback;
    }

    private void OnEnable()
    {
        generator = (NozzleFrustumColliderGenerator)target;
        SceneView.duringSceneGui += HandleSceneViewGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= HandleSceneViewGUI;
    }

    public override void OnInspectorGUI()
    {
        generator.selectionSource = (NozzleAxisHeightTest)EditorGUILayout.ObjectField(
            "Selection Source",
            generator.selectionSource,
            typeof(NozzleAxisHeightTest),
            true
        );
        generator.baseLink = (Transform)EditorGUILayout.ObjectField(
            "Base Link Override",
            generator.baseLink,
            typeof(Transform),
            true
        );
        generator.collisionParent = (Transform)EditorGUILayout.ObjectField(
            "Collision Parent",
            generator.collisionParent,
            typeof(Transform),
            true
        );

        EditorGUILayout.Space();
        generator.radialOffset = Mathf.Max(
            0f,
            EditorGUILayout.FloatField("Radial Offset", generator.radialOffset)
        );
        generator.axialOffset = Mathf.Max(
            0f,
            EditorGUILayout.FloatField("Axial Offset", generator.axialOffset)
        );

        EditorGUILayout.Space();
        generator.namePrefix = EditorGUILayout.TextField("Name Prefix", generator.namePrefix);
        generator.nextIndex = Mathf.Max(
            0,
            EditorGUILayout.IntField("Next Index", generator.nextIndex)
        );
        generator.showGeneratedMesh = EditorGUILayout.Toggle(
            "Show Generated Mesh",
            generator.showGeneratedMesh
        );
        EditorGUILayout.LabelField("Next Name", NextName());

        bool ready = TryCalculateGeometry(out Geometry geometry, out string status);
        if (ready)
        {
            EditorGUILayout.HelpBox(
                $"Ready | Axis={AxisName(geometry.axis)} | " +
                $"Top=Group {geometry.topGroup} | Bottom=Group {geometry.bottomGroup}\n" +
                $"Minimum Radius={geometry.minimumCircleRadius:F6}, " +
                $"Maximum Radius={geometry.maximumCircleRadius:F6}",
                MessageType.Info
            );
        }
        else
        {
            EditorGUILayout.HelpBox(status, MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Create " + NextName()))
                CreateCollider(geometry);
        }

        if (GUI.changed)
        {
            EditorUtility.SetDirty(generator);
            SceneView.RepaintAll();
        }
    }

    private void HandleSceneViewGUI(SceneView sceneView)
    {
        if (Event.current.type != EventType.Repaint ||
            !TryCalculateGeometry(out Geometry geometry, out _))
        {
            return;
        }

        Transform baseLink = ResolveBaseLink();
        if (!baseLink)
            return;

        float lowAxial = geometry.axialMinimum - generator.axialOffset;
        float highAxial = geometry.axialMaximum + generator.axialOffset;
        float lowRadius = CorrectOctagonRadius(geometry.minimumCircleRadius);
        float highRadius = CorrectOctagonRadius(geometry.maximumCircleRadius);
        DrawOctagon(baseLink, geometry.axis, lowAxial, geometry.minimumCenter, lowRadius);
        DrawOctagon(baseLink, geometry.axis, highAxial, geometry.maximumCenter, highRadius);
    }

    private bool TryCalculateGeometry(out Geometry geometry, out string status)
    {
        geometry = new Geometry();
        NozzleAxisHeightTest source = generator.selectionSource;
        if (!source)
        {
            status = "NozzleAxisHeightTest를 Selection Source에 지정하세요.";
            return false;
        }

        source.EnsureSideSelectionCapacity();
        Transform baseLink = ResolveBaseLink();
        if (!baseLink)
        {
            status = "Base Link을 지정하세요.";
            return false;
        }

        if (!TryGetTriangleData(
                source.meshFilterA,
                source.triangleIndexA,
                baseLink,
                out Vector3[] boundaryA,
                out Vector3 normalA) ||
            !TryGetTriangleData(
                source.meshFilterB,
                source.triangleIndexB,
                baseLink,
                out Vector3[] boundaryB,
                out Vector3 normalB))
        {
            status = "유효한 기준면 2개가 필요합니다.";
            return false;
        }

        int axisA = DominantAxis(normalA);
        int axisB = DominantAxis(normalB);
        if (axisA != axisB)
        {
            status = "두 기준면이 서로 다른 기준축을 가리킵니다.";
            return false;
        }
        if (AxisAlignment(normalA, axisA) < source.axisAlignment ||
            AxisAlignment(normalB, axisB) < source.axisAlignment)
        {
            status = "기준면의 축 정렬도가 Selection Source 기준보다 낮습니다.";
            return false;
        }

        geometry.axis = axisA;
        if (!TryGetGroupPoints(source, 0, baseLink, out Vector3[][] group1Triangles) ||
            !TryGetGroupPoints(source, 1, baseLink, out Vector3[][] group2Triangles))
        {
            status = "각 그룹에 유효한 메쉬 삼각형 3개가 필요합니다.";
            return false;
        }

        float spread1 = CalculateSpread(group1Triangles, geometry.axis);
        float spread2 = CalculateSpread(group2Triangles, geometry.axis);
        if (spread1 <= spread2)
        {
            geometry.topGroup = 1;
            geometry.bottomGroup = 2;
        }
        else
        {
            geometry.topGroup = 2;
            geometry.bottomGroup = 1;
        }

        float boundaryAPosition = AxisMean(boundaryA, geometry.axis);
        float boundaryBPosition = AxisMean(boundaryB, geometry.axis);
        float group1Mean = AxisMean(group1Triangles, geometry.axis);
        float group2Mean = AxisMean(group2Triangles, geometry.axis);
        float direct =
            Mathf.Abs(group1Mean - boundaryAPosition) +
            Mathf.Abs(group2Mean - boundaryBPosition);
        float swapped =
            Mathf.Abs(group1Mean - boundaryBPosition) +
            Mathf.Abs(group2Mean - boundaryAPosition);
        float group1Boundary = direct <= swapped ? boundaryAPosition : boundaryBPosition;
        float group2Boundary = direct <= swapped ? boundaryBPosition : boundaryAPosition;

        Vector3[][] minimumGroup;
        Vector3[][] maximumGroup;
        if (group1Boundary <= group2Boundary)
        {
            geometry.axialMinimum = group1Boundary;
            geometry.axialMaximum = group2Boundary;
            minimumGroup = group1Triangles;
            maximumGroup = group2Triangles;
        }
        else
        {
            geometry.axialMinimum = group2Boundary;
            geometry.axialMaximum = group1Boundary;
            minimumGroup = group2Triangles;
            maximumGroup = group1Triangles;
        }

        Vector2[] minimumPoints = SelectExtremePoints(minimumGroup, geometry.axis, true);
        Vector2[] maximumPoints = SelectExtremePoints(maximumGroup, geometry.axis, false);
        CalculateCircleAlways(
            minimumPoints,
            out geometry.minimumCenter,
            out geometry.minimumCircleRadius,
            out geometry.minimumFallback
        );
        CalculateCircleAlways(
            maximumPoints,
            out geometry.maximumCenter,
            out geometry.maximumCircleRadius,
            out geometry.maximumFallback
        );

        status = "Ready";
        return true;
    }

    private void CreateCollider(Geometry geometry)
    {
        Transform baseLink = ResolveBaseLink();
        if (!baseLink)
            return;

        float lowAxial = geometry.axialMinimum - generator.axialOffset;
        float highAxial = geometry.axialMaximum + generator.axialOffset;
        if (highAxial - lowAxial < 0.000001f)
        {
            float middle = (lowAxial + highAxial) * 0.5f;
            lowAxial = middle - 0.0000005f;
            highAxial = middle + 0.0000005f;
        }

        float lowOctagonRadius = CorrectOctagonRadius(geometry.minimumCircleRadius);
        float highOctagonRadius = CorrectOctagonRadius(geometry.maximumCircleRadius);
        Vector2 objectRadialCenter =
            (geometry.minimumCenter + geometry.maximumCenter) * 0.5f;
        float objectAxialCenter = (lowAxial + highAxial) * 0.5f;
        Vector3 objectCenter = Compose(
            geometry.axis,
            objectAxialCenter,
            objectRadialCenter
        );

        Mesh mesh = BuildMesh(
            geometry.axis,
            lowAxial,
            highAxial,
            geometry.minimumCenter,
            geometry.maximumCenter,
            lowOctagonRadius,
            highOctagonRadius,
            objectCenter
        );
        string generatedName = NextName();
        SaveMeshAsset(mesh, generatedName);
        GameObject result = CreateObject(
            generatedName,
            mesh,
            baseLink,
            objectCenter
        );

        NozzleFrustumCollisionData data = result.AddComponent<NozzleFrustumCollisionData>();
        data.generatedName = generatedName;
        data.symmetryAxis = (FrustumAxis)geometry.axis;
        data.axialMinimum = lowAxial;
        data.axialMaximum = highAxial;
        data.minimumCenter = geometry.minimumCenter;
        data.maximumCenter = geometry.maximumCenter;
        data.minimumCircleRadius = geometry.minimumCircleRadius;
        data.maximumCircleRadius = geometry.maximumCircleRadius;
        data.minimumOctagonVertexRadius = lowOctagonRadius;
        data.maximumOctagonVertexRadius = highOctagonRadius;
        data.topGroup = geometry.topGroup;
        data.bottomGroup = geometry.bottomGroup;
        data.minimumCircleUsedFallback = geometry.minimumFallback;
        data.maximumCircleUsedFallback = geometry.maximumFallback;
        data.generatedMesh = mesh;

        generator.nextIndex++;
        EditorUtility.SetDirty(generator);
        EditorUtility.SetDirty(data);
        Selection.activeGameObject = result;
        Debug.Log(
            $"NozzleFrustumColliderGenerator: {generatedName} 생성 완료 | " +
            $"Axis={AxisName(geometry.axis)} | " +
            $"Top=Group {geometry.topGroup} | Bottom=Group {geometry.bottomGroup} | " +
            $"Height={highAxial - lowAxial:F6} | " +
            $"MinimumRadius={geometry.minimumCircleRadius:F6} | " +
            $"MaximumRadius={geometry.maximumCircleRadius:F6}",
            result
        );
    }

    private float CorrectOctagonRadius(float circleRadius)
    {
        float expandedCircleRadius = Mathf.Max(
            MinimumRadius,
            circleRadius + generator.radialOffset
        );
        return expandedCircleRadius / Mathf.Cos(Mathf.PI / SideCount);
    }

    private static Mesh BuildMesh(
        int axis,
        float lowAxial,
        float highAxial,
        Vector2 lowCenter,
        Vector2 highCenter,
        float lowRadius,
        float highRadius,
        Vector3 objectCenter)
    {
        Vector3[] vertices = new Vector3[SideCount * 2 + 2];
        for (int i = 0; i < SideCount; i++)
        {
            float angle = Mathf.PI * 2f * i / SideCount;
            Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            vertices[i] = Compose(axis, lowAxial, lowCenter + direction * lowRadius) - objectCenter;
            vertices[SideCount + i] = Compose(axis, highAxial, highCenter + direction * highRadius) - objectCenter;
        }

        int lowCenterIndex = SideCount * 2;
        int highCenterIndex = lowCenterIndex + 1;
        vertices[lowCenterIndex] = Compose(axis, lowAxial, lowCenter) - objectCenter;
        vertices[highCenterIndex] = Compose(axis, highAxial, highCenter) - objectCenter;

        int[] triangles = new int[SideCount * 12];
        int cursor = 0;
        for (int i = 0; i < SideCount; i++)
        {
            int next = (i + 1) % SideCount;
            triangles[cursor++] = i;
            triangles[cursor++] = SideCount + next;
            triangles[cursor++] = SideCount + i;
            triangles[cursor++] = i;
            triangles[cursor++] = next;
            triangles[cursor++] = SideCount + next;
            triangles[cursor++] = lowCenterIndex;
            triangles[cursor++] = next;
            triangles[cursor++] = i;
            triangles[cursor++] = highCenterIndex;
            triangles[cursor++] = SideCount + i;
            triangles[cursor++] = SideCount + next;
        }

        EnsureOutwardWinding(vertices, triangles);
        Mesh mesh = new Mesh { name = "ObliqueOctagonalNozzleCollider" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void EnsureOutwardWinding(Vector3[] vertices, int[] triangles)
    {
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            Vector3 triangleCenter = (a + b + c) / 3f;
            if (Vector3.Dot(normal, triangleCenter) >= 0f)
                continue;
            int swap = triangles[i + 1];
            triangles[i + 1] = triangles[i + 2];
            triangles[i + 2] = swap;
        }
    }

    private GameObject CreateObject(
        string generatedName,
        Mesh mesh,
        Transform baseLink,
        Vector3 objectCenter)
    {
        Transform parent = generator.collisionParent
            ? generator.collisionParent
            : generator.transform;
        Transform generated = parent.Find("Generated");
        if (!generated)
        {
            GameObject container = new GameObject("Generated");
            Undo.RegisterCreatedObjectUndo(container, "Create generated collision container");
            generated = container.transform;
            generated.SetParent(parent, false);
        }

        GameObject result = new GameObject(generatedName);
        Undo.RegisterCreatedObjectUndo(result, "Create oblique nozzle collider");
        result.transform.SetParent(generated, false);
        result.transform.SetPositionAndRotation(
            baseLink.TransformPoint(objectCenter),
            baseLink.rotation
        );
        result.transform.localScale = Vector3.one;

        MeshFilter filter = result.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        if (generator.showGeneratedMesh)
        {
            MeshRenderer renderer = result.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = AssetDatabase.GetBuiltinExtraResource<Material>(
                "Default-Material.mat"
            );
        }

        MeshCollider collider = result.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;
        collider.convex = true;
        return result;
    }

    private static void SaveMeshAsset(Mesh mesh, string generatedName)
    {
        const string folder = "Assets/GeneratedCollisionMeshes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "GeneratedCollisionMeshes");
        string safeName = string.IsNullOrWhiteSpace(generatedName)
            ? "nozzle"
            : generatedName.Replace('/', '_');
        string path = AssetDatabase.GenerateUniqueAssetPath(
            folder + "/" + safeName + ".asset"
        );
        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();
    }

    private void DrawOctagon(
        Transform baseLink,
        int axis,
        float axial,
        Vector2 center,
        float radius)
    {
        Vector3[] points = new Vector3[SideCount + 1];
        for (int i = 0; i <= SideCount; i++)
        {
            float angle = Mathf.PI * 2f * i / SideCount;
            Vector2 radial = center + new Vector2(
                Mathf.Cos(angle),
                Mathf.Sin(angle)
            ) * radius;
            points[i] = baseLink.TransformPoint(Compose(axis, axial, radial));
        }
        Handles.color = Color.red;
        Handles.DrawAAPolyLine(3f, points);
    }

    private static bool TryGetGroupPoints(
        NozzleAxisHeightTest source,
        int group,
        Transform baseLink,
        out Vector3[][] triangles)
    {
        triangles = new Vector3[3][];
        int first = group * 3;
        for (int i = 0; i < 3; i++)
        {
            if (!TryGetTriangleData(
                    source.sideMeshFilters[first + i],
                    source.sideTriangleIndices[first + i],
                    baseLink,
                    out triangles[i],
                    out _))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryGetTriangleData(
        MeshFilter meshFilter,
        int triangleIndex,
        Transform baseLink,
        out Vector3[] points,
        out Vector3 normal)
    {
        points = null;
        normal = Vector3.zero;
        if (!meshFilter || !meshFilter.sharedMesh || !meshFilter.sharedMesh.isReadable)
            return false;
        Mesh mesh = meshFilter.sharedMesh;
        int[] triangleIndices = mesh.triangles;
        int start = triangleIndex * 3;
        if (start < 0 || start + 2 >= triangleIndices.Length)
            return false;
        points = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            Vector3 world = meshFilter.transform.TransformPoint(
                mesh.vertices[triangleIndices[start + i]]
            );
            points[i] = baseLink.InverseTransformPoint(world);
        }
        normal = Vector3.Cross(points[1] - points[0], points[2] - points[0]).normalized;
        return normal.sqrMagnitude > 0.000001f;
    }

    private static float CalculateSpread(Vector3[][] triangles, int axis)
    {
        List<Vector2> points = new List<Vector2>(9);
        foreach (Vector3[] triangle in triangles)
            foreach (Vector3 point in triangle)
                AddUnique(points, Project(point, axis));
        Vector2 center = Vector2.zero;
        foreach (Vector2 point in points)
            center += point;
        center /= Mathf.Max(1, points.Count);
        float spread = 0f;
        foreach (Vector2 point in points)
            spread += (point - center).sqrMagnitude;
        return spread / Mathf.Max(1, points.Count);
    }

    private static Vector2[] SelectExtremePoints(
        Vector3[][] triangles,
        int axis,
        bool minimum)
    {
        Vector2[] selected = new Vector2[3];
        for (int i = 0; i < 3; i++)
        {
            Vector3 extreme = triangles[i][0];
            float extremeValue = AxisValue(extreme, axis);
            for (int j = 1; j < 3; j++)
            {
                float value = AxisValue(triangles[i][j], axis);
                if ((minimum && value < extremeValue) ||
                    (!minimum && value > extremeValue))
                {
                    extreme = triangles[i][j];
                    extremeValue = value;
                }
            }
            selected[i] = Project(extreme, axis);
        }
        return selected;
    }

    private static void CalculateCircleAlways(
        Vector2[] points,
        out Vector2 center,
        out float radius,
        out bool usedFallback)
    {
        if (TryCircumcircle(points[0], points[1], points[2], out center, out radius))
        {
            usedFallback = false;
            return;
        }

        usedFallback = true;
        center = (points[0] + points[1] + points[2]) / 3f;
        radius = 0f;
        foreach (Vector2 point in points)
            radius = Mathf.Max(radius, Vector2.Distance(center, point));
        radius = Mathf.Max(radius, MinimumRadius);
    }

    private static bool TryCircumcircle(
        Vector2 a,
        Vector2 b,
        Vector2 c,
        out Vector2 center,
        out float radius)
    {
        center = Vector2.zero;
        radius = 0f;
        float determinant = 2f * (
            a.x * (b.y - c.y) +
            b.x * (c.y - a.y) +
            c.x * (a.y - b.y)
        );
        if (Mathf.Abs(determinant) <= 0.0000000001f)
            return false;
        float aa = a.sqrMagnitude;
        float bb = b.sqrMagnitude;
        float cc = c.sqrMagnitude;
        center = new Vector2(
            (aa * (b.y - c.y) + bb * (c.y - a.y) + cc * (a.y - b.y)) /
            determinant,
            (aa * (c.x - b.x) + bb * (a.x - c.x) + cc * (b.x - a.x)) /
            determinant
        );
        radius = Vector2.Distance(center, a);
        return radius > 0f && !float.IsNaN(radius) && !float.IsInfinity(radius);
    }

    private static int DominantAxis(Vector3 normal)
    {
        float x = Mathf.Abs(normal.x);
        float y = Mathf.Abs(normal.y);
        float z = Mathf.Abs(normal.z);
        if (x >= y && x >= z) return 0;
        return y >= z ? 1 : 2;
    }

    private static float AxisAlignment(Vector3 normal, int axis)
    {
        return Mathf.Abs(AxisValue(normal, axis));
    }

    private static float AxisMean(Vector3[] points, int axis)
    {
        float sum = 0f;
        foreach (Vector3 point in points)
            sum += AxisValue(point, axis);
        return sum / points.Length;
    }

    private static float AxisMean(Vector3[][] triangles, int axis)
    {
        float sum = 0f;
        int count = 0;
        foreach (Vector3[] triangle in triangles)
        {
            foreach (Vector3 point in triangle)
            {
                sum += AxisValue(point, axis);
                count++;
            }
        }
        return sum / Mathf.Max(1, count);
    }

    private static float AxisValue(Vector3 point, int axis)
    {
        if (axis == 0) return point.x;
        if (axis == 1) return point.y;
        return point.z;
    }

    private static Vector2 Project(Vector3 point, int axis)
    {
        if (axis == 0) return new Vector2(point.y, point.z);
        if (axis == 1) return new Vector2(point.x, point.z);
        return new Vector2(point.x, point.y);
    }

    private static Vector3 Compose(int axis, float axial, Vector2 radial)
    {
        if (axis == 0) return new Vector3(axial, radial.x, radial.y);
        if (axis == 1) return new Vector3(radial.x, axial, radial.y);
        return new Vector3(radial.x, radial.y, axial);
    }

    private static void AddUnique(List<Vector2> points, Vector2 candidate)
    {
        foreach (Vector2 point in points)
            if ((point - candidate).sqrMagnitude <= 0.0000000001f)
                return;
        points.Add(candidate);
    }

    private string NextName()
    {
        string prefix = string.IsNullOrWhiteSpace(generator.namePrefix)
            ? "nozzle"
            : generator.namePrefix;
        Transform parent = generator.collisionParent
            ? generator.collisionParent
            : generator.transform;
        Transform generated = parent.Find("Generated");
        int index = generator.nextIndex;
        while (generated && generated.Find(prefix + "_" + index))
            index++;
        return prefix + "_" + index;
    }

    private Transform ResolveBaseLink()
    {
        if (generator.baseLink)
            return generator.baseLink;
        if (generator.selectionSource && generator.selectionSource.baseLink)
            return generator.selectionSource.baseLink;
        return GameObject.Find("base_link")?.transform;
    }

    private static string AxisName(int axis)
    {
        if (axis == 0) return "X";
        if (axis == 1) return "Y";
        return "Z";
    }
}
