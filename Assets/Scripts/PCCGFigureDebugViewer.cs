using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class PCCGFigureDebugViewer : MonoBehaviour
{
    public enum FigureMode
    {
        Sampling,
        Filtering,
        Scoring
    }

    [Header("Target")]
    public CameraCandidateGenerator generator;

    [Header("Figure Mode")]
    public FigureMode currentMode = FigureMode.Sampling;

    [Header("Keyboard")]
    public bool enableHotkeys = true;
    public bool captureOnModeSwitch = true;

    [Header("Marker Size")]
    public float sampledSize = 0.06f;
    public float rejectedSize = 0.075f;
    public float feasibleSize = 0.085f;
    public float topCandidateSize = 0.18f;

    [Header("Screenshot")]
    public string screenshotFolderName = "PCCG_FigureCaptures";
    public int screenshotScale = 2;

    [Header("Colors")]
    public Color sampledColor = new Color(0.55f, 0.55f, 0.55f, 0.45f);
    public Color rejectedColor = new Color(1.0f, 0.15f, 0.1f, 0.75f);
    public Color feasibleColor = new Color(0.1f, 0.9f, 0.25f, 0.85f);
    public Color scoreRejectedColor = new Color(1.0f, 0.65f, 0.0f, 0.75f);
    public Color topColor = new Color(0.0f, 0.55f, 1.0f, 1.0f);
    public Color lineColor = Color.cyan;

    private readonly List<GameObject> _spawned = new List<GameObject>();

    private Material _sampledMat;
    private Material _rejectedMat;
    private Material _feasibleMat;
    private Material _scoreRejectedMat;
    private Material _topMat;
    private Material _lineMat;

    void Start()
    {
        PrepareMaterials();
    }

    void Update()
    {
        if (!enableHotkeys)
            return;

        if (Input.GetKeyDown(KeyCode.F1))
        {
            ShowSampling();
            if (captureOnModeSwitch) CaptureCurrentView("sampling");
        }
        else if (Input.GetKeyDown(KeyCode.F2))
        {
            ShowFiltering();
            if (captureOnModeSwitch) CaptureCurrentView("filtering");
        }
        else if (Input.GetKeyDown(KeyCode.F3))
        {
            ShowScoring();
            if (captureOnModeSwitch) CaptureCurrentView("scoring");
        }
        else if (Input.GetKeyDown(KeyCode.F12))
        {
            CaptureCurrentView(currentMode.ToString().ToLower());
        }
    }

    [ContextMenu("Show Sampling")]
    public void ShowSampling()
    {
        currentMode = FigureMode.Sampling;
        Rebuild();
    }

    [ContextMenu("Show Filtering")]
    public void ShowFiltering()
    {
        currentMode = FigureMode.Filtering;
        Rebuild();
    }

    [ContextMenu("Show Scoring")]
    public void ShowScoring()
    {
        currentMode = FigureMode.Scoring;
        Rebuild();
    }

    public void Rebuild()
    {
        if (generator == null)
        {
            Debug.LogError("[FigureDebug] generator is null.");
            return;
        }

        PrepareMaterials();
        Clear();

        switch (currentMode)
        {
            case FigureMode.Sampling:
                BuildSamplingView();
                break;

            case FigureMode.Filtering:
                BuildFilteringView();
                break;

            case FigureMode.Scoring:
                BuildScoringView();
                break;
        }

        Debug.Log($"[FigureDebug] {currentMode} view rebuilt.");
    }

    void BuildSamplingView()
    {
        foreach (Vector3 pos in generator.debugSampledPositions)
        {
            CreateSphere(pos, sampledSize, _sampledMat, "SampledPose");
        }

        CreateLabel(GetLabelPosition(), $"Sampling\n{generator.debugSampledPositions.Count} samples");
    }

    void BuildFilteringView()
    {
        foreach (Vector3 pos in generator.debugFeasiblePositions)
        {
            CreateSphere(pos, feasibleSize, _feasibleMat, "FeasiblePose");
        }

        CreateLabel(
            GetLabelPosition(),
            $"Filtering\n{generator.debugFeasiblePositions.Count} feasible poses"
        );
    }

    void BuildScoringView()
    {
        if (generator.topCandidates == null || generator.topCandidates.Count == 0)
        {
            CreateLabel(GetLabelPosition(), "Scoring\nNo top candidates");
            return;
        }

        for (int i = 0; i < generator.topCandidates.Count; i++)
        {
            var c = generator.topCandidates[i];

            CreateSphere(
                c.position,
                i == 0 ? topCandidateSize * 1.25f : topCandidateSize,
                _topMat,
                $"TopCandidate_{i + 1}"
            );

            CreateLookLine(c.position, c.rotation, 0.65f);
            CreateLabel(c.position + Vector3.up * 0.28f, $"C{i + 1}\n{c.totalScore:F2}");
        }

        CreateLabel(GetLabelPosition(), $"Scoring\nTop-{generator.topCandidates.Count}");
    }

    Vector3 GetLabelPosition()
    {
        if (generator != null && generator.characterRoot != null)
            return generator.characterRoot.position + Vector3.up * 2.1f;

        return Vector3.up * 2.1f;
    }

    GameObject CreateSphere(Vector3 pos, float size, Material mat, string name)
    {
        GameObject obj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        obj.name = name;
        obj.transform.position = pos;
        obj.transform.localScale = Vector3.one * size;

        Collider col = obj.GetComponent<Collider>();
        if (col != null)
            Destroy(col);

        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = mat;

        _spawned.Add(obj);
        return obj;
    }

    void CreateLookLine(Vector3 pos, Quaternion rot, float length)
    {
        GameObject obj = new GameObject("CandidateLookLine");
        LineRenderer lr = obj.AddComponent<LineRenderer>();

        lr.positionCount = 2;
        lr.SetPosition(0, pos);
        lr.SetPosition(1, pos + rot * Vector3.forward * length);

        lr.startWidth = 0.035f;
        lr.endWidth = 0.015f;
        lr.material = _lineMat;
        lr.startColor = lineColor;
        lr.endColor = lineColor;

        _spawned.Add(obj);
    }

    void CreateLabel(Vector3 pos, string text)
    {
        GameObject obj = new GameObject("FigureLabel");
        obj.transform.position = pos;

        TextMesh tm = obj.AddComponent<TextMesh>();
        tm.text = text;
        tm.fontSize = 42;
        tm.characterSize = 0.035f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.white;

        Camera cam = Camera.main;
        if (cam != null)
            obj.transform.rotation = Quaternion.LookRotation(obj.transform.position - cam.transform.position);

        _spawned.Add(obj);
    }

    public void CaptureCurrentView(string suffix)
    {
        string folder = Path.Combine(Application.dataPath, "..", screenshotFolderName);
        Directory.CreateDirectory(folder);

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"PCCG_{suffix}_{timestamp}.png";
        string path = Path.Combine(folder, fileName);

        ScreenCapture.CaptureScreenshot(path, screenshotScale);
        Debug.Log($"[FigureDebug] Screenshot saved: {path}");
    }

    void Clear()
    {
        foreach (GameObject obj in _spawned)
        {
            if (obj != null)
                Destroy(obj);
        }

        _spawned.Clear();
    }

    void PrepareMaterials()
    {
        if (_sampledMat == null) _sampledMat = CreateTransparentMaterial(sampledColor);
        if (_rejectedMat == null) _rejectedMat = CreateTransparentMaterial(rejectedColor);
        if (_feasibleMat == null) _feasibleMat = CreateTransparentMaterial(feasibleColor);
        if (_scoreRejectedMat == null) _scoreRejectedMat = CreateTransparentMaterial(scoreRejectedColor);
        if (_topMat == null) _topMat = CreateTransparentMaterial(topColor);

        if (_lineMat == null)
        {
            _lineMat = new Material(Shader.Find("Sprites/Default"));
            _lineMat.color = lineColor;
        }
    }

    Material CreateTransparentMaterial(Color color)
    {
        Material mat = new Material(Shader.Find("Standard"));
        mat.color = color;

        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;

        return mat;
    }
}
