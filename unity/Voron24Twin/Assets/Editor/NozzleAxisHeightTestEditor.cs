using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(NozzleAxisHeightTest))]
public class NozzleAxisHeightTestEditor : Editor
{
    private static readonly Color Group1Color = new Color(1f, 0.35f, 0.05f, 1f);
    private static readonly Color Group2Color = new Color(0.1f, 1f, 0.25f, 1f);

    private enum SelectionSlot
    {
        None,
        A,
        B,
        Group1Slot1,
        Group1Slot2,
        Group1Slot3,
        Group2Slot1,
        Group2Slot2,
        Group2Slot3
    }

    private NozzleAxisHeightTest test;
    private SelectionSlot activeSlot;

    private void OnEnable()
    {
        test = (NozzleAxisHeightTest)target;
        test.EnsureSideSelectionCapacity();
        SceneView.duringSceneGui += HandleSceneViewGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= HandleSceneViewGUI;
    }

    public override void OnInspectorGUI()
    {
        test.baseLink = (Transform)EditorGUILayout.ObjectField(
            "Base Link",
            test.baseLink,
            typeof(Transform),
            true
        );
        test.selectionRoot = (Transform)EditorGUILayout.ObjectField(
            "Selection Root",
            test.selectionRoot,
            typeof(Transform),
            true
        );
        test.axisAlignment = EditorGUILayout.Slider(
            "Axis Alignment",
            test.axisAlignment,
            0.1f,
            0.999f
        );

        EditorGUILayout.Space();
        DrawSelectionRow("Select Boundary A", SelectionSlot.A);
        DrawSelectionRow("Select Boundary B", SelectionSlot.B);

        EditorGUILayout.Space();
        DrawColoredGroupHeader("Unrestricted Mesh Group 1", Group1Color);
        for (int i = 0; i < 3; i++)
            DrawSelectionRow($"Select Group 1 Mesh {i + 1}", GroupSlot(0, i));

        EditorGUILayout.Space();
        DrawColoredGroupHeader("Unrestricted Mesh Group 2", Group2Color);
        for (int i = 0; i < 3; i++)
            DrawSelectionRow($"Select Group 2 Mesh {i + 1}", GroupSlot(1, i));

        DrawGroupClassification();

        if (activeSlot != SelectionSlot.None)
        {
            EditorGUILayout.HelpBox(
                $"현재 선택 대상: {SlotLabel(activeSlot)}. Scene 뷰에서 면을 클릭하세요.",
                MessageType.Info
            );
        }

        if (GUILayout.Button("Clear Both"))
        {
            Undo.RecordObject(test, "Clear nozzle axis-height selections");
            test.meshFilterA = null;
            test.triangleIndexA = -1;
            test.meshFilterB = null;
            test.triangleIndexB = -1;
            activeSlot = SelectionSlot.None;
            EditorUtility.SetDirty(test);
            SceneView.RepaintAll();
        }

        if (GUILayout.Button("Clear Both Mesh Groups"))
        {
            Undo.RecordObject(test, "Clear unrestricted mesh groups");
            for (int i = 0; i < 6; i++)
            {
                test.sideMeshFilters[i] = null;
                test.sideTriangleIndices[i] = -1;
            }
            if (IsGroupSlot(activeSlot))
                activeSlot = SelectionSlot.None;
            EditorUtility.SetDirty(test);
            SceneView.RepaintAll();
        }

        using (new EditorGUI.DisabledScope(!test.HasSelectionA || !test.HasSelectionB))
        {
            if (GUILayout.Button("Calculate Axis And Height"))
                CalculateAndLog();
        }

        if (GUI.changed)
            EditorUtility.SetDirty(test);
    }

    private void DrawSelectionRow(string label, SelectionSlot slot)
    {
        GetSelection(slot, out MeshFilter mesh, out int triangle);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(label))
            {
                activeSlot = slot;
                SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField(
                mesh != null && triangle >= 0
                    ? $"{mesh.name} / tri {triangle}"
                    : "Not selected"
            );
        }
    }

    private static void DrawColoredGroupHeader(string label, Color color)
    {
        Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
        const float swatchSize = 12f;
        Rect swatch = new Rect(
            row.x,
            row.y + (row.height - swatchSize) * 0.5f,
            swatchSize,
            swatchSize
        );
        EditorGUI.DrawRect(swatch, color);
        Rect text = new Rect(
            swatch.xMax + 6f,
            row.y,
            row.width - swatchSize - 6f,
            row.height
        );
        EditorGUI.LabelField(text, label, EditorStyles.boldLabel);
    }

    private void HandleSceneViewGUI(SceneView sceneView)
    {
        if (Event.current.type == EventType.Repaint)
            DrawSelections();

        if (activeSlot == SelectionSlot.None)
            return;

        HandleUtility.AddDefaultControl(
            GUIUtility.GetControlID(FocusType.Passive)
        );

        Event current = Event.current;
        if (current.type != EventType.MouseDown || current.button != 0 || current.alt)
            return;

        Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
        bool groupSelection = IsGroupSlot(activeSlot);
        Transform root = test.selectionRoot ? test.selectionRoot : test.transform;
        MeshFilter meshFilter;
        int triangleIndex;
        bool picked = groupSelection
            ? UnrestrictedMeshTriangleSelector.TryPick(root, ray, out meshFilter, out triangleIndex)
            : FindClosestTriangle(ray, out meshFilter, out triangleIndex);
        if (!picked)
        {
            if (!groupSelection)
                Debug.LogError(
                    "NozzleAxisHeightTest: 읽을 수 있는 메쉬 삼각형을 선택하지 못했습니다.",
                    test
                );
            return;
        }

        SelectionSlot completedSlot = activeSlot;
        Undo.RecordObject(test, groupSelection
            ? "Select unrestricted test mesh"
            : "Select nozzle boundary");
        if (activeSlot == SelectionSlot.A)
        {
            test.meshFilterA = meshFilter;
            test.triangleIndexA = triangleIndex;
        }
        else if (activeSlot == SelectionSlot.B)
        {
            test.meshFilterB = meshFilter;
            test.triangleIndexB = triangleIndex;
        }
        else
        {
            int index = GroupArrayIndex(activeSlot);
            test.sideMeshFilters[index] = meshFilter;
            test.sideTriangleIndices[index] = triangleIndex;
        }

        activeSlot = SelectionSlot.None;
        EditorUtility.SetDirty(test);
        current.Use();
        Repaint();
        SceneView.RepaintAll();

        if (!IsGroupSlot(completedSlot) && test.HasSelectionA && test.HasSelectionB)
            CalculateAndLog();
        else if (IsGroupSlot(completedSlot) &&
                 TryClassifyGroups(
                     out int topGroup,
                     out int bottomGroup,
                     out float group1Spread,
                     out float group2Spread,
                     out int classificationAxis))
        {
            Debug.Log(
                $"NozzleAxisHeightTest: Top=Group {topGroup} | " +
                $"Bottom=Group {bottomGroup} | Axis={AxisName(classificationAxis)} | " +
                $"Group1Spread={group1Spread:F8} | Group2Spread={group2Spread:F8}",
                test
            );
        }
    }

    private void CalculateAndLog()
    {
        Transform baseLink = test.baseLink
            ? test.baseLink
            : GameObject.Find("base_link")?.transform;

        if (!baseLink)
        {
            Debug.LogError("NozzleAxisHeightTest: Base Link을 지정해야 합니다.", test);
            return;
        }

        if (!TryGetTriangleData(
                test.meshFilterA,
                test.triangleIndexA,
                baseLink,
                out Vector3[] pointsA,
                out Vector3 normalA) ||
            !TryGetTriangleData(
                test.meshFilterB,
                test.triangleIndexB,
                baseLink,
                out Vector3[] pointsB,
                out Vector3 normalB))
        {
            Debug.LogError("NozzleAxisHeightTest: 저장된 삼각형 선택이 유효하지 않습니다.", test);
            return;
        }

        int axisA = DominantAxis(normalA);
        int axisB = DominantAxis(normalB);
        float alignmentA = AxisAlignment(normalA, axisA);
        float alignmentB = AxisAlignment(normalB, axisB);

        if (axisA != axisB)
        {
            Debug.LogError(
                $"NozzleAxisHeightTest: 두 면이 서로 다른 축을 가리킵니다. " +
                $"A={AxisName(axisA)}, B={AxisName(axisB)}",
                test
            );
            return;
        }

        if (alignmentA < test.axisAlignment || alignmentB < test.axisAlignment)
        {
            Debug.LogError(
                $"NozzleAxisHeightTest: 축 정렬도가 기준보다 낮습니다. " +
                $"A={alignmentA:F4}, B={alignmentB:F4}, Required={test.axisAlignment:F4}",
                test
            );
            return;
        }

        GetAxisRange(pointsA, axisA, out float minA, out float maxA);
        GetAxisRange(pointsB, axisA, out float minB, out float maxB);

        float axisMinimum = Mathf.Min(minA, minB);
        float axisMaximum = Mathf.Max(maxA, maxB);
        float height = axisMaximum - axisMinimum;

        Debug.Log(
            $"NozzleAxisHeightTest: Axis={AxisName(axisA)} | " +
            $"Min={axisMinimum:F6} | Max={axisMaximum:F6} | " +
            $"Height={height:F6} | " +
            $"BoundaryA=[{minA:F6}, {maxA:F6}] | " +
            $"BoundaryB=[{minB:F6}, {maxB:F6}] | " +
            $"AlignmentA={alignmentA:F4} | AlignmentB={alignmentB:F4} " +
            $"(base_link 기준)",
            test
        );
    }

    private static bool TryGetTriangleData(
        MeshFilter meshFilter,
        int triangleIndex,
        Transform baseLink,
        out Vector3[] points,
        out Vector3 localNormal)
    {
        points = null;
        localNormal = Vector3.zero;
        if (!meshFilter || !meshFilter.sharedMesh || !meshFilter.sharedMesh.isReadable)
            return false;

        Mesh mesh = meshFilter.sharedMesh;
        int[] triangles = mesh.triangles;
        int start = triangleIndex * 3;
        if (start < 0 || start + 2 >= triangles.Length)
            return false;

        points = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            Vector3 world = meshFilter.transform.TransformPoint(
                mesh.vertices[triangles[start + i]]
            );
            points[i] = baseLink.InverseTransformPoint(world);
        }

        localNormal = Vector3.Cross(
            points[1] - points[0],
            points[2] - points[0]
        ).normalized;
        return localNormal.sqrMagnitude > 0.000001f;
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
        if (axis == 0) return Mathf.Abs(normal.x);
        if (axis == 1) return Mathf.Abs(normal.y);
        return Mathf.Abs(normal.z);
    }

    private static string AxisName(int axis)
    {
        if (axis == 0) return "X";
        if (axis == 1) return "Y";
        return "Z";
    }

    private static void GetAxisRange(
        Vector3[] points,
        int axis,
        out float minimum,
        out float maximum)
    {
        minimum = float.PositiveInfinity;
        maximum = float.NegativeInfinity;
        foreach (Vector3 point in points)
        {
            float value = axis == 0 ? point.x : axis == 1 ? point.y : point.z;
            minimum = Mathf.Min(minimum, value);
            maximum = Mathf.Max(maximum, value);
        }
    }

    private void DrawSelections()
    {
        DrawTriangle(test.meshFilterA, test.triangleIndexA, Color.yellow);
        DrawTriangle(test.meshFilterB, test.triangleIndexB, Color.cyan);

        for (int i = 0; i < 3; i++)
        {
            UnrestrictedMeshTriangleSelector.DrawTriangle(
                test.sideMeshFilters[i],
                test.sideTriangleIndices[i],
                Group1Color
            );
            UnrestrictedMeshTriangleSelector.DrawTriangle(
                test.sideMeshFilters[i + 3],
                test.sideTriangleIndices[i + 3],
                Group2Color
            );
        }

        DrawGroupCircles();
    }

    private void DrawGroupCircles()
    {
        if (!TryClassifyGroups(
                out _,
                out _,
                out _,
                out _,
                out int axis))
        {
            return;
        }

        Transform baseLink = test.baseLink
            ? test.baseLink
            : GameObject.Find("base_link")?.transform;
        if (!baseLink ||
            !TryGetTriangleData(
                test.meshFilterA,
                test.triangleIndexA,
                baseLink,
                out Vector3[] boundaryA,
                out _) ||
            !TryGetTriangleData(
                test.meshFilterB,
                test.triangleIndexB,
                baseLink,
                out Vector3[] boundaryB,
                out _) ||
            !TryGetGroupAxisMean(0, axis, baseLink, out float group1Mean) ||
            !TryGetGroupAxisMean(1, axis, baseLink, out float group2Mean))
        {
            return;
        }

        float boundaryACenter = GetAxisMean(boundaryA, axis);
        float boundaryBCenter = GetAxisMean(boundaryB, axis);
        float directDistance =
            Mathf.Abs(group1Mean - boundaryACenter) +
            Mathf.Abs(group2Mean - boundaryBCenter);
        float swappedDistance =
            Mathf.Abs(group1Mean - boundaryBCenter) +
            Mathf.Abs(group2Mean - boundaryACenter);

        float group1Boundary = directDistance <= swappedDistance
            ? boundaryACenter
            : boundaryBCenter;
        float group2Boundary = directDistance <= swappedDistance
            ? boundaryBCenter
            : boundaryACenter;

        DrawOneGroupCircle(
            0,
            axis,
            group1Boundary < group2Boundary,
            group1Boundary,
            baseLink
        );
        DrawOneGroupCircle(
            1,
            axis,
            group2Boundary < group1Boundary,
            group2Boundary,
            baseLink
        );
    }

    private void DrawOneGroupCircle(
        int groupIndex,
        int axis,
        bool selectMinimum,
        float circleAxisPosition,
        Transform baseLink)
    {
        Vector2[] projectedExtremePoints = new Vector2[3];
        int firstSelection = groupIndex * 3;
        for (int i = 0; i < 3; i++)
        {
            int selection = firstSelection + i;
            if (!TryGetTriangleData(
                    test.sideMeshFilters[selection],
                    test.sideTriangleIndices[selection],
                    baseLink,
                    out Vector3[] points,
                    out _))
            {
                return;
            }

            Vector3 extreme = points[0];
            float extremeValue = AxisValue(extreme, axis);
            for (int pointIndex = 1; pointIndex < points.Length; pointIndex++)
            {
                float value = AxisValue(points[pointIndex], axis);
                if ((selectMinimum && value < extremeValue) ||
                    (!selectMinimum && value > extremeValue))
                {
                    extreme = points[pointIndex];
                    extremeValue = value;
                }
            }

            projectedExtremePoints[i] = ProjectPerpendicular(extreme, axis);
        }

        if (!TryGetCircumcircle(
                projectedExtremePoints[0],
                projectedExtremePoints[1],
                projectedExtremePoints[2],
                out Vector2 center,
                out float radius))
        {
            return;
        }

        const int segmentCount = 64;
        Vector3[] worldCircle = new Vector3[segmentCount + 1];
        for (int i = 0; i <= segmentCount; i++)
        {
            float angle = Mathf.PI * 2f * i / segmentCount;
            Vector2 point = center + new Vector2(
                Mathf.Cos(angle),
                Mathf.Sin(angle)
            ) * radius;
            Vector3 baseLocal = ComposeAxisPoint(axis, circleAxisPosition, point);
            worldCircle[i] = baseLink.TransformPoint(baseLocal);
        }

        Handles.color = Color.red;
        Handles.DrawAAPolyLine(3f, worldCircle);
    }

    private static bool TryGetCircumcircle(
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

        float aSquared = a.sqrMagnitude;
        float bSquared = b.sqrMagnitude;
        float cSquared = c.sqrMagnitude;
        center = new Vector2(
            (
                aSquared * (b.y - c.y) +
                bSquared * (c.y - a.y) +
                cSquared * (a.y - b.y)
            ) / determinant,
            (
                aSquared * (c.x - b.x) +
                bSquared * (a.x - c.x) +
                cSquared * (b.x - a.x)
            ) / determinant
        );
        radius = Vector2.Distance(center, a);
        return radius > 0f && !float.IsNaN(radius) && !float.IsInfinity(radius);
    }

    private static Vector3 ComposeAxisPoint(
        int axis,
        float axisPosition,
        Vector2 perpendicularPoint)
    {
        if (axis == 0)
            return new Vector3(axisPosition, perpendicularPoint.x, perpendicularPoint.y);
        if (axis == 1)
            return new Vector3(perpendicularPoint.x, axisPosition, perpendicularPoint.y);
        return new Vector3(perpendicularPoint.x, perpendicularPoint.y, axisPosition);
    }

    private bool TryGetGroupAxisMean(
        int groupIndex,
        int axis,
        Transform baseLink,
        out float mean)
    {
        mean = 0f;
        int pointCount = 0;
        int firstSelection = groupIndex * 3;
        for (int i = 0; i < 3; i++)
        {
            int selection = firstSelection + i;
            if (!TryGetTriangleData(
                    test.sideMeshFilters[selection],
                    test.sideTriangleIndices[selection],
                    baseLink,
                    out Vector3[] points,
                    out _))
            {
                return false;
            }

            foreach (Vector3 point in points)
            {
                mean += AxisValue(point, axis);
                pointCount++;
            }
        }

        if (pointCount == 0)
            return false;
        mean /= pointCount;
        return true;
    }

    private static float GetAxisMean(Vector3[] points, int axis)
    {
        float sum = 0f;
        foreach (Vector3 point in points)
            sum += AxisValue(point, axis);
        return sum / points.Length;
    }

    private static float AxisValue(Vector3 point, int axis)
    {
        if (axis == 0) return point.x;
        if (axis == 1) return point.y;
        return point.z;
    }

    private void DrawGroupClassification()
    {
        if (!TryClassifyGroups(
                out int topGroup,
                out int bottomGroup,
                out float group1Spread,
                out float group2Spread,
                out int axis))
        {
            EditorGUILayout.HelpBox(
                "경계면 2개와 각 그룹의 메쉬 3개가 유효하게 선택되면 Top/Bottom을 자동 판정합니다.",
                MessageType.None
            );
            return;
        }

        EditorGUILayout.HelpBox(
            $"자동 판정: Top = Group {topGroup}, Bottom = Group {bottomGroup}\n" +
            $"Axis = {AxisName(axis)}, Group 1 Spread = {group1Spread:F8}, " +
            $"Group 2 Spread = {group2Spread:F8}",
            MessageType.Info
        );
    }

    private bool TryClassifyGroups(
        out int topGroup,
        out int bottomGroup,
        out float group1Spread,
        out float group2Spread,
        out int axis)
    {
        topGroup = 0;
        bottomGroup = 0;
        group1Spread = 0f;
        group2Spread = 0f;
        axis = -1;

        Transform baseLink = test.baseLink
            ? test.baseLink
            : GameObject.Find("base_link")?.transform;
        if (!baseLink || !test.HasSelectionA || !test.HasSelectionB)
            return false;

        if (!TryGetTriangleData(
                test.meshFilterA,
                test.triangleIndexA,
                baseLink,
                out _,
                out Vector3 normalA) ||
            !TryGetTriangleData(
                test.meshFilterB,
                test.triangleIndexB,
                baseLink,
                out _,
                out Vector3 normalB))
        {
            return false;
        }

        int axisA = DominantAxis(normalA);
        int axisB = DominantAxis(normalB);
        if (axisA != axisB ||
            AxisAlignment(normalA, axisA) < test.axisAlignment ||
            AxisAlignment(normalB, axisB) < test.axisAlignment)
        {
            return false;
        }

        axis = axisA;
        if (!TryCalculateGroupSpread(0, axis, baseLink, out group1Spread) ||
            !TryCalculateGroupSpread(1, axis, baseLink, out group2Spread) ||
            Mathf.Approximately(group1Spread, group2Spread))
        {
            return false;
        }

        if (group1Spread < group2Spread)
        {
            topGroup = 1;
            bottomGroup = 2;
        }
        else
        {
            topGroup = 2;
            bottomGroup = 1;
        }

        return true;
    }

    private bool TryCalculateGroupSpread(
        int groupIndex,
        int axis,
        Transform baseLink,
        out float spread)
    {
        spread = 0f;
        List<Vector2> projectedPoints = new List<Vector2>(9);
        int firstSelection = groupIndex * 3;
        for (int i = 0; i < 3; i++)
        {
            int selection = firstSelection + i;
            if (!TryGetTriangleData(
                    test.sideMeshFilters[selection],
                    test.sideTriangleIndices[selection],
                    baseLink,
                    out Vector3[] points,
                    out _))
            {
                return false;
            }

            foreach (Vector3 point in points)
                AddUnique(projectedPoints, ProjectPerpendicular(point, axis));
        }

        if (projectedPoints.Count < 3)
            return false;

        Vector2 center = Vector2.zero;
        foreach (Vector2 point in projectedPoints)
            center += point;
        center /= projectedPoints.Count;

        foreach (Vector2 point in projectedPoints)
            spread += (point - center).sqrMagnitude;
        spread /= projectedPoints.Count;
        return true;
    }

    private static Vector2 ProjectPerpendicular(Vector3 point, int axis)
    {
        if (axis == 0) return new Vector2(point.y, point.z);
        if (axis == 1) return new Vector2(point.x, point.z);
        return new Vector2(point.x, point.y);
    }

    private static void AddUnique(List<Vector2> points, Vector2 candidate)
    {
        const float duplicateDistanceSquared = 0.0000000001f;
        foreach (Vector2 point in points)
        {
            if ((point - candidate).sqrMagnitude <= duplicateDistanceSquared)
                return;
        }
        points.Add(candidate);
    }

    private void GetSelection(
        SelectionSlot slot,
        out MeshFilter meshFilter,
        out int triangleIndex)
    {
        if (slot == SelectionSlot.A)
        {
            meshFilter = test.meshFilterA;
            triangleIndex = test.triangleIndexA;
            return;
        }

        if (slot == SelectionSlot.B)
        {
            meshFilter = test.meshFilterB;
            triangleIndex = test.triangleIndexB;
            return;
        }

        int index = GroupArrayIndex(slot);
        meshFilter = test.sideMeshFilters[index];
        triangleIndex = test.sideTriangleIndices[index];
    }

    private static SelectionSlot GroupSlot(int group, int slot)
    {
        return (SelectionSlot)((int)SelectionSlot.Group1Slot1 + group * 3 + slot);
    }

    private static bool IsGroupSlot(SelectionSlot slot)
    {
        return slot >= SelectionSlot.Group1Slot1 && slot <= SelectionSlot.Group2Slot3;
    }

    private static int GroupArrayIndex(SelectionSlot slot)
    {
        return (int)slot - (int)SelectionSlot.Group1Slot1;
    }

    private static string SlotLabel(SelectionSlot slot)
    {
        if (slot == SelectionSlot.A) return "Boundary A";
        if (slot == SelectionSlot.B) return "Boundary B";
        int index = GroupArrayIndex(slot);
        return $"Group {index / 3 + 1} Mesh {index % 3 + 1}";
    }

    private static void DrawTriangle(
        MeshFilter meshFilter,
        int triangleIndex,
        Color color)
    {
        if (!meshFilter || !meshFilter.sharedMesh)
            return;

        Mesh mesh = meshFilter.sharedMesh;
        int[] triangles = mesh.triangles;
        int start = triangleIndex * 3;
        if (start < 0 || start + 2 >= triangles.Length)
            return;

        Vector3 a = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start]]);
        Vector3 b = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start + 1]]);
        Vector3 c = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start + 2]]);
        Color fill = color;
        fill.a = 0.3f;
        Handles.color = fill;
        Handles.DrawAAConvexPolygon(a, b, c);
        Handles.color = color;
        Handles.DrawLine(a, b);
        Handles.DrawLine(b, c);
        Handles.DrawLine(c, a);
    }

    private bool FindClosestTriangle(
        Ray worldRay,
        out MeshFilter bestMeshFilter,
        out int bestTriangleIndex)
    {
        bestMeshFilter = null;
        bestTriangleIndex = -1;
        float bestDistance = float.MaxValue;
        Transform root = test.selectionRoot ? test.selectionRoot : test.transform;

        foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = meshFilter.sharedMesh;
            if (!mesh || !mesh.isReadable)
                continue;

            Renderer renderer = meshFilter.GetComponent<Renderer>();
            if (renderer && !renderer.bounds.IntersectRay(worldRay))
                continue;

            Matrix4x4 worldToLocal = meshFilter.transform.worldToLocalMatrix;
            Ray localRay = new Ray(
                worldToLocal.MultiplyPoint(worldRay.origin),
                worldToLocal.MultiplyVector(worldRay.direction).normalized
            );
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (!RayTriangle(
                        localRay,
                        vertices[triangles[i]],
                        vertices[triangles[i + 1]],
                        vertices[triangles[i + 2]],
                        out float distance))
                {
                    continue;
                }

                Vector3 worldHit = meshFilter.transform.TransformPoint(
                    localRay.GetPoint(distance)
                );
                float worldDistance = Vector3.Distance(worldRay.origin, worldHit);
                if (worldDistance >= bestDistance)
                    continue;

                bestDistance = worldDistance;
                bestMeshFilter = meshFilter;
                bestTriangleIndex = i / 3;
            }
        }

        return bestMeshFilter != null;
    }

    private static bool RayTriangle(
        Ray ray,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        distance = 0f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(ray.direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (Mathf.Abs(determinant) < 0.0000001f) return false;
        float inverse = 1f / determinant;
        Vector3 t = ray.origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(ray.direction, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f;
    }
}
