// StudySessionLogger.cs
// 정성 사용자 평가 실험용 로그 저장 컴포넌트.
//
// 한 참가자가 4개 감정 클래스(Tense/Delighted/Sadness/Relaxed)에 대해 각 5개씩,
// 총 20개 trial을 수행한다. 각 trial마다:
//   - 사용자가 입력한 자연어 명령
//   - 서버 intent parsing 결과(effect_class/source_path/intensity/confidence/view_preference/profile)
//   - Unity PCCG가 생성한 topCandidates 후보 정보
// 을 CSV/JSONL로 저장한다.
//
// ※ 이 컴포넌트는 기존 PCCG 후보 생성/필터링/scoring/trajectory 로직과 서버 통신 로직을
//    절대 수정하지 않는다. CameraCandidateGenerator.topCandidates는 "읽기만" 한다.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public class StudySessionLogger : MonoBehaviour
{
    [Header("Participant / Session")]
    public string participantId = "P01";
    public string sessionName = "pilot";
    public string trialOrderName = "A";
    public bool useTimestampedFolder = true;

    [Header("What to save")]
    public bool saveRawJson = true;
    public bool saveCandidateCsv = true;
    public bool saveTrialCsv = true;

    // (선택) 20개 trial 계획. 비워두면 prompt_id는 "T01".."T20", target은 빈 값으로 자동 채움.
    // SetCurrentTrialAuto(trialIndex)가 이 목록을 1-based 인덱스로 조회한다.
    [Serializable]
    public class TrialPlanEntry
    {
        public string promptId;            // 예: "T1", "R1"
        public string targetEffectClass;   // Tense / Delighted / Sadness / Relaxed
        [TextArea] public string promptText;   // 실험자 제시용(로그에는 저장하지 않음)
    }
    [Header("Optional 20-trial plan (index 1..20)")]
    public List<TrialPlanEntry> trialPlan = new List<TrialPlanEntry>();

    [Header("Candidates")]
    [Tooltip("trial당 기록할 후보 수 상한 (키 1~5, 후보 C1~C5 기준). CameraCandidateGenerator.topK도 5 권장.")]
    public int candidatesPerTrial = 5;

    // ── CSV headers (스펙 고정) ────────────────────────────────────────────────
    private const string TRIALS_HEADER =
        "participant_id,session_id,trial_index,prompt_id,target_effect_class,user_command,source_path,parsed_effect_class,intensity,confidence,view_preference,warning,timestamp,latency_s";
    // ★ 기존 컬럼 순서는 유지하고 끝에 assigned_key만 추가. assigned_key = 이 순위 후보(C1~C5)가
    //   이번 시행에서 몇 번 키(1~5)에 셔플 배정됐는지. candidate_id/total_score 등은 여전히 실제 순위 기준.
    private const string CANDIDATES_HEADER =
        "participant_id,session_id,trial_index,prompt_id,target_effect_class,candidate_id,position_x,position_y,position_z,rotation_x,rotation_y,rotation_z,total_score,h_over_h,elevation_deg,tilt_deg,angle_score,scale_score,view_score,timestamp,assigned_key";
    // 키 입력 로그(셔플된 표시 매핑으로 어떤 키가 어떤 순위 후보를 보여줬는지) 전용 CSV.
    private const string KEYPRESSES_HEADER =
        "participant_id,session_id,trial_index,prompt_id,key_pressed,shown_candidate_id,shown_rank,press_count,timestamp";

    // ── Session state ─────────────────────────────────────────────────────────
    private bool _sessionStarted;
    private string _sessionId;
    private string _sessionFolder;
    private StreamWriter _trialsWriter;
    private StreamWriter _candidatesWriter;
    private StreamWriter _rawWriter;
    private StreamWriter _keyPressesWriter;

    // 현재 시행의 누적 키 입력 횟수(같은 키 재입력 포함, 1부터 증가). 시행이 바뀌면 0으로 리셋.
    private int _keyPressCount;

    // ── Current trial buffer ──────────────────────────────────────────────────
    // candidates.csv가 prompt_id/target_effect_class를 쓰기 위해 현재 trial 식별자만 보관.
    // trials.csv 행은 LogTrialIntent() 시점에 13컬럼이 모두 확보되어 즉시 기록된다.
    private int _curTrialIndex;
    private string _curPromptId = "";
    private string _curTargetClass = "";

    // ── Public API ────────────────────────────────────────────────────────────

    public void StartSession()
    {
        if (_sessionStarted) return;

        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string folderName = participantId + "_" + sessionName;
        if (useTimestampedFolder) folderName += "_" + ts;
        _sessionId = folderName;

        try
        {
            string root = Application.persistentDataPath + "/StudyLogs/";
            _sessionFolder = Path.Combine(root, folderName);
            Directory.CreateDirectory(_sessionFolder);

            var csvEnc = new UTF8Encoding(true);   // BOM → Excel에서 한글 정상 표시
            var jsonEnc = new UTF8Encoding(false);

            if (saveTrialCsv)
            {
                _trialsWriter = new StreamWriter(Path.Combine(_sessionFolder, "trials.csv"), false, csvEnc);
                _trialsWriter.WriteLine(TRIALS_HEADER);
                _trialsWriter.Flush();
            }
            if (saveCandidateCsv)
            {
                _candidatesWriter = new StreamWriter(Path.Combine(_sessionFolder, "candidates.csv"), false, csvEnc);
                _candidatesWriter.WriteLine(CANDIDATES_HEADER);
                _candidatesWriter.Flush();

                // 키 입력 로그도 후보 CSV 토글과 함께 생성.
                _keyPressesWriter = new StreamWriter(Path.Combine(_sessionFolder, "key_presses.csv"), false, csvEnc);
                _keyPressesWriter.WriteLine(KEYPRESSES_HEADER);
                _keyPressesWriter.Flush();
            }
            if (saveRawJson)
            {
                _rawWriter = new StreamWriter(Path.Combine(_sessionFolder, "raw_responses.jsonl"), false, jsonEnc);
            }

            _sessionStarted = true;
            Debug.Log("[StudySessionLogger] session started -> " + _sessionFolder);
        }
        catch (Exception e)
        {
            Debug.LogError("[StudySessionLogger] failed to start session: " + e.Message);
            _sessionStarted = false;
        }
    }

    public void SetCurrentTrial(int trialIndex, string promptId, string targetEffectClass)
    {
        EnsureSession();
        _curTrialIndex = trialIndex;
        _curPromptId = promptId ?? "";
        _curTargetClass = targetEffectClass ?? "";

        // 새 시행 시작 → 누적 키 입력 횟수 리셋. SetCurrentTrialAuto도 이 메서드를 거치므로 함께 처리된다.
        _keyPressCount = 0;
    }

    // 편의 메서드: trialPlan(또는 기본값)에서 prompt_id/target을 채워 SetCurrentTrial 호출.
    public void SetCurrentTrialAuto(int trialIndex)
    {
        string promptId = "T" + trialIndex.ToString("00", CultureInfo.InvariantCulture);
        string targetClass = "";
        int i = trialIndex - 1;
        if (trialPlan != null && i >= 0 && i < trialPlan.Count && trialPlan[i] != null)
        {
            if (!string.IsNullOrEmpty(trialPlan[i].promptId)) promptId = trialPlan[i].promptId;
            targetClass = trialPlan[i].targetEffectClass ?? "";
        }
        SetCurrentTrial(trialIndex, promptId, targetClass);
    }

    // 프로토콜 권장 20-trial 혼합순서(T→R→S→D 5회 반복, 같은 감정 연속 금지)로 trialPlan을 채운다.
    // Inspector 컴포넌트 우클릭 → "Fill recommended 20-trial plan". 이후 수동 편집 가능.
    [ContextMenu("Fill recommended 20-trial plan")]
    public void FillRecommendedTrialPlan()
    {
        string[] cls = { "Tense", "Relaxed", "Sadness", "Delighted" };
        string[] pre = { "T", "R", "S", "D" };
        string[][] txt =
        {
            new[]
            {
                "인물 또는 장면이 위협받는 느낌이 들도록 연출해 주세요.",
                "장면이 불안하거나 불편한 느낌이 들도록 연출해 주세요.",
                "인물이 압박을 받는 것처럼 느껴지도록 연출해 주세요.",
                "장면의 분위기가 위험하게 느껴지도록 연출해 주세요.",
                "카메라 구도에서 불편함이나 긴장감이 느껴지도록 연출해 주세요.",
            },
            new[]
            {
                "장면이 평온하게 느껴지도록 연출해 주세요.",
                "인물이 편안한 상태처럼 느껴지도록 연출해 주세요.",
                "분위기가 차분하게 느껴지도록 연출해 주세요.",
                "순간이 안전하고 안정적으로 느껴지도록 연출해 주세요.",
                "인물이 안도하거나 긴장이 풀린 것처럼 느껴지도록 연출해 주세요.",
            },
            new[]
            {
                "인물이 외롭게 느껴지도록 연출해 주세요.",
                "장면이 비어 있고 공허하게 느껴지도록 연출해 주세요.",
                "인물이 감정적으로 멀어진 것처럼 느껴지도록 연출해 주세요.",
                "순간이 우울하거나 쓸쓸하게 느껴지도록 연출해 주세요.",
                "장면에서 상실감이나 고립감이 느껴지도록 연출해 주세요.",
            },
            new[]
            {
                "장면이 생동감 있고 활기차게 느껴지도록 연출해 주세요.",
                "인물 또는 장면에서 희망적인 느낌이 들도록 연출해 주세요.",
                "순간이 즐겁고 긍정적으로 느껴지도록 연출해 주세요.",
                "장면이 에너지 있고 고양된 느낌이 들도록 연출해 주세요.",
                "인물이 기대감이나 들뜬 감정을 가진 것처럼 느껴지도록 연출해 주세요.",
            },
        };

        trialPlan = new List<TrialPlanEntry>(20);
        for (int round = 1; round <= 5; round++)
            for (int k = 0; k < 4; k++)
                trialPlan.Add(new TrialPlanEntry
                {
                    promptId = pre[k] + round.ToString(CultureInfo.InvariantCulture),
                    targetEffectClass = cls[k],
                    promptText = txt[k][round - 1],
                });
        Debug.Log("[StudySessionLogger] trialPlan filled with 20 recommended trials.");
    }

    // rawResponseJson: 서버 응답 원본 문자열(IntentParserClient의 responseJson). 있으면 raw jsonl에 그대로 저장.
    // latencySeconds: Send 클릭 → 후보 생성 완료까지의 총 시간(초). 음수면 미측정으로 빈 값 기록.
    public void LogTrialIntent(string userCommand, IntentOutputData output, string rawResponseJson = null, float latencySeconds = -1f)
    {
        EnsureSession();
        if (_curTrialIndex <= 0) _curTrialIndex = 1;   // SetCurrentTrial 누락 시 안전장치

        // trials.csv: 14컬럼이 이 시점에 모두 확보됨 → 즉시 1행 기록 + flush (유실 방지)
        if (saveTrialCsv && _trialsWriter != null)
        {
            string row = string.Join(",", new string[]
            {
                Csv(participantId), Csv(_sessionId),
                _curTrialIndex.ToString(CultureInfo.InvariantCulture),
                Csv(_curPromptId), Csv(_curTargetClass), Csv(userCommand ?? ""),
                Csv(output != null ? output.source_path : ""),
                Csv(output != null ? output.effect_class : ""),
                Csv(output != null ? output.intensity : ""),
                Csv(output != null ? output.confidence : ""),
                Csv(output != null ? output.view_preference : ""),
                Csv(output != null ? output.warning : ""),
                Csv(Now()),
                latencySeconds >= 0f ? F(latencySeconds) : ""
            });
            _trialsWriter.WriteLine(row);
            _trialsWriter.Flush();
        }

        WriteRawJson(userCommand, rawResponseJson);
    }

    public void LogCandidates(int trialIndex, CameraCandidateGenerator generator)
    {
        EnsureSession();
        if (!saveCandidateCsv || _candidatesWriter == null) return;
        if (generator == null || generator.topCandidates == null) return;

        var list = generator.topCandidates;
        int n = Mathf.Min(list.Count, Mathf.Max(0, candidatesPerTrial));   // trial당 3개(C1/C2/C3)
        for (int i = 0; i < n; i++)
        {
            var c = list[i];
            if (c == null) continue;

            string candidateId = "C" + (i + 1).ToString(CultureInfo.InvariantCulture);   // C1~C5 (실제 순위 기준)
            Vector3 euler = c.rotation.eulerAngles;

            // assigned_key: 이 순위 후보(index i)가 이번 시행에서 몇 번 키에 배정됐는지 역참조.
            // 매핑 조회가 불가하면(-1) 빈 값으로 둔다.
            int assignedKey = generator.GetKeyNumberForCandidateIndex(i);
            string assignedKeyStr = assignedKey > 0
                ? assignedKey.ToString(CultureInfo.InvariantCulture)
                : "";

            string row = string.Join(",", new string[]
            {
                Csv(participantId), Csv(_sessionId),
                trialIndex.ToString(CultureInfo.InvariantCulture),
                Csv(_curPromptId), Csv(_curTargetClass), Csv(candidateId),
                F(c.position.x), F(c.position.y), F(c.position.z),
                F(euler.x), F(euler.y), F(euler.z),
                F(c.totalScore), F(c.hOverH), F(c.elevationDeg), F(c.tiltDeg),
                F(c.angleScore), F(c.scaleScore), F(c.viewScore),
                Csv(Now()), assignedKeyStr
            });
            _candidatesWriter.WriteLine(row);
        }
        _candidatesWriter.Flush();
    }

    // 키 입력 1회를 key_presses.csv에 기록한다. ApplyCandidateByIndex가 후보를 표시할 때마다 호출.
    //   keyPressed:        사용자가 실제로 누른 키 번호(1~5)
    //   shownCandidateId:  표시된 후보 id("C"+rank)
    //   shownRank:         표시된 후보의 실제 순위(top 몇 위)
    // [검증 포인트] 같은 시행에서 키를 N번 누르면 N줄이 쌓이고 press_count가 1→N으로 증가한다.
    //   같은 키를 재입력하면(셔플 매핑이 고정이므로) shown_candidate_id/shown_rank가 동일하게 기록된다.
    public void LogKeyPress(int keyPressed, string shownCandidateId, int shownRank)
    {
        EnsureSession();
        if (!saveCandidateCsv || _keyPressesWriter == null) return;

        _keyPressCount++;   // 현재 시행 누적 입력 횟수(1부터). SetCurrentTrial에서 0으로 리셋됨.

        string row = string.Join(",", new string[]
        {
            Csv(participantId), Csv(_sessionId),
            _curTrialIndex.ToString(CultureInfo.InvariantCulture),
            Csv(_curPromptId),
            keyPressed.ToString(CultureInfo.InvariantCulture),
            Csv(shownCandidateId ?? ""),
            shownRank.ToString(CultureInfo.InvariantCulture),
            _keyPressCount.ToString(CultureInfo.InvariantCulture),
            Csv(Now())
        });
        _keyPressesWriter.WriteLine(row);
        _keyPressesWriter.Flush();
    }

    // 후보별/trial 전체 평가는 프로토콜상 외부 설문/엑셀에서 수집한다(Unity 미저장).
    // 따라서 LogCandidateRating / LogOverallTrialEvaluation / ratings.csv 는 두지 않는다.
    // 후보 스크린샷도 저장하지 않는다 — 후보 pose(position/rotation)가 CSV에 있어 재현 가능.

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void EnsureSession()
    {
        if (!_sessionStarted) StartSession();
    }

    private void WriteRawJson(string userCommand, string rawResponseJson)
    {
        if (!saveRawJson || _rawWriter == null) return;
        // response에는 서버 응답 원본 JSON을 그대로 삽입(이미 valid JSON). 없으면 null.
        string resp = string.IsNullOrEmpty(rawResponseJson) ? "null" : rawResponseJson.Trim();
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append("\"participant_id\":").Append(JsonStr(participantId)).Append(',');
        sb.Append("\"session_id\":").Append(JsonStr(_sessionId)).Append(',');
        sb.Append("\"trial_index\":").Append(_curTrialIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"prompt_id\":").Append(JsonStr(_curPromptId)).Append(',');
        sb.Append("\"target_effect_class\":").Append(JsonStr(_curTargetClass)).Append(',');
        sb.Append("\"user_command\":").Append(JsonStr(userCommand ?? "")).Append(',');
        sb.Append("\"timestamp\":").Append(JsonStr(Now())).Append(',');
        sb.Append("\"response\":").Append(resp);
        sb.Append('}');
        _rawWriter.WriteLine(sb.ToString());
        _rawWriter.Flush();
    }

    // JSON 문자열 값 escaping (양끝 따옴표 포함).
    private static string JsonStr(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm.fff", CultureInfo.InvariantCulture);
    }

    // 문화권 무관 소수점('.') 보장 — 한국어 로케일에서 ','로 찍혀 CSV가 깨지는 것 방지
    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "";
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // CSV escaping: 쉼표/따옴표/줄바꿈 포함 시 따옴표로 감싸고 내부 따옴표는 두 번으로.
    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needQuote = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
                         || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
        if (needQuote) return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private void OnApplicationQuit()
    {
        CloseSession();
    }

    public void CloseSession()
    {
        CloseWriter(ref _trialsWriter);
        CloseWriter(ref _candidatesWriter);
        CloseWriter(ref _keyPressesWriter);
        CloseWriter(ref _rawWriter);
        _sessionStarted = false;
    }

    private static void CloseWriter(ref StreamWriter w)
    {
        if (w == null) return;
        try { w.Flush(); w.Close(); }
        catch (Exception e) { Debug.LogError("[StudySessionLogger] close failed: " + e.Message); }
        w = null;
    }
}
