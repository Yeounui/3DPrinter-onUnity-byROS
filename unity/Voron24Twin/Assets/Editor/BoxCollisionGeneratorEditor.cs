using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(BoxCollisionGenerator))]
public class BoxCollisionGeneratorEditor : Editor
{
    private BoxCollisionGenerator g;
    private int activeSlot = -1;
    private readonly bool[] valid = new bool[6];
    private readonly int[] seedTriangle = new int[6];
    private readonly MeshFilter[] meshes = new MeshFilter[6];
    private readonly Vector3[] points = new Vector3[6];
    private readonly Vector3[] normals = new Vector3[6];
    private readonly Vector3[] boundsMin = new Vector3[6];
    private readonly Vector3[] boundsMax = new Vector3[6];
    private readonly HashSet<int>[] cachedRegions = new HashSet<int>[6];

    private void OnEnable() { g = (BoxCollisionGenerator)target; SceneView.duringSceneGui += HandleSceneViewGUI; }
    private void OnDisable() { SceneView.duringSceneGui -= HandleSceneViewGUI; }

    public override void OnInspectorGUI()
    {
        g.baseLink = (Transform)EditorGUILayout.ObjectField("Base Link", g.baseLink, typeof(Transform), true);
        g.collisionParent = (Transform)EditorGUILayout.ObjectField("Collision Parent", g.collisionParent, typeof(Transform), true);
        g.selectionRoot = (Transform)EditorGUILayout.ObjectField("Selection Root", g.selectionRoot, typeof(Transform), true);
        g.collisionNamePrefix = EditorGUILayout.TextField("Name Prefix", g.collisionNamePrefix);
        g.persistNameCounter = EditorGUILayout.Toggle("Persist Name Counter", g.persistNameCounter);
        if (g.persistNameCounter) g.nextIndex = Mathf.Max(0, EditorGUILayout.IntField("Next Index", g.nextIndex));
        EditorGUILayout.LabelField("Next Name Preview", NextName());
        g.commonOffset = Mathf.Max(0, EditorGUILayout.FloatField("Common Offset", g.commonOffset));
        g.useIndividualAxisOffsets = EditorGUILayout.Toggle("Use Individual Axis Offsets", g.useIndividualAxisOffsets);
        using (new EditorGUI.DisabledScope(!g.useIndividualAxisOffsets))
            g.individualAxisOffsets = EditorGUILayout.Vector3Field("X/Y/Z Offsets", g.individualAxisOffsets);
        g.axisAlignment = EditorGUILayout.Slider("Axis Alignment", g.axisAlignment, .1f, .999f);
        g.minimumThickness = Mathf.Max(.000001f, EditorGUILayout.FloatField("Minimum Thickness", g.minimumThickness));
        EditorGUI.BeginChangeCheck();
        bool expandPlanarSelection = EditorGUILayout.Toggle(
            "Expand Planar Selection",
            g.expandPlanarSelection
        );
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(g, "Change planar selection mode");
            g.expandPlanarSelection = expandPlanarSelection;
            ClearAllSlots();
            EditorUtility.SetDirty(g);
        }
        g.showSelectedFaces = EditorGUILayout.Toggle("Show Selected Faces", g.showSelectedFaces);
        g.showSelectedFaceOutline = EditorGUILayout.Toggle("Show Face As Outline", g.showSelectedFaceOutline);
        EditorGUILayout.HelpBox(
            g.expandPlanarSelection
                ? "Expanded mode: each click uses the connected coplanar triangle region."
                : "Single triangle mode: each click uses and displays only one triangle.",
            MessageType.Info
        );
        EditorGUILayout.HelpBox("6 valid faces are required. X/Y/Z must each contain two faces.", MessageType.Info);
        for (int i = 0; i < 6; i++)
        {
            EditorGUILayout.LabelField("Face " + (i + 1), valid[i] ? meshes[i].name : "Not selected");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Face " + (i + 1))) { activeSlot = i; SceneView.RepaintAll(); }
                if (GUILayout.Button("Clear", GUILayout.Width(55))) ClearSlot(i);
            }
        }
        if (activeSlot >= 0) EditorGUILayout.HelpBox("현재 선택 대상: Face " + (activeSlot + 1), MessageType.Info);
        if (GUILayout.Button("Clear All")) ClearAllSlots();
        using (new EditorGUI.DisabledScope(!AllValid()))
        {
            if (GUILayout.Button("Add " + NextName())) AddBox();
        }
    }

    private void ClearSlot(int i) { valid[i] = false; cachedRegions[i] = null; if (activeSlot == i) activeSlot = -1; Repaint(); SceneView.RepaintAll(); }
    private void ClearAllSlots()
    {
        for (int i = 0; i < 6; i++)
        {
            valid[i] = false;
            cachedRegions[i] = null;
        }

        activeSlot = -1;
        Repaint();
        SceneView.RepaintAll();
    }
    private bool AllValid() { for (int i = 0; i < 6; i++) if (!valid[i]) return false; return true; }
    private string NextName()
    {
        string prefix = string.IsNullOrWhiteSpace(g.collisionNamePrefix) ? "box" : g.collisionNamePrefix;
        int i = g.persistNameCounter ? g.nextIndex : 0;
        while (Exists(prefix + "_" + i)) i++;
        return prefix + "_" + i;
    }
    private bool Exists(string name)
    {
        Transform parent = g.collisionParent ? g.collisionParent : g.transform;
        Transform generated = parent.Find("Generated");
        return generated && generated.Find(name);
    }

    private void HandleSceneViewGUI(SceneView view)
    {
        if (g.showSelectedFaces && Event.current.type == EventType.Repaint) DrawSelectedFaces();
        if (activeSlot < 0) return;
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        Event e = Event.current;
        if (e.type != EventType.MouseDown || e.button != 0 || e.alt) return;
        if (!FindClosestTriangle(HandleUtility.GUIPointToWorldRay(e.mousePosition), out MeshFilter mesh, out int triangle, out Vector3 hit, out Vector3 normal))
        { Debug.LogError("BoxCollisionGenerator: 선택 실패. 읽을 수 있는 MeshFilter가 없거나 메쉬를 클릭하지 않았습니다.", g); return; }
        Transform baseLink = ResolveBaseLink();
        if (!baseLink) { Debug.LogError("BoxCollisionGenerator: Base Link을 지정하거나 이름이 base_link인 오브젝트를 만들어야 합니다.", g); return; }
        if (AxisDot(normal, baseLink) < g.axisAlignment)
        { Debug.LogError("BoxCollisionGenerator: 선택 면이 base_link의 X/Y/Z 축과 충분히 평행하지 않습니다.", g); return; }
        Undo.RecordObject(g, "Select collision face");
        meshes[activeSlot] = mesh; seedTriangle[activeSlot] = triangle; points[activeSlot] = hit; normals[activeSlot] = normal;
        cachedRegions[activeSlot] = g.expandPlanarSelection
            ? CollectRegion(mesh, triangle, normal)
            : CreateSingleTriangleRegion(triangle);
        CalculateBounds(mesh, cachedRegions[activeSlot], baseLink, out boundsMin[activeSlot], out boundsMax[activeSlot]);
        valid[activeSlot] = true; Repaint(); SceneView.RepaintAll();
        LogSelectedAxisBounds(activeSlot, mesh, triangle, baseLink);
    }

    private static HashSet<int> CreateSingleTriangleRegion(int triangle)
    {
        return new HashSet<int> { triangle };
    }

    private void LogSelectedAxisBounds(int slot, MeshFilter mesh, int triangle, Transform baseLink)
    {
        float xAlignment = Mathf.Abs(Vector3.Dot(normals[slot], baseLink.right));
        float yAlignment = Mathf.Abs(Vector3.Dot(normals[slot], baseLink.up));
        float zAlignment = Mathf.Abs(Vector3.Dot(normals[slot], baseLink.forward));

        string axis;
        float min;
        float max;
        float alignment;

        if (xAlignment >= yAlignment && xAlignment >= zAlignment)
        {
            axis = "X";
            min = boundsMin[slot].x;
            max = boundsMax[slot].x;
            alignment = xAlignment;
        }
        else if (yAlignment >= zAlignment)
        {
            axis = "Y";
            min = boundsMin[slot].y;
            max = boundsMax[slot].y;
            alignment = yAlignment;
        }
        else
        {
            axis = "Z";
            min = boundsMin[slot].z;
            max = boundsMax[slot].z;
            alignment = zAlignment;
        }

        Debug.Log(
            $"BoxCollisionGenerator: Face {slot + 1} | Axis={axis} | " +
            $"min={min:F6}, max={max:F6}, span={max - min:F6} | " +
            $"alignment={alignment:F4} | Mesh={mesh.name} | " +
            $"seedTriangle={triangle} | regionTriangles={cachedRegions[slot].Count}",
            g
        );
    }

    private void DrawSelectedFaces()
    {
        for (int i = 0; i < 6; i++)
        {
            if (!valid[i] || !meshes[i] || cachedRegions[i] == null) continue;
            Transform b = ResolveBaseLink(); if (!b) continue;
            float x = Mathf.Abs(Vector3.Dot(normals[i], b.right));
            float y = Mathf.Abs(Vector3.Dot(normals[i], b.up));
            float z = Mathf.Abs(Vector3.Dot(normals[i], b.forward));
            Color color = x >= y && x >= z ? new Color(1, .15f, .15f, .28f) : y >= z ? new Color(.15f, 1, .15f, .28f) : new Color(.15f, .35f, 1, .28f);
            Mesh mesh = meshes[i].sharedMesh; int[] t = mesh.triangles;
            if (g.showSelectedFaceOutline)
            {
                DrawRegionOutline(meshes[i], cachedRegions[i], normals[i], color);
                continue;
            }
            foreach (int tri in cachedRegions[i])
            {
                int s = tri * 3; Vector3 a = meshes[i].transform.TransformPoint(mesh.vertices[t[s]]); Vector3 c = meshes[i].transform.TransformPoint(mesh.vertices[t[s + 2]]); Vector3 d = meshes[i].transform.TransformPoint(mesh.vertices[t[s + 1]]);
                Handles.color = color; Handles.DrawAAConvexPolygon(a, d, c); Handles.color = new Color(color.r, color.g, color.b, .9f); Handles.DrawLine(a, d); Handles.DrawLine(d, c); Handles.DrawLine(c, a);
            }
        }
    }

    private void DrawRegionOutline(MeshFilter filter, HashSet<int> region, Vector3 normal, Color color)
    {
        Mesh mesh = filter.sharedMesh;
        int[] triangles = mesh.triangles;
        Vector3 planeNormal = normal.normalized;
        Vector3 reference = Mathf.Abs(Vector3.Dot(planeNormal, Vector3.up)) < .9f ? Vector3.up : Vector3.right;
        Vector3 axisU = Vector3.Cross(planeNormal, reference).normalized;
        Vector3 axisV = Vector3.Cross(planeNormal, axisU).normalized;
        int firstTriangle = 0;
        foreach (int id in region) { firstTriangle = id; break; }
        Vector3 origin = filter.transform.TransformPoint(mesh.vertices[triangles[firstTriangle * 3]]);
        List<ProjectedPoint> points = new List<ProjectedPoint>();
        HashSet<Vector3> unique = new HashSet<Vector3>();
        foreach (int id in region)
        {
            int start = id * 3;
            for (int j = 0; j < 3; j++)
            {
                Vector3 world = filter.transform.TransformPoint(mesh.vertices[triangles[start + j]]);
                if (!unique.Add(world)) continue;
                Vector3 delta = world - origin;
                points.Add(new ProjectedPoint(Vector2.Dot(delta, axisU), Vector2.Dot(delta, axisV), world));
            }
        }
        if (points.Count < 3) return;
        points.Sort((a, b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        List<ProjectedPoint> hull = new List<ProjectedPoint>();
        foreach (ProjectedPoint point in points) { while (hull.Count >= 2 && Cross(hull[hull.Count - 1], hull[hull.Count - 2], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        int lowerCount = hull.Count;
        for (int i = points.Count - 2; i >= 0; i--) { ProjectedPoint point = points[i]; while (hull.Count > lowerCount && Cross(hull[hull.Count - 1], hull[hull.Count - 2], point) <= 0) hull.RemoveAt(hull.Count - 1); hull.Add(point); }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        Vector3[] polygon = new Vector3[hull.Count]; for (int i = 0; i < hull.Count; i++) polygon[i] = hull[i].world;
        Handles.color = color; Handles.DrawAAConvexPolygon(polygon);
        Handles.color = new Color(color.r, color.g, color.b, .9f); for (int i = 0; i < polygon.Length; i++) Handles.DrawLine(polygon[i], polygon[(i + 1) % polygon.Length]);
    }

    private struct ProjectedPoint
    {
        public float x, y; public Vector3 world;
        public ProjectedPoint(float x, float y, Vector3 world) { this.x = x; this.y = y; this.world = world; }
    }
    private static float Cross(ProjectedPoint a, ProjectedPoint b, ProjectedPoint c) { return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x); }

    private Transform ResolveBaseLink() { return g.baseLink ? g.baseLink : GameObject.Find("base_link")?.transform; }
    private float AxisDot(Vector3 n, Transform b) { return Mathf.Max(Mathf.Abs(Vector3.Dot(n, b.right)), Mathf.Abs(Vector3.Dot(n, b.up)), Mathf.Abs(Vector3.Dot(n, b.forward))); }

    private void AddBox()
    {
        Transform b = ResolveBaseLink();
        Transform parent = g.collisionParent ? g.collisionParent : g.transform;
        if (!b)
        {
            Debug.LogError("BoxCollisionGenerator: 박스 생성 실패. Base Link이 없습니다.", g);
            return;
        }

        int[] counts = { 0, 0, 0 };
        float[] axisMin = { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        float[] axisMax = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

        for (int i = 0; i < 6; i++)
        {
            int axis = DominantAxis(normals[i], b);
            counts[axis]++;

            if (axis == 0)
            {
                axisMin[axis] = Mathf.Min(axisMin[axis], boundsMin[i].x);
                axisMax[axis] = Mathf.Max(axisMax[axis], boundsMax[i].x);
            }
            else if (axis == 1)
            {
                axisMin[axis] = Mathf.Min(axisMin[axis], boundsMin[i].y);
                axisMax[axis] = Mathf.Max(axisMax[axis], boundsMax[i].y);
            }
            else
            {
                axisMin[axis] = Mathf.Min(axisMin[axis], boundsMin[i].z);
                axisMax[axis] = Mathf.Max(axisMax[axis], boundsMax[i].z);
            }
        }

        if (counts[0] != 2 || counts[1] != 2 || counts[2] != 2)
        {
            Debug.LogError(
                $"BoxCollisionGenerator: 박스 생성 실패. " +
                $"X:{counts[0]} Y:{counts[1]} Z:{counts[2]}; 각 축에 정확히 2개가 필요합니다.",
                g
            );
            return;
        }

        Vector3 offset = g.useIndividualAxisOffsets
            ? Vector3.Max(Vector3.zero, g.individualAxisOffsets)
            : Vector3.one * Mathf.Max(0f, g.commonOffset);

        Vector3 min = new Vector3(axisMin[0], axisMin[1], axisMin[2]) - offset;
        Vector3 max = new Vector3(axisMax[0], axisMax[1], axisMax[2]) + offset;
        Vector3 size = max - min;

        if (size.x < g.minimumThickness || size.y < g.minimumThickness || size.z < g.minimumThickness)
        {
            Debug.LogError(
                $"BoxCollisionGenerator: 박스 생성 실패. 계산 크기 {size} 중 " +
                $"Minimum Thickness {g.minimumThickness}보다 작은 축이 있습니다.",
                g
            );
            return;
        }

        Transform generated = parent.Find("Generated");
        if (!generated) { GameObject go = new GameObject("Generated"); Undo.RegisterCreatedObjectUndo(go, "Create Generated"); generated = go.transform; generated.SetParent(parent, false); }
        string name = NextName();
        GameObject box = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(box, "Create collision box");
        box.transform.SetParent(generated, false);
        box.transform.SetPositionAndRotation(b.TransformPoint((min + max) * .5f), b.rotation);
        box.transform.localScale = Vector3.one;

        BoxCollider collider = box.AddComponent<BoxCollider>();
        collider.center = Vector3.zero;
        collider.size = size;

        BoxCollisionData data = box.AddComponent<BoxCollisionData>();
        data.generatedName = name;

        if (g.persistNameCounter) g.nextIndex++;
        EditorUtility.SetDirty(g);

        Debug.Log(
            $"BoxCollisionGenerator: {name} 생성 완료 | " +
            $"Min={min}, Max={max}, Center={(min + max) * .5f}, Size={size} " +
            $"(base_link 기준)",
            box
        );

        ClearAllSlots();
    }

    private static int DominantAxis(Vector3 normal, Transform baseLink)
    {
        float x = Mathf.Abs(Vector3.Dot(normal, baseLink.right));
        float y = Mathf.Abs(Vector3.Dot(normal, baseLink.up));
        float z = Mathf.Abs(Vector3.Dot(normal, baseLink.forward));

        if (x >= y && x >= z) return 0;
        return y >= z ? 1 : 2;
    }

    private HashSet<int> CollectRegion(MeshFilter f, int seed, Vector3 worldNormal)
    {
        Mesh m = f.sharedMesh; int[] t = m.triangles; Vector3[] v = m.vertices; int start = seed * 3; var candidates = new HashSet<int>(); var edges = new Dictionary<string, List<int>>(); Vector3 origin = v[t[start]]; Vector3 normal = worldNormal.normalized;
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]], cross = Vector3.Cross(b - a, c - a); if (cross.sqrMagnitude < 1e-12f) continue; Vector3 n = cross.normalized;
            if (Mathf.Abs(Vector3.Dot(n, normal)) < .999f || Mathf.Abs(Vector3.Dot(normal, a - origin)) > .0001f || Mathf.Abs(Vector3.Dot(normal, b - origin)) > .0001f || Mathf.Abs(Vector3.Dot(normal, c - origin)) > .0001f) continue;
            int id = i / 3; candidates.Add(id); AddEdge(edges, t[i], t[i + 1], id); AddEdge(edges, t[i + 1], t[i + 2], id); AddEdge(edges, t[i + 2], t[i], id);
        }
        var region = new HashSet<int>(); var queue = new Queue<int>(); region.Add(seed); queue.Enqueue(seed);
        while (queue.Count > 0) { int id = queue.Dequeue(), s = id * 3; ExpandEdge(edges, t[s], t[s + 1], candidates, region, queue); ExpandEdge(edges, t[s + 1], t[s + 2], candidates, region, queue); ExpandEdge(edges, t[s + 2], t[s], candidates, region, queue); }
        return region;
    }
    private void AddEdge(Dictionary<string, List<int>> map, int a, int b, int tri) { string key = a < b ? a + ":" + b : b + ":" + a; if (!map.TryGetValue(key, out List<int> list)) map[key] = list = new List<int>(); list.Add(tri); }
    private void ExpandEdge(Dictionary<string, List<int>> map, int a, int b, HashSet<int> candidates, HashSet<int> region, Queue<int> queue) { string key = a < b ? a + ":" + b : b + ":" + a; if (!map.TryGetValue(key, out List<int> list)) return; foreach (int id in list) if (candidates.Contains(id) && region.Add(id)) queue.Enqueue(id); }
    private void CalculateBounds(MeshFilter f, HashSet<int> region, Transform b, out Vector3 min, out Vector3 max) { min = Vector3.one * float.PositiveInfinity; max = Vector3.one * float.NegativeInfinity; int[] t = f.sharedMesh.triangles; Vector3[] v = f.sharedMesh.vertices; foreach (int id in region) for (int j = 0; j < 3; j++) { Vector3 p = b.InverseTransformPoint(f.transform.TransformPoint(v[t[id * 3 + j]])); min = Vector3.Min(min, p); max = Vector3.Max(max, p); } }
    private bool FindClosestTriangle(Ray ray, out MeshFilter best, out int index, out Vector3 hit, out Vector3 normal)
    {
        best = null; index = -1; hit = Vector3.zero; normal = Vector3.zero; float closest = float.MaxValue;
        foreach (MeshFilter f in (g.selectionRoot ? g.selectionRoot : g.transform).GetComponentsInChildren<MeshFilter>(true)) { Mesh m = f.sharedMesh; if (!m || !m.isReadable) continue; Renderer r = f.GetComponent<Renderer>(); if (r && !r.bounds.IntersectRay(ray)) continue; Matrix4x4 w2l = f.transform.worldToLocalMatrix; Ray local = new Ray(w2l.MultiplyPoint(ray.origin), w2l.MultiplyVector(ray.direction).normalized); Vector3[] v = m.vertices; int[] t = m.triangles;
            for (int i = 0; i < t.Length; i += 3) { if (!RayTriangle(local, v[t[i]], v[t[i + 1]], v[t[i + 2]], out float d)) continue; Vector3 wp = f.transform.TransformPoint(local.GetPoint(d)); float wd = Vector3.Distance(ray.origin, wp); if (wd >= closest) continue; closest = wd; best = f; index = i / 3; hit = wp; normal = Vector3.Cross(f.transform.TransformDirection(v[t[i + 1]] - v[t[i]]), f.transform.TransformDirection(v[t[i + 2]] - v[t[i]])).normalized; }
        } return best != null;
    }
    private bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float d) { d = 0; Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, p); if (Mathf.Abs(det) < 1e-7f) return false; Vector3 s = ray.origin - a; float u = Vector3.Dot(s, p) / det; if (u < 0 || u > 1) return false; Vector3 q = Vector3.Cross(s, e1); float v = Vector3.Dot(ray.direction, q) / det; if (v < 0 || u + v > 1) return false; d = Vector3.Dot(e2, q) / det; return d >= 0; }
}
