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

    [Header("Sampling")]
    [Range(100, 1000)]
    public int sampleCount = 300;

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

    [Header("Internal Sampling Ranges")]
    public float sampleElevationMin = -35f;
    public float sampleElevationMax = 80f;

    [Header("Camera Angle Interpretation")]
    public bool isBirdsEyeProfile = false;
    public float targetTiltMin = -12f;
    public float targetTiltMax = 12f;

    private const float COLLISION_RADIUS = 0.18f;

    [Header("Output (read-only)")]
    public List<CameraCandidate> topCandidates = new List<CameraCandidate>();

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
    public float birdsEyeFOV = 75f;

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
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Main entry point
    // ═══════════════════════════════════════════════════════════════════════

    public void GenerateCandidates()
    {
        topCandidates.Clear();

        if (characterRoot == null || previewCamera == null)
        {
            Debug.LogError("[PCCG] characterRoot or previewCamera is null.");
            return;
        }

        Vector3 anchorPos = GetAnchorPosition();

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
            _spaceBounds = new Bounds(anchorPos, Vector3.one * 20f);
            _safeSearchRadius = 8f;
            Debug.LogWarning("[PCCG] meshProxyRoot not set. Using fallback bounds.");
        }

        // ── 천장 높이 계산 ──────────────────────────────────────────────────
        float ceilingY = float.MaxValue;
        if (Physics.Raycast(anchorPos + Vector3.up * 0.1f, Vector3.up,
            out RaycastHit ceilHit, 20f, environmentLayer))
        {
            ceilingY = ceilHit.point.y - 0.3f;
            Debug.Log($"[PCCG] Ceiling detected at Y={ceilingY:F2}m");
        }
        else
        {
            Debug.Log("[PCCG] No ceiling detected above anchor.");
        }

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
        float D_min = 0.8f;
        float D_max = Mathf.Max(1.5f, _safeSearchRadius);

        if (hOverH_min >= telephotoThreshold)
        {
            D_min = 1.0f;
            D_max = Mathf.Min(_safeSearchRadius, Mathf.Max(3.0f, profileDMax));
        }

        if (isBirdsEyeProfile)
        {
            D_min = 1.0f;
            D_max = Mathf.Min(_safeSearchRadius, 4.5f);
        }

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

            for (int di = 0; di < 3; di++)
            {
                totalDistanceSamples++;
                float t = (di + 1) / 4f;
                float D = Mathf.Lerp(D_min, D_max, t);
                Vector3 candidatePos = anchorPos + dir * D;

                // ── Step 3: 공간 제약 필터링 ──────────────────────────────────

                // 3a. Mesh와 충돌하는 위치 제거 (environment + character)
                if (Physics.CheckSphere(candidatePos, COLLISION_RADIUS, combinedLayer))
                { rejectCollision++; continue; }

                // 3b. 캐릭터가 보이지 않는 위치 제거
                if (!HasLineOfSight(candidatePos, anchorPos))
                { rejectLineOfSight++; continue; }

                // 3c. 바닥 아래 위치 제거 (ground-level profile은 상대 높이로 별도 처리)
                if (!isGroundLevelProfile && candidatePos.y < 0.25f)
                { rejectGround++; continue; }

                // 3c-2. 천장 위 위치 제거
                if (candidatePos.y > ceilingY) { rejectCeiling++; continue; }

                // 3c-3. Ground-level profile: world Y가 아닌 ground 기준 상대 높이로 제한
                if (isGroundLevelProfile)
                {
                    float groundY = GetGroundReferenceY();
                    float relativeCameraHeight = candidatePos.y - groundY;
                    if (relativeCameraHeight < sampleCameraHeightMin ||
                        relativeCameraHeight > sampleCameraHeightMax)
                    { rejectGround++; continue; }
                }

                // 3d. AABB 대신 다방향 raycast로 공간 내부 확인.
                if (!IsInsideSpaceByRaycast(candidatePos)) { rejectInsideSpace++; continue; }

                // ── Step 4: 실제 h/H 계산 (scoring용) ───────────────────────
                float actualDist = Vector3.Distance(candidatePos, anchorPos);
                float actualHOverH = subjectHeightM / (2f * actualDist * tanHalfFov);

                // ── Step 5: Scoring ───────────────────────────────────────────
                float tiltDeg = ComputeCameraTiltDeg(candidatePos, anchorPos);
                float angleScore = ComputeAngleScore(tiltDeg);
                float scaleScore = ComputeScaleScore(actualHOverH);
                float totalScore = angleScore * anglePriority * 0.5f + scaleScore * 0.5f;

                Quaternion lookRot = Quaternion.LookRotation(anchorPos - candidatePos, Vector3.up);

                validCandidates.Add(new CameraCandidate
                {
                    position     = candidatePos,
                    rotation     = lookRot,
                    totalScore   = totalScore,
                    elevationDeg = elevDeg,
                    hOverH       = actualHOverH,
                    angleScore   = angleScore,
                    scaleScore   = scaleScore,
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
            Debug.Log($"[PCCG] Best: score={best.totalScore:F3} elev={best.elevationDeg:F1}° tilt={best.tiltDeg:F1}° h/H={best.hOverH:F3}");
        }

        Debug.Log($"[PCCG][FilterStats] dirSamples={totalDirSamples}, distSamples={totalDistanceSamples}, rejectElev={rejectElevation}, rejectView={rejectViewPreference}, rejectCollision={rejectCollision}, rejectLOS={rejectLineOfSight}, rejectGround={rejectGround}, rejectCeiling={rejectCeiling}, rejectInside={rejectInsideSpace}, accepted={accepted}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ★ FIX 3: 다방향 Raycast로 공간 내부 판단
    //
    // 배경:
    //   MeshProxyRoot가 Y축 43.876도 회전되어 있어서
    //   Renderer.bounds(AABB)가 실제 공간보다 훨씬 큰 박스를 반환함.
    //   → bounds.Contains()를 쓰면 실제 방 밖의 모서리 공간도 통과시킴.
    //
    // 해결:
    //   후보 위치에서 수평 8방향으로 ray를 쏴서
    //   환경 mesh(벽/바닥/천장)와의 충돌 수를 확인.
    //   방 안에 있으면 주변에 벽이 있어서 많은 방향에서 충돌이 일어남.
    //   방 밖에 있으면 벽이 없는 방향이 많아서 충돌이 적음.
    // ═══════════════════════════════════════════════════════════════════════
    bool IsInsideSpaceByRaycast(Vector3 pos)
    {
        Vector3 anchor = GetAnchorPosition();

        // 근거리는 visibility check를 통과했으므로 내부로 간주
        // 기존 1.5m에서 3.0m로 확대 (sampling D_min이 0.8m여서 실제 샘플 최소값이 2.5m임)
        if (Vector3.Distance(pos, anchor) < 3.0f)
            return true;

        int hits = 0;
        float checkDist = _safeSearchRadius * 2f;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            if (Physics.Raycast(pos, dir, checkDist, environmentLayer))
                hits++;
        }

        // 불완전한 mesh와 복도 입구를 감안해 threshold를 1로 낮춤
        // (복도 공간은 양쪽 벽 중 하나만 있어도 내부로 판단)
        return hits >= 1;
    }

    public void ApplyBestCandidate()
    {
        if (topCandidates.Count == 0)
        {
            Debug.LogWarning("[PCCG] No candidates to apply.");
            return;
        }

        var best = topCandidates[0];
        previewCamera.transform.position = best.position;
        previewCamera.transform.rotation = best.rotation;
        Debug.Log($"[PCCG] Camera placed at {best.position} (score={best.totalScore:F3})");
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
        if (focusAnchor == "head" && headBone != null)
            return headBone.position;
        if (focusAnchor == "upper_body" && headBone != null)
            return Vector3.Lerp(characterRoot.position, headBone.position, 0.6f);

        float characterHeight = GetCharacterWorldHeight();

        float heightRatio;
        switch (focusAnchor)
        {
            case "head":
            case "face":       heightRatio = 0.88f; break;
            case "upper_body": heightRatio = 0.65f; break;
            case "full_body":  heightRatio = 0.62f; break;
            case "feet":       heightRatio = 0.05f; break;
            default:           heightRatio = 0.50f; break;
        }

        return characterRoot.position + Vector3.up * (characterHeight * heightRatio);
    }

    float GetReferenceSubjectHeight()
    {
        float characterHeight = GetCharacterWorldHeight();
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

    float GetCharacterWorldHeight()
    {
        if (characterRoot == null)
        {
            Debug.LogWarning("[PCCG] characterRoot is null. Using default 1.7m.");
            return 1.7f;
        }

        Renderer[] renderers = characterRoot.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            Bounds bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds.size.y;
        }

        CapsuleCollider capsule = characterRoot.GetComponentInChildren<CapsuleCollider>();
        if (capsule != null)
            return capsule.height * characterRoot.lossyScale.y;

        CharacterController cc = characterRoot.GetComponentInChildren<CharacterController>();
        if (cc != null)
            return cc.height * characterRoot.lossyScale.y;

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
        if (viewPreference == "unspecified") return true;
        float az = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        switch (viewPreference)
        {
            case "front":                return Mathf.Abs(az) < 45f;
            case "back":                 return Mathf.Abs(az) > 135f;
            case "left":                 return az > -135f && az < -45f;
            case "right":               return az > 45f && az < 135f;
            case "three_quarter_front": return Mathf.Abs(az) > 22.5f && Mathf.Abs(az) < 67.5f;
            case "three_quarter_back":  return Mathf.Abs(az) > 112.5f && Mathf.Abs(az) < 157.5f;
            default: return true;
        }
    }

    float ComputeCameraTiltDeg(Vector3 cameraPos, Vector3 targetPos)
    {
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

    // ═══════════════════════════════════════════════════════════════════════
    // Trajectory generation & playback
    // ═══════════════════════════════════════════════════════════════════════

    // 모든 후보 trajectory를 수집한 뒤 diversity를 보장하며 top-k 선택
    public void GenerateTrajectoryCandidates()
    {
        trajectoryCandidates.Clear();

        if (topCandidates == null || topCandidates.Count < 2)
        {
            Debug.LogWarning("[PCCG] Need at least 2 placement candidates for trajectory generation.");
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
        Vector3 anchorPos = GetAnchorPosition();
        Vector3 mid = (startPos + endPos) * 0.5f;
        Vector3 control = mid + Vector3.up * splineArcHeight;
        for (int i = 0; i < trajectorySampleCount; i++)
        {
            float t = i / (float)(trajectorySampleCount - 1);
            Vector3 pos = QuadraticBezier(startPos, control, endPos, t);
            Quaternion rot = Quaternion.LookRotation(anchorPos - pos, Vector3.up);
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
        if (PassesTooCloseToCharacter(traj)) return false;
        return true;
    }

    bool PassesTooCloseToCharacter(CameraTrajectory traj)
    {
        Vector3 characterCenter = characterRoot.position +
            Vector3.up * (GetCharacterWorldHeight() * 0.5f);
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
        Vector3 anchorPos = GetAnchorPosition();
        Vector3 mid = (startPos + endPos) * 0.5f;
        Vector3 pathDir = endPos - startPos;
        pathDir.y = 0f;
        if (pathDir.sqrMagnitude < 0.001f) pathDir = characterRoot.forward;
        pathDir.Normalize();
        Vector3 sideDir = Vector3.Cross(Vector3.up, pathDir).normalized * sideSign;
        Vector3 awayFromAnchor = mid - anchorPos;
        awayFromAnchor.y = 0f;
        if (awayFromAnchor.sqrMagnitude > 0.001f) awayFromAnchor.Normalize();
        else awayFromAnchor = sideDir;
        Vector3 control = mid
            + sideDir * sideOffset
            + awayFromAnchor * avoidOffset
            + Vector3.up * splineArcHeight;
        for (int i = 0; i < trajectorySampleCount; i++)
        {
            float t = i / (float)(trajectorySampleCount - 1);
            Vector3 pos = QuadraticBezier(startPos, control, endPos, t);
            Quaternion rot = Quaternion.LookRotation(anchorPos - pos, Vector3.up);
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
        Vector3 anchorPos = GetAnchorPosition();
        LayerMask combinedTraj = environmentLayer | characterLayer;
        for (int i = 0; i < traj.positions.Count; i++)
        {
            Vector3 pos = traj.positions[i];
            if (Physics.CheckSphere(pos, COLLISION_RADIUS, combinedTraj)) collisionPenalty += 1f;
            if (!HasLineOfSight(pos, anchorPos)) visibilityPenalty += 1f;
        }
        collisionPenalty /= traj.positions.Count;
        visibilityPenalty /= traj.positions.Count;
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
        DrawTrajectoryLine(trajectoryCandidates[0]);
        StartCoroutine(PlayTrajectoryCoroutine(trajectoryCandidates[0]));
    }

    IEnumerator PlayTrajectoryCoroutine(CameraTrajectory traj)
    {
        float elapsed = 0f;
        while (elapsed < trajectoryDuration)
        {
            float t = Mathf.Clamp01(elapsed / trajectoryDuration);
            float scaled = t * (traj.positions.Count - 1);
            int idx = Mathf.FloorToInt(scaled);
            int nextIdx = Mathf.Min(idx + 1, traj.positions.Count - 1);
            float localT = scaled - idx;
            previewCamera.transform.position = Vector3.Lerp(traj.positions[idx], traj.positions[nextIdx], localT);
            previewCamera.transform.rotation = Quaternion.Slerp(traj.rotations[idx], traj.rotations[nextIdx], localT);
            elapsed += Time.deltaTime;
            yield return null;
        }
        int last = traj.positions.Count - 1;
        previewCamera.transform.position = traj.positions[last];
        previewCamera.transform.rotation = traj.rotations[last];
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
        if (topCandidates == null || topCandidates.Count == 0) return;
        for (int i = 0; i < topCandidates.Count; i++)
        {
            var c = topCandidates[i];
            float t = 1f - (float)i / Mathf.Max(topCandidates.Count - 1, 1);
            Gizmos.color = Color.Lerp(Color.blue, Color.red, t);
            Gizmos.DrawSphere(c.position, 0.12f);
            Gizmos.DrawRay(c.position, c.rotation * Vector3.forward * 0.4f);
        }
        if (characterRoot != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(GetAnchorPosition(), 0.08f);
        }
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
            hOverH_min = prof.subject_scale_range.min;
            hOverH_max = prof.subject_scale_range.max;
        }

        focusAnchor    = prof.default_focus_anchor ?? "full_body";
        viewPreference = output.view_preference    ?? "unspecified";

        isBirdsEyeProfile =
            output.profile != null &&
            output.profile.target_angle != null &&
            output.profile.target_angle.ToLower().Contains("birds");

        // Ground-level 감지: 카메라 위치는 바닥 근처, tilt는 약하게(0~12°) 분리
        isGroundLevelProfile =
            output.profile != null &&
            output.profile.target_angle != null &&
            output.profile.target_angle.ToLower().Contains("ground");

        targetTiltMin = elevationMin;
        targetTiltMax = elevationMax;

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
            sampleCameraHeightMin = 0.03f;  // shoes 기준 3cm 이상
            sampleCameraHeightMax = 0.30f;  // shoes 기준 30cm 이하
            targetTiltMin = 0f;
            targetTiltMax = 12f;
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
                  $"h/H=[{hOverH_min:F3},{hOverH_max:F3}]");

        // FOV가 h/H 계산에 영향을 주므로 후보 생성 전에 먼저 적용
        ApplyCameraFOV();
        GenerateCandidates();

        if (isBirdsEyeProfile)
        {
            // Bird's eye: trajectory 생성/재생 없이 static placement만 적용
            Debug.Log("[PCCG] Bird's-eye profile detected: using static placement mode.");
            ApplyStaticCameraPlacement();
        }
        else
        {
            // 일반/ground-level: placement + top-k trajectory 생성 및 재생
            GenerateTrajectoryCandidates();
            PlayBestTrajectory();
        }
    }
}