using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Profile-Constrained Camera Candidate Generator (PCCG)
///
/// Pipeline:
///   Profile p (from Python server)
///   → D range computation (h/H → world-space distance)
///   → Fibonacci sphere sampling
///   → Spatial constraint filtering (collision + occlusion)
///   → Cinematographic scoring
///   → Top-k candidate output
///
/// References:
///   Christie et al. (2008) Camera Control in Computer Graphics
///   Galvane et al. (2018) Immersive Previz
///   Cherif et al. (2007) Shot type identification (h/H range basis)
/// </summary>
public class CameraCandidateGenerator : MonoBehaviour
{
    [Header("Scene References")]
    [Tooltip("Man01 root transform")]
    public Transform characterRoot;

    [Tooltip("Man01 head bone (for head/face anchor)")]
    public Transform headBone;

    [Tooltip("Optional head/face mesh root used to measure Cherif h/H face height.")]
    public Transform headMeshRoot;

    [Tooltip("Main preview camera")]
    public Camera previewCamera;

    [Tooltip("Layer mask for environment mesh (Space_realmeshfi)")]
    public LayerMask environmentLayer;

    [Tooltip("Layer mask for character (Man01)")]
    public LayerMask characterLayer;

    [Header("Space Bounds")]
    [Tooltip("Root transform of the environment mesh proxy")]
    public Transform meshProxyRoot;
    private Bounds _spaceBounds;
    // ★ FIX: 회전된 mesh의 AABB 오류를 보정하기 위한 내부 탐색 반경
    private float _safeSearchRadius = 8f;

    // [진단용] IsInsideSpaceByRaycast 단계별 reject 카운터
    private int _dbgRayBlocked = 0;
    private int _dbgHitsZero   = 0;
    private int _dbgFloorFail  = 0;
    private int _dbgInsidePass = 0;

    [Header("Manual Playable Camera Area")]
    public BoxCollider playableAreaBox;
    public bool useManualPlayableArea = false;
    public bool drawManualPlayableAreaGizmo = false;

    [Header("Auto Create Playable Area Box")]
    public bool autoCreatePlayableAreaBoxFromMesh = false;
    public string playableAreaObjectName = "PlayableCameraArea_Auto";
    [Range(0.1f, 1.0f)]
    public float autoBoxShrinkX = 0.85f;
    [Range(0.1f, 1.0f)]
    public float autoBoxShrinkZ = 0.85f;
    public float autoBoxHeight = 2.2f;
    public float autoBoxYOffset = 1.1f;
    public bool logPlayableAreaCorners = true;

    [Header("Auto Playable Area From Mesh")]
    public Transform playableAreaMeshRoot;
    public bool useAutoPlayableMeshBounds = false;
    [Range(0.1f, 1.0f)]
    public float playableBoundsShrinkX = 0.85f;
    [Range(0.1f, 1.0f)]
    public float playableBoundsShrinkZ = 0.85f;
    public float playableBoundsYOffsetMin = -0.2f;
    public float playableBoundsYOffsetMax = 2.2f;
    public bool drawAutoPlayableBoundsGizmo = false;

    private MeshFilter _playableMeshFilter;
    private Bounds _playableLocalBounds;
    private bool _hasPlayableLocalBounds = false;

    [Header("Auto Playable Area From Floor Vertices")]
    public bool autoCreatePlayableAreaFromFloorVertices = false;
    public float floorVertexTolerance = 0.25f;
    public float floorAreaShrinkX = 0.90f;
    public float floorAreaShrinkZ = 0.90f;
    public float playableAreaHeight = 2.0f;
    public float playableAreaBottomOffset = 0.05f;
    public bool usePercentileBounds = true;
    [Range(0f, 10f)]
    public float boundsPercentileTrim = 5f;

    [Header("Sampling")]
    [Range(100, 1000)]
    public int sampleCount = 300;
    private bool _relaxedFallbackSampling = false;

    [Range(1, 10)]
    public int topK = 5;

    [Header("Profile p (set by IntentSystem)")]
    public float elevationMin = -12f;
    public float elevationMax = 12f;
    public float anglePriority = 1.0f;
    public float hOverH_min = 0.08f;
    public float hOverH_max = 0.13f;
    public string focusAnchor = "full_body";
    public string viewPreference = "unspecified";
    public bool useFaceHeightForShotScale = true;
    public float fallbackFaceHeightRatio = 0.13f;
    public string lookTargetMode = "upper_body";

    [Header("Intent Intensity")]
    public string intentIntensity = "medium";
    private string currentEffectClass = "Unknown";
    private string currentTargetAngle = "";

    [Header("Internal Sampling Ranges")]
    public float sampleElevationMin = -35f;
    public float sampleElevationMax = 80f;

    [Header("Camera Angle Interpretation")]
    public bool isBirdsEyeProfile = false;
    public float targetTiltMin = -12f;
    public float targetTiltMax = 12f;

    [Header("View Preference Settings")]
    public bool useCharacterLocalViewPreference = true;
    public float viewYawOffsetDeg = 0f;
    public float viewConeHalfAngle = 75f;
    public float quarterViewHalfAngle = 50f;
    [Tooltip("view_preference가 unspecified일 때 허용할 인물 정면 반구 half-angle(도). ±90이면 양 사이드까지 허용.")]
    public float unspecifiedFrontHalfAngle = 90f;
    private bool _ignoreUnspecifiedFrontFilter = false;

    private const float COLLISION_RADIUS = 0.18f;

    [Header("Output (read-only)")]
    public List<CameraCandidate> topCandidates = new List<CameraCandidate>();

    [Header("Candidate Preview")]
    public bool enableNumberKeyCandidatePreview = true;
    public int currentPreviewCandidateIndex = -1;

    [Header("Animated Target Follow")]
    public bool followAnimatedTarget = true;
    private bool hasFollowOffset = false;
    private Vector3 selectedCameraOffsetFromPivot = Vector3.zero;
    private bool isPlayingTrajectory = false;

    [Header("Trajectory Output (read-only)")]
    public List<CameraTrajectory> trajectoryCandidates = new List<CameraTrajectory>();

    [Header("Telephoto Settings")]
    [Tooltip("Close-up/XCU shot에서 사용할 망원 FOV (degrees). 작을수록 더 멀리서 촬영 가능.")]
    [Range(5f, 40f)]
    public float closeUpFOV = 20f;

    [Tooltip("Telephoto를 적용할 h/H 하한값. 이 이상이면 망원 렌즈 사용.")]
    public float telephotoThreshold = 0.40f;

    [Header("Bird's Eye Settings")]
    [Tooltip("Bird's eye shot에서 천장이 낮을 때 사용할 광각 FOV (degrees).")]
    [Range(40f, 120f)]
    public float birdsEyeFOV = 100f;

    [Header("Trajectory Settings")]
    [Range(8, 64)]
    public int trajectorySampleCount = 24;
    public float trajectoryDuration = 3.0f;
    public float splineArcHeight = 0.4f;

    [Header("Trajectory Avoidance")]
    public float characterAvoidRadius = 0.9f;
    public float sideOffset = 1.2f;
    public float avoidOffset = 0.8f;
    public bool rejectCollidingTrajectory = true;

    // Ground-level: 카메라를 바닥 근처에 배치하되 tilt는 약하게 유지
    [Header("Ground Level Settings")]
    public bool isGroundLevelProfile = false;
    [Tooltip("Ground-level profile: maximum camera height relative to character/floor ground Y.")]
    public float sampleCameraHeightMax = 0.6f;
    [Tooltip("Ground-level profile: minimum camera height relative to character/floor ground Y.")]
    public float sampleCameraHeightMin = 0.05f;

    // shoesReference is not a global camera anchor.
    // It is used only to constrain camera height for ground_level_angle.
    // The camera still looks at the profile-defined focus anchor.
    [Header("Ground Reference")]
    [Tooltip("Optional shoes/feet mesh used only for ground-level camera height constraint.")]
    public Transform shoesReference;

    // Trajectory 다양성 보장 설정
    [Header("Trajectory Top-K & Diversity")]
    [Range(1, 5)]
    public int topTrajectoryK = 3;
    [Tooltip("두 trajectory의 start/end 거리가 모두 이 값(m) 이하이면 유사 trajectory로 간주.")]
    public float trajectoryDiversityThreshold = 1.5f;

    [Header("Figure Debug Data")]
    public bool collectFigureDebugData = true;

    [HideInInspector] public List<Vector3> debugSampledPositions = new List<Vector3>();
    [HideInInspector] public List<Vector3> debugSpatialRejectedPositions = new List<Vector3>();
    [HideInInspector] public List<Vector3> debugFeasiblePositions = new List<Vector3>();
    [HideInInspector] public List<Vector3> debugScoreRejectedPositions = new List<Vector3>();

    [System.Serializable]
    public class CameraCandidate
    {
        public Vector3 position;
        public Quaternion rotation;
        public float totalScore;
        public float elevationDeg;
        public float hOverH;
        public float angleScore;
        public float scaleScore;
        public float viewScore;
        public float tiltDeg;
    }

    [System.Serializable]
    public class CameraTrajectory
    {
        public List<Vector3> positions = new List<Vector3>();
        public List<Quaternion> rotations = new List<Quaternion>();
        public CameraCandidate startCandidate;
        public CameraCandidate endCandidate;
        public float trajectoryScore;
        public float averagePlacementScore;
        public float collisionPenalty;
        public float visibilityPenalty;
        public float smoothnessScore;
        public float outsideSpacePenalty;
    }

    void Update()
    {
        if (enableNumberKeyCandidatePreview)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
                ApplyCandidateByIndex(0);
            else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
                ApplyCandidateByIndex(1);
            else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
                ApplyCandidateByIndex(2);
            else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
                ApplyCandidateByIndex(3);
            else if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5))
                ApplyCandidateByIndex(4);
        }

        if (followAnimatedTarget &&
            hasFollowOffset &&
            !isPlayingTrajectory &&
            previewCamera != null &&
            currentPreviewCandidateIndex >= 0 &&
            currentPreviewCandidateIndex < topCandidates.Count)
        {
            Vector3 currentPivot = GetPivotPoint();
            previewCamera.transform.position = currentPivot + selectedCameraOffsetFromPivot;

            Vector3 currentLookTarget = GetCurrentAnimatedLookTarget();
            Vector3 lookDir = currentLookTarget - previewCamera.transform.position;
            if (lookDir.sqrMagnitude > 0.0001f)
                previewCamera.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Main entry point
    // ═══════════════════════════════════════════════════════════════════════

    public void GenerateCandidates()
    {
        topCandidates.Clear();
        _dbgRayBlocked = 0;
        _dbgHitsZero   = 0;
        _dbgFloorFail  = 0;
        _dbgInsidePass = 0;

        if (collectFigureDebugData)
        {
            debugSampledPositions.Clear();
            debugSpatialRejectedPositions.Clear();
            debugFeasiblePositions.Clear();
            debugScoreRejectedPositions.Clear();
        }

        if (characterRoot == null || previewCamera == null)
        {
            Debug.LogError("[PCCG] characterRoot or previewCamera is null.");
            return;
        }

        Vector3 pivotPoint = GetPivotPoint();
        Vector3 lookTargetPoint = GetLookTargetPoint();

        Debug.Log($"[PCCG][PivotDbg] pivotInsideEnvCollider={Physics.CheckSphere(pivotPoint, 0.05f, environmentLayer)}, pivot={pivotPoint}");

        Debug.Log(
            "[PCCG] Tilt convention: ComputeCameraTiltDeg(camera,target) is negative when " +
            "the camera looks downward from above and positive when it looks upward from below. " +
            "Profile angle high/low signs may be opposite in the source table; this code does not flip them."
        );
        Debug.Log(
            $"[PCCG] Target split: pivot={pivotPoint}, lookTarget={lookTargetPoint}, " +
            $"lookTargetMode={lookTargetMode}, referenceHeight={GetReferenceSubjectHeight():F3}m"
        );

        if (isGroundLevelProfile)
        {
            float groundY = GetGroundReferenceY();
            Debug.Log(
                $"[PCCG] Ground-level reference: groundY={groundY:F2}, " +
                $"source={(shoesReference != null ? shoesReference.name : "characterRoot")}, " +
                $"relativeHeightRange=[{sampleCameraHeightMin:F2},{sampleCameraHeightMax:F2}]"
            );
        }

        // ── Space Bounds 계산 ───────────────────────────────────────────────
        if (meshProxyRoot != null)
        {
            Renderer[] renderers = meshProxyRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                _spaceBounds = renderers[0].bounds;
                foreach (var r in renderers)
                    _spaceBounds.Encapsulate(r.bounds);

                _spaceBounds.Expand(-0.3f);

                // ★ FIX 1: AABB는 mesh 회전 때문에 실제 공간보다 크게 나옴.
                // 가장 짧은 변의 절반을 안전 탐색 반경으로 사용.
                // 예: 18×18m 공간 → 회전 후 AABB가 25×25m 가 되어도
                //     안전 반경을 (18/2 × 0.85) ≈ 7.65m 로 제한.
                float shortSide = Mathf.Min(_spaceBounds.size.x, _spaceBounds.size.z);
                _safeSearchRadius = shortSide * 0.5f * 0.85f;

                Debug.Log($"[PCCG] Space bounds: center={_spaceBounds.center} " +
                          $"size={_spaceBounds.size} safeRadius={_safeSearchRadius:F2}m");
            }
        }
        else
        {
            _spaceBounds = new Bounds(pivotPoint, Vector3.one * 20f);
            _safeSearchRadius = 8f;
            Debug.LogWarning("[PCCG] meshProxyRoot not set. Using fallback bounds.");
        }

        // ── 천장 높이 계산 ──────────────────────────────────────────────────
        float ceilingY = float.MaxValue;
        {
            float highestCeiling = float.MinValue;
            int ceilingHitCount = 0;
            float[] probeOffsets = { 0f, 1.5f, -1.5f };
            foreach (float ox in probeOffsets)
            {
                foreach (float oz in probeOffsets)
                {
                    Vector3 origin = pivotPoint + new Vector3(ox, 0.1f, oz);
                    if (Physics.Raycast(origin, Vector3.up, out RaycastHit ch, 20f, environmentLayer))
                    {
                        ceilingHitCount++;
                        if (ch.point.y > highestCeiling) highestCeiling = ch.point.y;
                    }
                }
            }
            if (ceilingHitCount > 0)
            {
                ceilingY = highestCeiling - 0.3f;
                Debug.Log($"[PCCG] Ceiling: highest hit among {ceilingHitCount} probes, ceilingY={ceilingY:F2}m");
            }
            else
            {
                Debug.Log("[PCCG] No ceiling detected above any probe (ceilingY=MaxValue).");
            }
        }
        Debug.Log($"[PCCG] Ceiling check: pivotY={pivotPoint.y:F2}, detectedCeilingY={ceilingY:F2}");

        // ── Step 1: h/H → D 변환 ────────────────────────────────────────────
        float subjectHeightM = GetReferenceSubjectHeight();
        // ★ Telephoto: h/H_min이 threshold 이상이면 망원 FOV 사용.
        // 실제 촬영에서 배우 얼굴은 망원 렌즈로 멀리서 찍음.
        // 이렇게 하면 D가 충분히 커져서 캐릭터 통과 문제 해소.
        float effectiveFOV;
        if (hOverH_min >= telephotoThreshold)
            effectiveFOV = closeUpFOV;
        else if (isBirdsEyeProfile)
            effectiveFOV = birdsEyeFOV;
        else
            effectiveFOV = previewCamera.fieldOfView;
        float halfFovRad = effectiveFOV * 0.5f * Mathf.Deg2Rad;
        float tanHalfFov = Mathf.Tan(halfFovRad);
        Debug.Log($"[PCCG] Effective FOV: {effectiveFOV:F1}° " +
                  $"(telephoto={hOverH_min >= telephotoThreshold} birdsEye={isBirdsEyeProfile})");

        float profileDMin = (hOverH_max > 0f)
            ? subjectHeightM / (2f * tanHalfFov * hOverH_max)
            : 0.5f;
        float profileDMax = (hOverH_min > 0f)
            ? subjectHeightM / (2f * tanHalfFov * hOverH_min)
            : _safeSearchRadius;

        // Profile p는 scoring target. sampling range는 공간 기준으로 넓게 설정.
        float D_min = profileDMin;
        float D_max = profileDMax;

        if (D_max < D_min + 0.3f)
            D_max = D_min + 0.3f;

        D_max = Mathf.Min(D_max, _safeSearchRadius);
        if (D_max < D_min)
            D_min = Mathf.Max(0.1f, D_max - 0.3f);

        Debug.Log($"[PCCG] Profile h/H=[{hOverH_min:F3},{hOverH_max:F3}] profileD=[{profileDMin:F2},{profileDMax:F2}] samplingD=[{D_min:F2},{D_max:F2}]");

        // ── Step 2: Fibonacci sphere 샘플링 ─────────────────────────────────
        Vector3[] sphereSamples = GenerateFibonacciSphere(sampleCount);
        List<CameraCandidate> validCandidates = new List<CameraCandidate>();
        LayerMask combinedLayer = environmentLayer | characterLayer;

        int totalDirSamples = 0;
        int rejectElevation = 0;
        int rejectViewPreference = 0;
        int totalDistanceSamples = 0;
        int rejectCollision = 0;
        int rejectLineOfSight = 0;
        int rejectGround = 0;
        int rejectCeiling = 0;
        int rejectInsideSpace = 0;
        int rejectScale = 0;
        int rejectAngle = 0;
        int accepted = 0;

        foreach (Vector3 dir in sphereSamples)
        {
            totalDirSamples++;
            float elevDeg = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (elevDeg < sampleElevationMin || elevDeg > sampleElevationMax)
            {
                rejectElevation++;
                continue;
            }
            if (!PassesViewPreference(dir)) { rejectViewPreference++; continue; }

            int distanceSampleCount = _relaxedFallbackSampling ? 7 : 3;
            for (int di = 0; di < distanceSampleCount; di++)
            {
                totalDistanceSamples++;
                float t = (di + 1) / (float)(distanceSampleCount + 1);
                float D = Mathf.Lerp(D_min, D_max, t);
                Vector3 candidatePos = pivotPoint + dir * D;

                if (collectFigureDebugData)
                    debugSampledPositions.Add(candidatePos);

                // ── Step 3: 공간 제약 필터링 ──────────────────────────────────

                // 3a. Mesh와 충돌하는 위치 제거 (environment + character)
                if (Physics.CheckSphere(candidatePos, COLLISION_RADIUS, combinedLayer))
                {
                    rejectCollision++;
                    if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                    continue;
                }

                // 3b. 캐릭터가 보이지 않는 위치 제거
                if (!HasLineOfSight(candidatePos, lookTargetPoint))
                {
                    rejectLineOfSight++;
                    if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                    continue;
                }

                // 3c. 바닥 아래 위치 제거 (ground-level profile은 상대 높이로 별도 처리)
                if (!isGroundLevelProfile && candidatePos.y < 0.25f)
                {
                    rejectGround++;
                    if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                    continue;
                }

                // 3c-2. 천장 위 위치 제거
                if (candidatePos.y > ceilingY)
                {
                    rejectCeiling++;
                    if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                    continue;
                }

                // 3c-3. Ground-level profile: world Y가 아닌 ground 기준 상대 높이로 제한
                if (isGroundLevelProfile)
                {
                    float groundY = GetGroundReferenceY();
                    float relativeCameraHeight = candidatePos.y - groundY;
                    if (relativeCameraHeight < sampleCameraHeightMin ||
                        relativeCameraHeight > sampleCameraHeightMax)
                    {
                        rejectGround++;
                        if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                        continue;
                    }
                }

                // 3d. pivot→후보 직선이 벽에 막히면 공간 밖이므로 제외.
                if (!IsInsideSpaceByRaycast(candidatePos))
                {
                    rejectInsideSpace++;
                    if (collectFigureDebugData) debugSpatialRejectedPositions.Add(candidatePos);
                    continue;
                }

                if (collectFigureDebugData)
                    debugFeasiblePositions.Add(candidatePos);

                // ── Step 4: 실제 h/H 계산 (scoring용) ───────────────────────
                float actualDist = Vector3.Distance(candidatePos, lookTargetPoint);
                float actualHOverH = subjectHeightM / (2f * actualDist * tanHalfFov);

                // ── Step 5: Scoring ───────────────────────────────────────────
                float tiltDeg = ComputeCameraTiltDeg(candidatePos, lookTargetPoint);
                float angleScore = ComputeAngleScore(tiltDeg);
                float scaleScore = ComputeScaleScore(actualHOverH);
                float viewScore = ComputeViewPreferenceScore(candidatePos);

                if (scaleScore <= 0.001f)
                {
                    rejectScale++;
                    if (collectFigureDebugData) debugScoreRejectedPositions.Add(candidatePos);
                    continue;
                }

                if (angleScore <= 0.001f)
                {
                    rejectAngle++;
                    if (collectFigureDebugData) debugScoreRejectedPositions.Add(candidatePos);
                    continue;
                }

                bool hasViewPreference =
                    !string.IsNullOrEmpty(viewPreference) &&
                    viewPreference.Trim().ToLowerInvariant() != "unspecified";

                float totalScore;
                if (hasViewPreference)
                {
                    totalScore =
                        angleScore * anglePriority * 0.30f +
                        scaleScore * 0.30f +
                        viewScore * 0.40f;
                }
                else
                {
                    totalScore =
                        angleScore * anglePriority * 0.50f +
                        scaleScore * 0.50f;
                }

                float intensityBias = ComputeIntensityScaleBias(actualHOverH);
                totalScore *= intensityBias;

                Quaternion lookRot = Quaternion.LookRotation(lookTargetPoint - candidatePos, Vector3.up);

                validCandidates.Add(new CameraCandidate
                {
                    position     = candidatePos,
                    rotation     = lookRot,
                    totalScore   = totalScore,
                    elevationDeg = elevDeg,
                    hOverH       = actualHOverH,
                    angleScore   = angleScore,
                    scaleScore   = scaleScore,
                    viewScore    = viewScore,
                    tiltDeg      = tiltDeg
                });
                accepted++;
            }
        }

        validCandidates.Sort((a, b) => b.totalScore.CompareTo(a.totalScore));
        int take = Mathf.Min(topK, validCandidates.Count);
        topCandidates = validCandidates.GetRange(0, take);

        if (validCandidates.Count == 0)
        {
            Debug.LogError("[PCCG] No valid placement candidates. Check FilterStats.");
        }

        Debug.Log($"[PCCG] {validCandidates.Count} valid candidates → top-{take} selected.");

        if (topCandidates.Count > 0)
        {
            var best = topCandidates[0];
            Debug.Log(
                $"[PCCG] Best: score={best.totalScore:F3} elev={best.elevationDeg:F1}deg " +
                $"tilt={best.tiltDeg:F1}deg h/H={best.hOverH:F3} " +
                $"viewPreference={viewPreference} characterEuler={characterRoot.eulerAngles} " +
                $"viewYawOffset={viewYawOffsetDeg:F1} localAzimuth={GetCandidateLocalAzimuth(best.position):F1} " +
                $"angleScore={best.angleScore:F3} scaleScore={best.scaleScore:F3} viewScore={best.viewScore:F3}"
            );
        }

        Debug.Log($"[PCCG][FilterStats] dirSamples={totalDirSamples}, distSamples={totalDistanceSamples}, rejectElev={rejectElevation}, rejectView={rejectViewPreference}, rejectCollision={rejectCollision}, rejectLOS={rejectLineOfSight}, rejectGround={rejectGround}, rejectCeiling={rejectCeiling}, rejectInside={rejectInsideSpace}, rejectScale={rejectScale}, rejectAngle={rejectAngle}, accepted={accepted}");
        Debug.Log($"[PCCG][InsideDbg] rayBlocked={_dbgRayBlocked}, hitsZero={_dbgHitsZero}, floorFail={_dbgFloorFail}, insidePass={_dbgInsidePass}");
    }

    void EnsurePlayableAreaBox()
    {
        if (!autoCreatePlayableAreaBoxFromMesh)
            return;

        if (playableAreaBox != null)
            return;

        Transform root = playableAreaMeshRoot != null ? playableAreaMeshRoot : meshProxyRoot;
        if (root == null)
        {
            Debug.LogWarning("[PCCG] Cannot auto-create playable area: playableAreaMeshRoot and meshProxyRoot are null.");
            return;
        }

        MeshFilter mf = root.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("[PCCG] Cannot auto-create playable area: MeshFilter/sharedMesh not found.");
            return;
        }

        Bounds b = mf.sharedMesh.bounds;
        Vector3 center = b.center;
        Vector3 size = b.size;

        size.x *= autoBoxShrinkX;
        size.z *= autoBoxShrinkZ;

        float groundY = GetGroundReferenceY();
        Vector3 localGround = mf.transform.InverseTransformPoint(
            new Vector3(characterRoot.position.x, groundY, characterRoot.position.z)
        );

        center.y = localGround.y + autoBoxYOffset;
        size.y = autoBoxHeight;

        GameObject boxObj = new GameObject(playableAreaObjectName);
        boxObj.transform.position = mf.transform.position;
        boxObj.transform.rotation = mf.transform.rotation;
        boxObj.transform.localScale = mf.transform.lossyScale;

        BoxCollider box = boxObj.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = center;
        box.size = size;

        playableAreaBox = box;

        if (logPlayableAreaCorners)
            LogPlayableAreaBoxCorners(mf, b, center, size);

        Debug.Log(
            $"[PCCG] Auto-created playableAreaBox from mesh={mf.name}: " +
            $"boxObject={boxObj.name}, center={center}, size={size}, " +
            $"shrinkX={autoBoxShrinkX:F2}, shrinkZ={autoBoxShrinkZ:F2}, " +
            $"height={autoBoxHeight:F2}"
        );
    }

    void EnsurePlayableAreaBoxFromFloorVertices()
    {
        Transform root = playableAreaMeshRoot != null ? playableAreaMeshRoot : meshProxyRoot;
        if (root == null)
        {
            Debug.LogError("[PCCG] Cannot create playable area: mesh root is null.");
            return;
        }

        MeshFilter mf = root.GetComponentInChildren<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogError("[PCCG] Cannot create playable area: MeshFilter/sharedMesh not found.");
            return;
        }

        float groundY = GetGroundReferenceY();

        List<float> xs = new List<float>();
        List<float> zs = new List<float>();

        foreach (Vector3 v in mf.sharedMesh.vertices)
        {
            Vector3 world = mf.transform.TransformPoint(v);

            if (Mathf.Abs(world.y - groundY) > floorVertexTolerance)
                continue;

            Vector3 local = mf.transform.InverseTransformPoint(world);
            xs.Add(local.x);
            zs.Add(local.z);
        }

        if (xs.Count < 10)
        {
            Debug.LogError("[PCCG] Not enough floor vertices to create playable area. Increase floorVertexTolerance.");
            return;
        }

        xs.Sort();
        zs.Sort();

        int lo = usePercentileBounds ? Mathf.FloorToInt(xs.Count * boundsPercentileTrim / 100f) : 0;
        int hi = usePercentileBounds ? Mathf.CeilToInt(xs.Count * (1f - boundsPercentileTrim / 100f)) - 1 : xs.Count - 1;

        lo = Mathf.Clamp(lo, 0, xs.Count - 1);
        hi = Mathf.Clamp(hi, lo + 1, xs.Count - 1);

        float minX = xs[lo];
        float maxX = xs[hi];
        float minZ = zs[lo];
        float maxZ = zs[hi];

        float cx = (minX + maxX) * 0.5f;
        float cz = (minZ + maxZ) * 0.5f;
        float sx = (maxX - minX) * floorAreaShrinkX;
        float sz = (maxZ - minZ) * floorAreaShrinkZ;

        Vector3 localGround = mf.transform.InverseTransformPoint(
            new Vector3(characterRoot.position.x, groundY, characterRoot.position.z)
        );

        Vector3 center = new Vector3(
            cx,
            localGround.y + playableAreaBottomOffset + playableAreaHeight * 0.5f,
            cz
        );

        Vector3 size = new Vector3(
            Mathf.Max(0.1f, sx),
            playableAreaHeight,
            Mathf.Max(0.1f, sz)
        );

        GameObject boxObj = new GameObject("PlayableCameraArea_FloorAuto");
        boxObj.transform.SetParent(mf.transform, false);
        boxObj.transform.localPosition = Vector3.zero;
        boxObj.transform.localRotation = Quaternion.identity;
        boxObj.transform.localScale = Vector3.one;

        BoxCollider box = boxObj.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = center;
        box.size = size;

        playableAreaBox = box;
        useManualPlayableArea = true;
        drawManualPlayableAreaGizmo = true;

        if (logPlayableAreaCorners)
            LogPlayableAreaBoxCorners(playableAreaBox);

        Debug.Log($"[PCCG] Floor-based playable area created from {xs.Count} floor vertices. center={center}, size={size}, groundY={groundY}");
    }

    void LogPlayableAreaBoxCorners(MeshFilter mf, Bounds originalBounds, Vector3 boxCenter, Vector3 boxSize)
    {
        Vector3 half = boxSize * 0.5f;
        Vector3 min = boxCenter - half;
        Vector3 max = boxCenter + half;

        Vector3[] localCorners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z),
        };

        for (int i = 0; i < localCorners.Length; i++)
        {
            Vector3 worldCorner = mf.transform.TransformPoint(localCorners[i]);
            Debug.Log($"[PCCG] PlayableArea corner {i}: local={localCorners[i]}, world={worldCorner}");
        }
    }

    void LogPlayableAreaBoxCorners(BoxCollider box)
    {
        Vector3 half = box.size * 0.5f;
        Vector3 min = box.center - half;
        Vector3 max = box.center + half;

        Vector3[] localCorners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z),
        };

        for (int i = 0; i < localCorners.Length; i++)
        {
            Vector3 worldCorner = box.transform.TransformPoint(localCorners[i]);
            Debug.Log($"[PCCG] PlayableArea box corner {i}: local={localCorners[i]}, world={worldCorner}");
        }
    }

    void InitializeAutoPlayableBounds()
    {
        _hasPlayableLocalBounds = false;
        _playableMeshFilter = null;

        Transform root = playableAreaMeshRoot != null ? playableAreaMeshRoot : meshProxyRoot;
        if (root == null)
        {
            Debug.LogWarning("[PCCG] No playableAreaMeshRoot or meshProxyRoot set. Auto playable bounds disabled.");
            return;
        }

        _playableMeshFilter = root.GetComponentInChildren<MeshFilter>();
        if (_playableMeshFilter == null || _playableMeshFilter.sharedMesh == null)
        {
            Debug.LogWarning("[PCCG] No MeshFilter/sharedMesh found for auto playable bounds.");
            return;
        }

        Bounds b = _playableMeshFilter.sharedMesh.bounds;
        Vector3 center = b.center;
        Vector3 size = b.size;

        size.x *= playableBoundsShrinkX;
        size.z *= playableBoundsShrinkZ;

        float groundYWorld = GetGroundReferenceY();
        Vector3 localGround = _playableMeshFilter.transform.InverseTransformPoint(
            new Vector3(characterRoot.position.x, groundYWorld, characterRoot.position.z)
        );

        float localMinY = localGround.y + playableBoundsYOffsetMin;
        float localMaxY = localGround.y + playableBoundsYOffsetMax;

        center.y = (localMinY + localMaxY) * 0.5f;
        size.y = Mathf.Max(0.1f, localMaxY - localMinY);

        _playableLocalBounds = new Bounds(center, size);
        _hasPlayableLocalBounds = true;

        Debug.Log(
            $"[PCCG] Auto playable local bounds initialized: " +
            $"root={root.name}, meshFilter={_playableMeshFilter.name}, " +
            $"center={center}, size={size}, " +
            $"shrinkX={playableBoundsShrinkX:F2}, shrinkZ={playableBoundsShrinkZ:F2}"
        );
    }

    bool IsInsideAutoPlayableBounds(Vector3 worldPos)
    {
        if (!useAutoPlayableMeshBounds)
            return true;

        if (!_hasPlayableLocalBounds || _playableMeshFilter == null)
            return true;

        Vector3 localPos = _playableMeshFilter.transform.InverseTransformPoint(worldPos);
        return _playableLocalBounds.Contains(localPos);
    }

    bool IsInsideManualPlayableArea(Vector3 worldPos)
    {
        if (!useManualPlayableArea)
            return true;

        if (playableAreaBox == null)
        {
            Debug.LogWarning("[PCCG] useManualPlayableArea is true but playableAreaBox is null. Rejecting candidate.");
            return false;
        }

        Transform t = playableAreaBox.transform;
        Vector3 local = t.InverseTransformPoint(worldPos);

        Vector3 center = playableAreaBox.center;
        Vector3 half = playableAreaBox.size * 0.5f;
        Vector3 d = local - center;

        return Mathf.Abs(d.x) <= half.x &&
               Mathf.Abs(d.y) <= half.y &&
               Mathf.Abs(d.z) <= half.z;
    }

    bool IsInsideSpaceByRaycast(Vector3 pos)
    {
        // 공통: pivot→후보 직선이 환경 mesh에 막히면 공간 밖.
        Vector3 pivot = GetPivotPoint();
        Vector3 toCandidate = pos - pivot;
        float dist = toCandidate.magnitude;
        if (dist > 0.01f)
        {
            if (Physics.Raycast(pivot, toCandidate.normalized, dist, environmentLayer))
            {
                _dbgRayBlocked++;
                return false;
            }
        }

        int hits = 0;
        float checkDist = Mathf.Max(2.0f, _safeSearchRadius * 1.5f);

        // 후보 위치의 Y 대신 pivot Y에서 수평 ray를 쏴서,
        // 천장이 낮은 방에서 후보가 높거나 낮을 때 벽 높이대를 빗나가는 문제 방지.
        Vector3 pivotForProbe = GetPivotPoint();
        Vector3 probeOrigin = new Vector3(pos.x, pivotForProbe.y, pos.z);
        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            if (Physics.Raycast(probeOrigin, dir, checkDist, environmentLayer))
                hits++;
        }

        if (hits < 1)
        {
            _dbgHitsZero++;
            return false;
        }

        // ground-level만 추가 검사: 후보 바로 아래에 바닥 mesh가 가까이 있어야 내부.
        // 방 안이면 아래에 바닥이 있고, 방 밖(벽 너머)이면 아래가 뚫려서 막히는 게 없음.
        if (isGroundLevelProfile)
        {
            float floorProbe = 2.0f; // 후보 아래 2m 안에 바닥이 있으면 방 안으로 간주
            if (!Physics.Raycast(pos, Vector3.down, floorProbe, environmentLayer))
            {
                _dbgFloorFail++;
                return false;
            }
        }

        _dbgInsidePass++;
        return true;
    }

    public void ApplyBestCandidate()
    {
        if (topCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No candidates to apply.");
            return;
        }

        var best = topCandidates[0];
        Vector3 pivot = GetPivotPoint();
        selectedCameraOffsetFromPivot = best.position - pivot;
        hasFollowOffset = true;
        previewCamera.transform.position = best.position;
        previewCamera.transform.rotation = best.rotation;
        ApplyFOVForSelectedCandidate(best);
        currentPreviewCandidateIndex = 0;
        Debug.Log($"[PCCG] Camera placed at {best.position} (score={best.totalScore:F3})");
    }

    public void ApplyCandidateByIndex(int index)
    {
        if (topCandidates == null || topCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No candidates to preview.");
            return;
        }

        if (index < 0 || index >= topCandidates.Count)
        {
            Debug.LogWarning($"[PCCG] Candidate #{index + 1} is not available. topCandidates.Count={topCandidates.Count}");
            return;
        }

        if (previewCamera == null)
        {
            Debug.LogWarning("[PCCG] previewCamera is null. Cannot preview candidate.");
            return;
        }

        StopAllCoroutines();
        isPlayingTrajectory = false;

        var candidate = topCandidates[index];
        Vector3 pivot = GetPivotPoint();
        selectedCameraOffsetFromPivot = candidate.position - pivot;
        hasFollowOffset = true;
        previewCamera.transform.position = candidate.position;
        Vector3 currentLookTarget = GetCurrentAnimatedLookTarget();
        Vector3 lookDir = currentLookTarget - candidate.position;
        if (lookDir.sqrMagnitude > 0.0001f)
            previewCamera.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        else
            previewCamera.transform.rotation = candidate.rotation;
        ApplyFOVForSelectedCandidate(candidate);
        currentPreviewCandidateIndex = index;

        Debug.Log(
            $"[PCCG] Preview candidate #{index + 1}: " +
            $"score={candidate.totalScore:F3}, pos={candidate.position}, " +
            $"elev={candidate.elevationDeg:F2}, tilt={candidate.tiltDeg:F2}, " +
            $"h/H={candidate.hOverH:F3}, angleScore={candidate.angleScore:F3}, " +
            $"scaleScore={candidate.scaleScore:F3}, viewScore={candidate.viewScore:F3}"
        );
    }

    void ApplyCameraFOV()
    {
        if (previewCamera == null) return;
        bool usesTelephoto = hOverH_min >= telephotoThreshold;
        bool isBirdsEye = elevationMin <= -60f;
        if (usesTelephoto)
            previewCamera.fieldOfView = closeUpFOV;
        else if (isBirdsEye)
            previewCamera.fieldOfView = birdsEyeFOV;
        else
            previewCamera.fieldOfView = 60f;
        Debug.Log($"[PCCG] Camera FOV set to {previewCamera.fieldOfView:F1}°");
    }

    // ground_level_angle에서만 카메라 높이 제한에 사용.
    // shoesReference가 있으면 shoes mesh의 바닥(bounds.min.y)을 기준으로 사용.
    // 다른 profile에서는 이 함수를 호출하지 않는다.
    // (추후 아래쪽 raycast로 실제 floor hit Y로 확장 가능)
    void ApplyFOVForSelectedCandidate(CameraCandidate candidate)
    {
        if (previewCamera == null || candidate == null)
            return;

        if (isBirdsEyeProfile)
            return;

        string effectClass = string.IsNullOrEmpty(currentEffectClass) ? "" : currentEffectClass.ToLowerInvariant();
        bool usesCloseUpFOV = effectClass == "relaxed" || hOverH_min >= telephotoThreshold;
        if (!usesCloseUpFOV)
            return;

        float center = (hOverH_min + hOverH_max) * 0.5f;
        float targetHOverH;
        string normalizedIntensity = string.IsNullOrEmpty(intentIntensity)
            ? "medium"
            : intentIntensity.ToLowerInvariant();

        switch (normalizedIntensity)
        {
            case "low":
                targetHOverH = center;
                break;
            case "high":
                targetHOverH = Mathf.Lerp(center, hOverH_max, 0.85f);
                break;
            case "medium":
            case "unknown":
            default:
                targetHOverH = Mathf.Lerp(center, hOverH_max, 0.5f);
                break;
        }

        targetHOverH = Mathf.Clamp(targetHOverH, hOverH_min, hOverH_max);

        float subjectHeightM = GetReferenceSubjectHeight();
        float D = Vector3.Distance(candidate.position, GetLookTargetPoint());
        if (D <= 0.001f || targetHOverH <= 0.001f)
            return;

        float fovRad = 2f * Mathf.Atan(subjectHeightM / (2f * D * targetHOverH));
        float fovDeg = Mathf.Clamp(fovRad * Mathf.Rad2Deg, 10f, 35f);
        previewCamera.fieldOfView = fovDeg;

        Debug.Log(
            $"[PCCG] FOV adjusted for intensity: effect={currentEffectClass}, intensity={intentIntensity}, " +
            $"D={D:F2}, targetH/H={targetHOverH:F3}, fov={fovDeg:F1}"
        );
    }

    float GetGroundReferenceY()
    {
        if (shoesReference != null)
        {
            Renderer[] renderers = shoesReference.GetComponentsInChildren<Renderer>();
            if (renderers != null && renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (var r in renderers)
                    b.Encapsulate(r.bounds);
                return b.min.y;
            }

            Collider[] colliders = shoesReference.GetComponentsInChildren<Collider>();
            if (colliders != null && colliders.Length > 0)
            {
                Bounds b = colliders[0].bounds;
                foreach (var c in colliders)
                    b.Encapsulate(c.bounds);
                return b.min.y;
            }

            return shoesReference.position.y;
        }

        if (characterRoot != null)
            return characterRoot.position.y;

        return 0f;
    }

    Vector3 GetAnchorPosition()
    {
        return GetLookTargetPoint();
    }

    Vector3 GetPivotPoint()
    {
        float characterHeight = GetCharacterWorldHeight();
        return characterRoot.position + Vector3.up * (characterHeight * 0.5f);
    }

    Vector3 GetLookTargetPoint()
    {
        string mode = (lookTargetMode ?? string.Empty).ToLowerInvariant();

        if (headBone != null)
        {
            switch (mode)
            {
                case "xcu_cu":
                case "close_up":
                case "face_eye":
                case "eye":
                case "eyes":
                case "face":
                    return headBone.position;

                case "mcu_ms":
                case "medium":
                case "face_upper_body":
                    return Vector3.Lerp(characterRoot.position, headBone.position, 0.75f);

                case "mls":
                case "wide":
                case "upper_body":
                    return Vector3.Lerp(characterRoot.position, headBone.position, 0.60f);
            }
        }

        float characterHeight = GetCharacterWorldHeight();
        return characterRoot.position + Vector3.up * (characterHeight * GetLookTargetHeightRatio());
    }

    Vector3 GetCurrentAnimatedLookTarget()
    {
        return GetLookTargetPoint();
    }

    float GetLookTargetHeightRatio()
    {
        switch ((lookTargetMode ?? string.Empty).ToLowerInvariant())
        {
            // XCU + CU: close-up group.
            case "xcu_cu":
            case "close_up":
            case "face_eye":
            case "eye":
            case "eyes":
            case "face":
                return 0.90f;

            // MCU + MS: medium group.
            case "mcu_ms":
            case "medium":
            case "face_upper_body":
                return 0.82f;

            // MLS: wide shot group.
            case "mls":
            case "wide":
            case "upper_body":
                return 0.67f;

            // XLS + LS: long shot group.
            case "xls_ls":
            case "ls":
            case "long":
            case "torso_upper":
            case "torso":
                return 0.62f;

            // Optional pure XLS mode.
            case "xls":
            case "body_center":
            case "body":
                return 0.50f;

            default:
                return 0.67f;
        }
    }

    float GetReferenceSubjectHeight()
    {
        float characterHeight = GetCharacterWorldHeight();

        if (useFaceHeightForShotScale)
            return GetFaceHeight();

        switch (focusAnchor)
        {
            case "head":
            case "face":       return characterHeight * 0.13f;
            case "upper_body": return characterHeight * 0.44f;
            case "full_body":  return characterHeight;
            case "feet":       return characterHeight * 0.20f;
            default:           return characterHeight;
        }
    }

    float GetFaceHeight()
    {
        if (headMeshRoot != null)
        {
            Renderer[] renderers = headMeshRoot.GetComponentsInChildren<Renderer>();
            if (renderers != null && renderers.Length > 0)
            {
                Bounds bounds = renderers[0].bounds;
                foreach (var r in renderers)
                    bounds.Encapsulate(r.bounds);
                if (bounds.size.y > 0.001f)
                    return bounds.size.y;
            }

            Collider[] colliders = headMeshRoot.GetComponentsInChildren<Collider>();
            if (colliders != null && colliders.Length > 0)
            {
                Bounds bounds = colliders[0].bounds;
                foreach (var c in colliders)
                    bounds.Encapsulate(c.bounds);
                if (bounds.size.y > 0.001f)
                    return bounds.size.y;
            }
        }

        return GetCharacterWorldHeight() * Mathf.Max(0.001f, fallbackFaceHeightRatio);
    }

    string InferLookTargetModeFromShotScale(float min, float max)
    {
        // XCU + CU: profile p h/H = 0.65-0.95.
        if (min >= 0.65f && max <= 0.95f)
            return "xcu_cu";

        // MCU + MS: profile p h/H = 0.24-0.65.
        if (min >= 0.24f && max <= 0.65f)
            return "mcu_ms";

        // MLS: profile p h/H = 0.15-0.24.
        if (min >= 0.15f && max <= 0.24f)
            return "mls";

        // XLS + LS: profile p h/H = 0.03-0.15.
        if (min >= 0.03f && max <= 0.15f)
            return "xls_ls";

        // Optional pure XLS mode.
        if (max <= 0.05f)
            return "xls";

        float center = (min + max) * 0.5f;

        if (center >= 0.65f) return "xcu_cu";
        if (center >= 0.24f) return "mcu_ms";
        if (center >= 0.15f) return "mls";
        if (center >= 0.03f) return "xls_ls";

        return "xls";
    }

    float GetCharacterWorldHeight()
    {
        if (headBone != null && characterRoot != null)
            return (headBone.position.y - characterRoot.position.y) / 0.90f;

        if (characterRoot == null)
        {
            Debug.LogWarning("[PCCG] characterRoot is null. Using default 1.7m.");
            return 1.7f;
        }

        CapsuleCollider capsule = characterRoot.GetComponentInChildren<CapsuleCollider>();
        if (capsule != null)
            return capsule.height * characterRoot.lossyScale.y;

        CharacterController cc = characterRoot.GetComponentInChildren<CharacterController>();
        if (cc != null)
            return cc.height * characterRoot.lossyScale.y;

        Renderer[] renderers = characterRoot.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            Bounds bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds.size.y;
        }

        return 1.7f;
    }

    Vector3[] GenerateFibonacciSphere(int n)
    {
        Vector3[] pts = new Vector3[n];
        float phi = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int i = 0; i < n; i++)
        {
            float y = 1f - (i / (float)(n - 1)) * 2f;
            float r = Mathf.Sqrt(1f - y * y);
            float theta = phi * i;
            pts[i] = new Vector3(r * Mathf.Cos(theta), y, r * Mathf.Sin(theta));
        }
        return pts;
    }

    bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        // 환경 mesh만 확인. characterLayer 포함 시
        // bird's eye/close-up에서 anchor raycast가 몸통에 막혀 0 candidates 발생.
        // 캐릭터 통과 방지는 CheckSphere(combinedLayer)와
        // PassesTooCloseToCharacter()에서 별도 처리.
        return !Physics.Raycast(from, dir.normalized,
            out RaycastHit _, dist, environmentLayer);
    }

    bool PassesViewPreference(Vector3 dir)
    {
        string pref = string.IsNullOrEmpty(viewPreference)
            ? "unspecified"
            : viewPreference.Trim().ToLowerInvariant();
        if (characterRoot == null)
            return true;

        float az = GetLocalAzimuthFromDirection(dir);

        if (pref == "unspecified")
        {
            // 방향 미지정: 정면 반구(±unspecifiedFrontHalfAngle)만 허용해 head가 보이게 한다.
            if (_ignoreUnspecifiedFrontFilter)
                return true;
            return Mathf.Abs(Mathf.DeltaAngle(az, 0f)) <= unspecifiedFrontHalfAngle;
        }

        switch (pref)
        {
            case "front":
                return Mathf.Abs(Mathf.DeltaAngle(az, 0f)) <= viewConeHalfAngle;
            case "back":
                return Mathf.Abs(Mathf.DeltaAngle(az, 180f)) <= viewConeHalfAngle;
            case "left":
                return Mathf.Abs(Mathf.DeltaAngle(az, -90f)) <= viewConeHalfAngle;
            case "right":
                return Mathf.Abs(Mathf.DeltaAngle(az, 90f)) <= viewConeHalfAngle;
            case "three_quarter_front":
                return MinAbsDeltaAngle(az, -45f, 45f) <= quarterViewHalfAngle;
            case "three_quarter_back":
                return MinAbsDeltaAngle(az, -135f, 135f) <= quarterViewHalfAngle;
            default:
                return true;
        }
    }

    float ComputeViewPreferenceScore(Vector3 candidatePos)
    {
        string pref = string.IsNullOrEmpty(viewPreference)
            ? "unspecified"
            : viewPreference.Trim().ToLowerInvariant();
        if (pref == "unspecified")
            return 1f;
        if (characterRoot == null)
            return 1f;

        float az = GetCandidateLocalAzimuth(candidatePos);
        float halfAngle;
        float diff;

        switch (pref)
        {
            case "front":
                halfAngle = viewConeHalfAngle;
                diff = Mathf.Abs(Mathf.DeltaAngle(az, 0f));
                break;
            case "back":
                halfAngle = viewConeHalfAngle;
                diff = Mathf.Abs(Mathf.DeltaAngle(az, 180f));
                break;
            case "left":
                halfAngle = viewConeHalfAngle;
                diff = Mathf.Abs(Mathf.DeltaAngle(az, -90f));
                break;
            case "right":
                halfAngle = viewConeHalfAngle;
                diff = Mathf.Abs(Mathf.DeltaAngle(az, 90f));
                break;
            case "three_quarter_front":
                halfAngle = quarterViewHalfAngle;
                diff = MinAbsDeltaAngle(az, -45f, 45f);
                break;
            case "three_quarter_back":
                halfAngle = quarterViewHalfAngle;
                diff = MinAbsDeltaAngle(az, -135f, 135f);
                break;
            default:
                return 1f;
        }

        return Mathf.Clamp01(1f - diff / Mathf.Max(halfAngle, 0.001f));
    }

    float GetCandidateLocalAzimuth(Vector3 candidatePos)
    {
        Vector3 dir = candidatePos - GetPivotPoint();
        return GetLocalAzimuthFromDirection(dir);
    }

    float GetLocalAzimuthFromDirection(Vector3 dir)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            return 0f;
        dir.Normalize();

        Vector3 localDir = useCharacterLocalViewPreference && characterRoot != null
            ? characterRoot.InverseTransformDirection(dir)
            : dir;

        localDir.y = 0f;
        if (localDir.sqrMagnitude < 0.0001f)
            return 0f;
        localDir.Normalize();

        if (Mathf.Abs(viewYawOffsetDeg) > 0.001f)
            localDir = Quaternion.Euler(0f, -viewYawOffsetDeg, 0f) * localDir;

        return Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
    }

    float MinAbsDeltaAngle(float angle, float targetA, float targetB)
    {
        return Mathf.Min(
            Mathf.Abs(Mathf.DeltaAngle(angle, targetA)),
            Mathf.Abs(Mathf.DeltaAngle(angle, targetB))
        );
    }

    float ComputeCameraTiltDeg(Vector3 cameraPos, Vector3 targetPos)
    {
        // Convention note for VRST/profile tables:
        // target below a high camera => negative tilt; target above a low camera => positive tilt.
        // Some angle profile tables may label high/low with the opposite sign. Do not flip signs here.
        Vector3 dir = (targetPos - cameraPos).normalized;
        return Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
    }

    float ComputeAngleScore(float tiltDeg)
    {
        float center = (targetTiltMin + targetTiltMax) * 0.5f;
        float half   = Mathf.Max((targetTiltMax - targetTiltMin) * 0.5f, 0.1f);
        return Mathf.Clamp01(1f - Mathf.Abs(tiltDeg - center) / half);
    }

    float ComputeScaleScore(float hOverH)
    {
        float center = (hOverH_min + hOverH_max) * 0.5f;
        float half   = Mathf.Max((hOverH_max - hOverH_min) * 0.5f, 0.001f);
        return Mathf.Clamp01(1f - Mathf.Abs(hOverH - center) / half);
    }

    float ComputeIntensityScaleBias(float hOverH)
    {
        float center = (hOverH_min + hOverH_max) * 0.5f;
        float bias = GetIntensityBias01();
        float target = center;
        string effectClass = string.IsNullOrEmpty(currentEffectClass) ? "" : currentEffectClass.ToLowerInvariant();

        if (effectClass == "tense" || effectClass == "sadness")
        {
            target = Mathf.Lerp(center, hOverH_min, bias);
        }
        else if (effectClass == "relaxed")
        {
            target = Mathf.Lerp(center, hOverH_max, bias);
        }
        else if (effectClass == "delighted")
        {
            target = Mathf.Lerp(center, hOverH_max, bias * 0.7f);
        }

        float half   = Mathf.Max((hOverH_max - hOverH_min) * 0.5f, 0.001f);
        float closeness = Mathf.Clamp01(1f - Mathf.Abs(hOverH - target) / half);
        return Mathf.Lerp(0.90f, 1.10f, closeness);
    }

    float GetIntensityBias01()
    {
        string normalized = string.IsNullOrEmpty(intentIntensity)
            ? "medium"
            : intentIntensity.ToLowerInvariant();

        switch (normalized)
        {
            case "low":
                return 0.25f;
            case "high":
                return 0.80f;
            case "medium":
            case "unknown":
            default:
                return 0.50f;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Trajectory generation & playback
    // ═══════════════════════════════════════════════════════════════════════

    // 모든 후보 trajectory를 수집한 뒤 diversity를 보장하며 top-k 선택
    public void GenerateTrajectoryCandidates()
    {
        trajectoryCandidates.Clear();

        if (topCandidates == null || topCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No placement candidates for trajectory generation.");
            return;
        }

        if (topCandidates.Count < 2)
        {
            Debug.LogWarning("[PCCG] Only one placement candidate. Trajectory skipped, applying static placement fallback.");
            ApplyBestCandidate();
            return;
        }

        List<CameraTrajectory> allCandidates = new List<CameraTrajectory>();
        CameraCandidate endCandidate = topCandidates[0];

        for (int i = 1; i < topCandidates.Count; i++)
        {
            CameraCandidate startCandidate = topCandidates[i];

            CameraTrajectory leftTraj = BuildAvoidanceSplineTrajectory(
                startCandidate.position, startCandidate.rotation,
                endCandidate.position, endCandidate.rotation,
                endCandidate, -1f);
            ScoreTrajectory(leftTraj);
            if (IsTrajectoryAcceptable(leftTraj)) allCandidates.Add(leftTraj);

            CameraTrajectory rightTraj = BuildAvoidanceSplineTrajectory(
                startCandidate.position, startCandidate.rotation,
                endCandidate.position, endCandidate.rotation,
                endCandidate, 1f);
            ScoreTrajectory(rightTraj);
            if (IsTrajectoryAcceptable(rightTraj)) allCandidates.Add(rightTraj);

            CameraTrajectory upTraj = BuildSplineTrajectory(
                startCandidate.position, startCandidate.rotation,
                endCandidate.position, endCandidate.rotation,
                endCandidate);
            ScoreTrajectory(upTraj);
            if (IsTrajectoryAcceptable(upTraj)) allCandidates.Add(upTraj);
        }

        // 점수 기준 정렬 후 diversity를 보장하며 top-k 선택
        allCandidates.Sort((a, b) => b.trajectoryScore.CompareTo(a.trajectoryScore));
        trajectoryCandidates = SelectDiverseTopTrajectories(allCandidates, topTrajectoryK, trajectoryDiversityThreshold);

        Debug.Log($"[PCCG] top-{trajectoryCandidates.Count} trajectories generated (pool={allCandidates.Count}).");
        for (int i = 0; i < trajectoryCandidates.Count; i++)
        {
            var traj = trajectoryCandidates[i];
            Debug.Log($"[PCCG] Traj#{i} score={traj.trajectoryScore:F3} " +
                      $"collision={traj.collisionPenalty:F3} " +
                      $"visibility={traj.visibilityPenalty:F3} " +
                      $"outside={traj.outsideSpacePenalty:F3} " +
                      $"smoothness={traj.smoothnessScore:F3}");
        }

        if (trajectoryCandidates.Count == 0)
            Debug.LogWarning("[PCCG] No valid trajectory after collision/diversity filtering.");
    }

    CameraTrajectory BuildSplineTrajectory(Vector3 startPos, Quaternion startRot,
                                           Vector3 endPos, Quaternion endRot,
                                           CameraCandidate endCandidate)
    {
        CameraTrajectory traj = new CameraTrajectory();
        traj.endCandidate = endCandidate;
        Vector3 lookTargetPoint = GetLookTargetPoint();
        Vector3 mid = (startPos + endPos) * 0.5f;
        Vector3 control = mid + Vector3.up * splineArcHeight;
        for (int i = 0; i < trajectorySampleCount; i++)
        {
            float t = i / (float)(trajectorySampleCount - 1);
            Vector3 pos = QuadraticBezier(startPos, control, endPos, t);
            Quaternion rot = Quaternion.LookRotation(lookTargetPoint - pos, Vector3.up);
            traj.positions.Add(pos);
            traj.rotations.Add(rot);
        }
        return traj;
    }

    bool IsTrajectoryAcceptable(CameraTrajectory traj)
    {
        if (traj == null || traj.positions.Count == 0) return false;
        if (rejectCollidingTrajectory && traj.collisionPenalty > 0f) return false;
        if (traj.visibilityPenalty > 0.25f) return false;
        if (traj.outsideSpacePenalty > 0f) return false;
        if (PassesTooCloseToCharacter(traj)) return false;
        return true;
    }

    bool PassesTooCloseToCharacter(CameraTrajectory traj)
    {
        Vector3 characterCenter = GetPivotPoint();
        foreach (Vector3 pos in traj.positions)
        {
            Vector3 flatPos = new Vector3(pos.x, 0f, pos.z);
            Vector3 flatCenter = new Vector3(characterCenter.x, 0f, characterCenter.z);
            if (Vector3.Distance(flatPos, flatCenter) < characterAvoidRadius)
                return true;
        }
        return false;
    }

    CameraTrajectory BuildAvoidanceSplineTrajectory(
        Vector3 startPos, Quaternion startRot,
        Vector3 endPos, Quaternion endRot,
        CameraCandidate endCandidate, float sideSign)
    {
        CameraTrajectory traj = new CameraTrajectory();
        traj.endCandidate = endCandidate;
        Vector3 pivotPoint = GetPivotPoint();
        Vector3 lookTargetPoint = GetLookTargetPoint();
        Vector3 mid = (startPos + endPos) * 0.5f;
        Vector3 pathDir = endPos - startPos;
        pathDir.y = 0f;
        if (pathDir.sqrMagnitude < 0.001f) pathDir = characterRoot.forward;
        pathDir.Normalize();
        Vector3 sideDir = Vector3.Cross(Vector3.up, pathDir).normalized * sideSign;
        Vector3 awayFromPivot = mid - pivotPoint;
        awayFromPivot.y = 0f;
        if (awayFromPivot.sqrMagnitude > 0.001f) awayFromPivot.Normalize();
        else awayFromPivot = sideDir;
        Vector3 control = mid
            + sideDir * sideOffset
            + awayFromPivot * avoidOffset
            + Vector3.up * splineArcHeight;
        for (int i = 0; i < trajectorySampleCount; i++)
        {
            float t = i / (float)(trajectorySampleCount - 1);
            Vector3 pos = QuadraticBezier(startPos, control, endPos, t);
            Quaternion rot = Quaternion.LookRotation(lookTargetPoint - pos, Vector3.up);
            traj.positions.Add(pos);
            traj.rotations.Add(rot);
        }
        return traj;
    }

    Vector3 QuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return u * u * p0 + 2f * u * t * p1 + t * t * p2;
    }

    void ScoreTrajectory(CameraTrajectory traj)
    {
        if (traj.positions.Count == 0) { traj.trajectoryScore = 0f; return; }
        float collisionPenalty = 0f;
        float visibilityPenalty = 0f;
        Vector3 lookTargetPoint = GetLookTargetPoint();
        LayerMask combinedTraj = environmentLayer | characterLayer;
        float outsidePenalty = 0f;
        for (int i = 0; i < traj.positions.Count; i++)
        {
            Vector3 pos = traj.positions[i];
            if (Physics.CheckSphere(pos, COLLISION_RADIUS, combinedTraj)) collisionPenalty += 1f;
            if (!HasLineOfSight(pos, lookTargetPoint)) visibilityPenalty += 1f;
            if (!IsInsideSpaceByRaycast(pos)) outsidePenalty += 1f;
        }
        collisionPenalty /= traj.positions.Count;
        visibilityPenalty /= traj.positions.Count;
        outsidePenalty /= traj.positions.Count;
        traj.outsideSpacePenalty = outsidePenalty;
        float smoothnessScore = ComputeSmoothnessScore(traj);
        float endPlacementScore = traj.endCandidate != null ? traj.endCandidate.totalScore : 0f;
        traj.trajectoryScore = endPlacementScore * 0.55f + smoothnessScore * 0.25f
            + (1f - collisionPenalty) * 0.10f + (1f - visibilityPenalty) * 0.10f;
        traj.collisionPenalty = collisionPenalty;
        traj.visibilityPenalty = visibilityPenalty;
        traj.smoothnessScore = smoothnessScore;
        traj.averagePlacementScore = endPlacementScore;
    }

    float ComputeSmoothnessScore(CameraTrajectory traj)
    {
        if (traj.positions.Count < 3) return 1f;
        float totalTurn = 0f; int count = 0;
        for (int i = 1; i < traj.positions.Count - 1; i++)
        {
            Vector3 prev = (traj.positions[i] - traj.positions[i - 1]).normalized;
            Vector3 next = (traj.positions[i + 1] - traj.positions[i]).normalized;
            totalTurn += Vector3.Angle(prev, next);
            count++;
        }
        return Mathf.Clamp01(1f - (count > 0 ? totalTurn / count : 0f) / 45f);
    }

    // Diversity를 보장하며 상위 k개 trajectory 선택.
    // 이미 선택된 trajectory와 start/end 위치가 모두 threshold 이하이면 유사 후보로 skip.
    List<CameraTrajectory> SelectDiverseTopTrajectories(List<CameraTrajectory> sorted, int k, float threshold)
    {
        var selected = new List<CameraTrajectory>();
        foreach (var traj in sorted)
        {
            if (selected.Count >= k) break;
            if (traj.positions.Count == 0) continue;

            Vector3 trajStart = traj.positions[0];
            Vector3 trajEnd   = traj.positions[traj.positions.Count - 1];
            bool tooSimilar   = false;

            foreach (var sel in selected)
            {
                float startDist = Vector3.Distance(trajStart, sel.positions[0]);
                float endDist   = Vector3.Distance(trajEnd, sel.positions[sel.positions.Count - 1]);
                if (startDist < threshold && endDist < threshold)
                {
                    tooSimilar = true;
                    break;
                }
            }
            if (!tooSimilar) selected.Add(traj);
        }
        return selected;
    }

    // Bird's-eye 전용: 천장이 낮은 공간에서 후보 샘플링 대신
    // 천장 바로 아래 최대 높이에 카메라를 직접 배치하고 아래를 내려다본다.
    // profile p의 -90° 이상치를 공간이 허용하지 않을 때의 fallback.
    void PlaceBirdsEyeCamera()
    {
        Vector3 pivotPoint = GetPivotPoint();
        Vector3 lookTargetPoint = GetLookTargetPoint();

        // 천장 높이 측정 (9-probe 중 최고값)
        float highestCeiling = float.MinValue;
        int hitCount = 0;
        float[] probeOffsets = { 0f, 1.5f, -1.5f };
        foreach (float ox in probeOffsets)
            foreach (float oz in probeOffsets)
            {
                Vector3 origin = pivotPoint + new Vector3(ox, 0.1f, oz);
                if (Physics.Raycast(origin, Vector3.up, out RaycastHit ch, 20f, environmentLayer))
                {
                    hitCount++;
                    if (ch.point.y > highestCeiling) highestCeiling = ch.point.y;
                }
            }

        float camY;
        if (hitCount > 0)
            camY = highestCeiling - 0.3f;   // 천장 바로 아래
        else
            camY = pivotPoint.y + 5f;        // 천장 못 찾으면 5m 위

        // 카메라는 캐릭터 머리 위 수직선상, 천장 바로 아래
        Vector3 camPos = new Vector3(pivotPoint.x, camY, pivotPoint.z);

        // 너무 낮으면(머리에 거의 붙으면) 최소 확보
        if (camPos.y < lookTargetPoint.y + 0.5f)
            camPos.y = lookTargetPoint.y + 0.5f;

        Quaternion camRot = Quaternion.LookRotation(lookTargetPoint - camPos, Vector3.up);
        previewCamera.transform.position = camPos;
        previewCamera.transform.rotation = camRot;
        previewCamera.fieldOfView = birdsEyeFOV;

        float height = camPos.y - lookTargetPoint.y;
        Debug.Log($"[PCCG] Bird's-eye direct placement: camPos={camPos}, ceilingY={(hitCount > 0 ? highestCeiling : -1f):F2}, heightAboveTarget={height:F2}m, FOV={birdsEyeFOV:F1}");

        // topCandidates에도 1개 기록 (UI/gizmo 표시용)
        topCandidates.Clear();
        topCandidates.Add(new CameraCandidate {
            position     = camPos,
            rotation     = camRot,
            totalScore   = 1f,
            elevationDeg = 90f,
            hOverH       = 0f,
            tiltDeg      = ComputeCameraTiltDeg(camPos, lookTargetPoint)
        });
    }

    // Bird's-eye profile: trajectory 없이 best static placement만 적용
    void ApplyStaticCameraPlacement()
    {
        if (topCandidates == null || topCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No placement candidates for static mode.");
            return;
        }

        var best = topCandidates[0];
        previewCamera.transform.position = best.position;
        previewCamera.transform.rotation = best.rotation;
        Debug.Log($"[PCCG] Static placement applied: pos={best.position} score={best.totalScore:F3}");

        int showCount = Mathf.Min(3, topCandidates.Count);
        for (int i = 0; i < showCount; i++)
        {
            var c = topCandidates[i];
            Debug.Log($"[PCCG] Static candidate #{i}: pos={c.position} " +
                      $"elev={c.elevationDeg:F1}° tilt={c.tiltDeg:F1}° score={c.totalScore:F3}");
        }
    }

    public void PlayBestTrajectory()
    {
        if (trajectoryCandidates == null || trajectoryCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No trajectory candidates to play.");
            return;
        }
        StopAllCoroutines();
        isPlayingTrajectory = false;
        hasFollowOffset = false;
        currentPreviewCandidateIndex = -1;
        DrawTrajectoryLine(trajectoryCandidates[0]);
        StartCoroutine(PlayTrajectoryCoroutine(trajectoryCandidates[0]));
    }

    IEnumerator PlayTrajectoryCoroutine(CameraTrajectory traj)
    {
        isPlayingTrajectory = true;
        float elapsed = 0f;
        while (elapsed < trajectoryDuration)
        {
            float t = Mathf.Clamp01(elapsed / trajectoryDuration);
            float scaled = t * (traj.positions.Count - 1);
            int idx = Mathf.FloorToInt(scaled);
            int nextIdx = Mathf.Min(idx + 1, traj.positions.Count - 1);
            float localT = scaled - idx;
            Vector3 pos = Vector3.Lerp(traj.positions[idx], traj.positions[nextIdx], localT);
            previewCamera.transform.position = pos;
            Vector3 currentLookTarget = (headBone != null) ? headBone.position : GetLookTargetPoint();
            Vector3 lookDir = currentLookTarget - pos;
            if (lookDir.sqrMagnitude > 0.0001f)
                previewCamera.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
            else
                previewCamera.transform.rotation = Quaternion.Slerp(traj.rotations[idx], traj.rotations[nextIdx], localT);
            elapsed += Time.deltaTime;
            yield return null;
        }
        int last = traj.positions.Count - 1;
        previewCamera.transform.position = traj.positions[last];
        Vector3 finalLookTarget = (headBone != null) ? headBone.position : GetLookTargetPoint();
        Vector3 finalDir = finalLookTarget - traj.positions[last];
        if (finalDir.sqrMagnitude > 0.0001f)
            previewCamera.transform.rotation = Quaternion.LookRotation(finalDir, Vector3.up);
        else
            previewCamera.transform.rotation = traj.rotations[last];
        isPlayingTrajectory = false;
    }

    [Header("Trajectory Visualization")]
    public bool showTrajectoryLine = true;
    public Color trajectoryColor = Color.cyan;
    private LineRenderer _trajectoryLine;

    void DrawTrajectoryLine(CameraTrajectory traj)
    {
        if (!showTrajectoryLine || traj == null || traj.positions.Count == 0) return;

        if (_trajectoryLine == null)
        {
            GameObject lineObj = new GameObject("TrajectoryLine");
            lineObj.transform.SetParent(transform);
            _trajectoryLine = lineObj.AddComponent<LineRenderer>();
            _trajectoryLine.material = new Material(Shader.Find("Sprites/Default"));
            _trajectoryLine.startWidth = 0.05f;
            _trajectoryLine.endWidth = 0.05f;
            _trajectoryLine.useWorldSpace = true;
        }

        _trajectoryLine.startColor = trajectoryColor;
        _trajectoryLine.endColor = trajectoryColor;
        _trajectoryLine.positionCount = traj.positions.Count;
        _trajectoryLine.SetPositions(traj.positions.ToArray());
    }

    void OnDrawGizmos()
    {
        if (topCandidates != null)
        {
            for (int i = 0; i < topCandidates.Count; i++)
            {
                var c = topCandidates[i];
                float t = 1f - (float)i / Mathf.Max(topCandidates.Count - 1, 1);
                Gizmos.color = Color.Lerp(Color.green, Color.magenta, t);
                float radius = i == currentPreviewCandidateIndex ? 0.2f : 0.12f;
                Gizmos.DrawSphere(c.position, radius);
                Gizmos.DrawRay(c.position, c.rotation * Vector3.forward * 0.4f);
            }
        }

        // if (characterRoot != null)
        // {
        //     Gizmos.color = Color.red;
        //     Gizmos.DrawSphere(GetPivotPoint(), 0.08f);

        //     Gizmos.color = Color.blue;
        //     Gizmos.DrawSphere(GetLookTargetPoint(), 0.08f);
        // }
    }

    void DrawManualPlayableAreaGizmo()
    {
        if (!drawManualPlayableAreaGizmo || playableAreaBox == null)
            return;

        Matrix4x4 oldMatrix = Gizmos.matrix;
        Color oldColor = Gizmos.color;

        Gizmos.matrix = playableAreaBox.transform.localToWorldMatrix;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(playableAreaBox.center, playableAreaBox.size);

        Gizmos.matrix = oldMatrix;
        Gizmos.color = oldColor;
    }

    void OnDrawGizmosSelected()
    {
        return;
    }

    public void ApplyProfileAndGenerate(IntentOutputData output)
    {
        var prof = output.profile;

        if (prof.angle_ranges != null && prof.angle_ranges.Count > 0)
        {
            var primary = prof.angle_ranges[0];
            foreach (var r in prof.angle_ranges)
                if (r.priority > primary.priority) primary = r;
            elevationMin  = primary.min_deg;
            elevationMax  = primary.max_deg;
            anglePriority = primary.priority;
        }

        if (prof.subject_scale_range != null)
        {
            // Cherif h/H: h is face height and H is frame height.
            // Shot scale is controlled by this range; lookTargetMode only sets the gaze center.
            hOverH_min = prof.subject_scale_range.min;
            hOverH_max = prof.subject_scale_range.max;
            lookTargetMode = InferLookTargetModeFromShotScale(hOverH_min, hOverH_max);
        }

        focusAnchor    = prof.default_focus_anchor ?? "full_body";
        viewPreference = output.view_preference    ?? "unspecified";
        currentEffectClass = output.effect_class ?? "Unknown";
        currentTargetAngle = output.profile != null && output.profile.target_angle != null
            ? output.profile.target_angle
            : "";
        intentIntensity = string.IsNullOrEmpty(output.intensity) || output.intensity.ToLowerInvariant() == "unknown"
            ? "medium"
            : output.intensity.ToLowerInvariant();

        Debug.Log($"[PCCG] Intent intensity applied: {intentIntensity}");

        isBirdsEyeProfile =
            output.profile != null &&
            output.profile.target_angle != null &&
            output.profile.target_angle.ToLower().Contains("birds");

        // Ground-level 감지: 카메라 위치는 바닥 근처, tilt는 약하게(0~12°) 분리
        isGroundLevelProfile =
            output.profile != null &&
            output.profile.target_angle != null &&
            output.profile.target_angle.ToLower().Contains("ground");

        bool isLowAngleProfile =
            output != null &&
            output.profile != null &&
            output.profile.target_angle != null &&
            output.profile.target_angle.ToLowerInvariant().Contains("low_angle");

        targetTiltMin = elevationMin;
        targetTiltMax = elevationMax;

        if (isLowAngleProfile)
        {
            float a = Mathf.Abs(elevationMin);
            float b = Mathf.Abs(elevationMax);
            targetTiltMin = Mathf.Min(a, b);
            targetTiltMax = Mathf.Max(a, b);

            Debug.Log(
                $"[PCCG] Low-angle tilt sign converted for Unity convention: " +
                $"profile=[{elevationMin},{elevationMax}] scoring=[{targetTiltMin},{targetTiltMax}]"
            );
        }

        if (isBirdsEyeProfile)
        {
            sampleElevationMin = 65f;
            sampleElevationMax = 89f;
            Debug.LogWarning($"[PCCG] Bird's-eye profile. Tilt target=[{targetTiltMin},{targetTiltMax}], samplingElev=[{sampleElevationMin},{sampleElevationMax}]");
        }
        else if (isGroundLevelProfile)
        {
            // 카메라 위치는 shoes/ground 기준 상대 높이로 제한 (shoesReference 우선).
            // elevation sampling은 완만하게 열어두고 위치 높이 필터(3c-3)가 제어.
            // tilt target은 수평 근처로 유지 (bird's-eye처럼 아래로 꺾이면 안 됨).
            sampleElevationMin = -25f;
            sampleElevationMax = 25f;
            sampleCameraHeightMin = 0.05f;
            sampleCameraHeightMax = 0.50f;
            targetTiltMin = 0f;
            targetTiltMax = 35f;  // 신발 높이에서 몸통을 올려다보는 실제 각도를 허용 (공간 배치 보정)
            Debug.Log(
                $"[PCCG] Ground-level profile. " +
                $"Camera relative height=[{sampleCameraHeightMin},{sampleCameraHeightMax}]m, " +
                $"tilt target=[{targetTiltMin},{targetTiltMax}°], " +
                $"samplingElev=[{sampleElevationMin},{sampleElevationMax}]"
            );
        }
        else
        {
            sampleElevationMin = Mathf.Max(-45f, elevationMin - 25f);
            sampleElevationMax = Mathf.Min(80f, elevationMax + 25f);
            Debug.Log($"[PCCG] Profile angle target=[{targetTiltMin},{targetTiltMax}], samplingElev=[{sampleElevationMin},{sampleElevationMax}]");
        }

        Debug.Log($"[PCCG] Profile applied: {output.effect_class} " +
                  $"elev=[{elevationMin},{elevationMax}] " +
                  $"h/H=[{hOverH_min:F3},{hOverH_max:F3}] " +
                  $"lookTargetMode={lookTargetMode}");

        // FOV가 h/H 계산에 영향을 주므로 후보 생성 전에 먼저 적용
        Debug.Log(
            $"[PCCG] Intensity scoring bias: effect={currentEffectClass}, intensity={intentIntensity}, " +
            $"scaleRange=[{hOverH_min:F3},{hOverH_max:F3}], tiltRange=[{targetTiltMin:F1},{targetTiltMax:F1}]"
        );
        ApplyCameraFOV();
        GenerateCandidates();

        bool isRelaxedProfile =
            output != null &&
            (
                output.effect_class == "Relaxed" ||
                (output.profile != null &&
                 output.profile.target_angle != null &&
                 output.profile.target_angle.ToLower().Contains("low_angle"))
            );

        if (isRelaxedProfile && topCandidates.Count == 0)
        {
            int oldSampleCount = sampleCount;
            bool oldRelaxedFallbackSampling = _relaxedFallbackSampling;

            Debug.LogWarning("[PCCG] Relaxed fallback retry: increased sampling density only. profile p unchanged.");

            sampleCount = Mathf.Max(sampleCount, 1000);
            _relaxedFallbackSampling = true;

            try
            {
                GenerateCandidates();
            }
            finally
            {
                sampleCount = oldSampleCount;
                _relaxedFallbackSampling = oldRelaxedFallbackSampling;
            }

            if (topCandidates.Count == 0)
            {
                Debug.LogWarning(
                    "[PCCG] Relaxed fallback still produced 0 candidates. " +
                    "Likely causes: character collider rejects close-up positions, angleScore/scaleScore hard reject, or inside raycast rejects low-angle close-up positions."
                );
            }
        }

        string appliedViewPreference = string.IsNullOrEmpty(viewPreference)
            ? "unspecified"
            : viewPreference.Trim().ToLowerInvariant();

        if (!isBirdsEyeProfile &&
            topCandidates.Count < 2 &&
            appliedViewPreference != "unspecified")
        {
            float oldViewConeHalfAngle = viewConeHalfAngle;
            float oldQuarterViewHalfAngle = quarterViewHalfAngle;
            int oldSampleCount = sampleCount;

            viewConeHalfAngle = Mathf.Max(viewConeHalfAngle, 95f);
            quarterViewHalfAngle = Mathf.Max(quarterViewHalfAngle, 70f);
            sampleCount = Mathf.Max(sampleCount, 600);

            Debug.Log(
                $"[PCCG] Too few candidates ({topCandidates.Count}) with viewPreference={viewPreference}. " +
                $"Retrying with relaxed view cones: viewCone={viewConeHalfAngle:F1}, " +
                $"quarterCone={quarterViewHalfAngle:F1}, sampleCount={sampleCount}."
            );

            GenerateCandidates();

            viewConeHalfAngle = oldViewConeHalfAngle;
            quarterViewHalfAngle = oldQuarterViewHalfAngle;
            sampleCount = oldSampleCount;
        }

        // Unspecified 정면 반구 필터가 후보를 너무 적게 남기면 전체 구로 완화하여 재생성
        if (!isBirdsEyeProfile &&
            appliedViewPreference == "unspecified" &&
            topCandidates.Count < 2 &&
            !_ignoreUnspecifiedFrontFilter)
        {
            Debug.LogWarning("[PCCG] Unspecified front-hemisphere filter left too few candidates. Retrying with full sphere.");
            _ignoreUnspecifiedFrontFilter = true;
            GenerateCandidates();
            _ignoreUnspecifiedFrontFilter = false;
        }

        if (isBirdsEyeProfile)
        {
            // Bird's eye: 천장 아래 직접 배치 (후보 샘플링 결과 무시)
            Debug.Log("[PCCG] Bird's-eye profile detected: using direct overhead placement.");
            PlaceBirdsEyeCamera();
        }
        else
        {
            // 일반/ground-level: placement + top-k trajectory 생성 및 재생
            GenerateTrajectoryCandidates();
            if (trajectoryCandidates != null && trajectoryCandidates.Count > 0)
                PlayBestTrajectory();
        }
    }
}