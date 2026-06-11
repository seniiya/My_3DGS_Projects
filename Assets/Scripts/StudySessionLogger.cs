// StudySessionLogger.cs
// 정성 사용자 평가 실험용 로그 저장 컴포넌트.
//
// 한 참가자가 4개 감정 클래스(Tense/Delighted/Sadness/Relaxed)에 대해 각 5개씩,
// 총 20개 trial을 수행한다. 각 trial마다:
//   - 사용자가 입력한 자연어 명령
//   - 서버 intent parsing 결과(effect_class/source_path/intensity/confidence/view_preference/profile)
//   - Unity PCCG가 생성한 topCandidates 후보 정보
//   - (선택) 후보별/전체 사용자 평가 점수, 스크린샷
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
    public bool saveRatingCsv = true;

    // (선택) 20개 trial 계획. 비워두면 prompt_id는 "T01".."T20", target은 빈 값으로 자동 채움.
    // SetCurrentTrialAuto(trialIndex)가 이 목록을 1-based 인덱스로 조회한다.
    [Serializable]
    public class TrialPlanEntry
    {
        public string promptId;            // 예: "Tense_01"
        public string targetEffectClass;   // Tense / Delighted / Sadness / Relaxed
    }
    [Header("Optional 20-trial plan (index 1..20)")]
    public List<TrialPlanEntry> trialPlan = new List<TrialPlanEntry>();

    // ── CSV headers (스펙 고정) ────────────────────────────────────────────────
    private const string TRIALS_HEADER =
        "participant_id,session_id,trial_index,prompt_id,target_effect_class,user_command,source_path,parsed_effect_class,intensity,confidence,view_preference,warning,best_candidate,overall_instruction_reflection,overall_emotion_expression,overall_usefulness,timestamp";
    private const string CANDIDATES_HEADER =
        "participant_id,session_id,trial_index,candidate_id,position_x,position_y,position_z,rotation_x,rotation_y,rotation_z,total_score,h_over_h,elevation_deg,tilt_deg,angle_score,scale_score,view_score,screenshot_path,timestamp";
    // ratings.csv는 스펙의 파일 목록엔 없지만 saveRatingCsv 플래그와 LogCandidateRating()이 있어
    // 후보별 평가를 담을 파일로 추가한다.
    private const string RATINGS_HEADER =
        "participant_id,session_id,trial_index,candidate_id,emotion_score,composition_score,satisfaction_score,is_best,comment,timestamp";

    // ── Session state ─────────────────────────────────────────────────────────
    private bool _sessionStarted;
    private string _sessionId;
    private string _sessionFolder;
    private string _screenshotFolder;
    private StreamWriter _trialsWriter;
    private StreamWriter _candidatesWriter;
    private StreamWriter _ratingsWriter;
    private StreamWriter _rawWriter;

    // ── Current trial buffer ──────────────────────────────────────────────────
    // trials.csv 한 행은 intent(전송 시점) + overall 평가(이후 시점)를 합쳐야 완성되므로
    // 버퍼에 모았다가 LogOverallTrialEvaluation() 또는 다음 trial 시작/종료 시 1행으로 flush 한다.
    private bool _hasPendingTrial;
    private bool _curTrialRowWritten;
    private int _curTrialIndex;
    private string _curPromptId = "";
    private string _curTargetClass = "";
    private string _curUserCommand = "";
    private string _curSourcePath = "";
    private string _curParsedEffect = "";
    private string _curIntensity = "";
    private string _curConfidence = "";
    private string _curViewPref = "";
    private string _curWarning = "";
    private bool _curIntentLogged;
    private string _curBestCandidate = "";
    private int _curInstrRefl;
    private int _curEmoExpr;
    private int _curUseful;
    private bool _curOverallLogged;

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
            _screenshotFolder = Path.Combine(_sessionFolder, "screenshots");
            Directory.CreateDirectory(_screenshotFolder);

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
            }
            if (saveRatingCsv)
            {
                _ratingsWriter = new StreamWriter(Path.Combine(_sessionFolder, "ratings.csv"), false, csvEnc);
                _ratingsWriter.WriteLine(RATINGS_HEADER);
                _ratingsWriter.Flush();
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
        FinalizePendingTrial();   // 이전 trial이 overall 평가 없이 끝났어도 intent 행은 남긴다.

        _hasPendingTrial = true;
        _curTrialRowWritten = false;
        _curTrialIndex = trialIndex;
        _curPromptId = promptId ?? "";
        _curTargetClass = targetEffectClass ?? "";
        _curUserCommand = "";
        _curSourcePath = "";
        _curParsedEffect = "";
        _curIntensity = "";
        _curConfidence = "";
        _curViewPref = "";
        _curWarning = "";
        _curIntentLogged = false;
        _curBestCandidate = "";
        _curInstrRefl = 0;
        _curEmoExpr = 0;
        _curUseful = 0;
        _curOverallLogged = false;
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

    public void LogTrialIntent(string userCommand, IntentOutputData output)
    {
        EnsureSession();
        if (!_hasPendingTrial)
        {
            // SetCurrentTrial 없이 호출된 경우의 안전장치
            _hasPendingTrial = true;
            _curTrialRowWritten = false;
            _curOverallLogged = false;
            if (_curTrialIndex <= 0) _curTrialIndex = 1;
        }

        _curUserCommand = userCommand ?? "";
        if (output != null)
        {
            _curSourcePath = output.source_path ?? "";
            _curParsedEffect = output.effect_class ?? "";
            _curIntensity = output.intensity ?? "";
            _curConfidence = output.confidence ?? "";
            _curViewPref = output.view_preference ?? "";
            _curWarning = output.warning ?? "";
        }
        _curIntentLogged = true;

        WriteRawJson(userCommand, output);   // intent는 즉시 jsonl로 남겨 유실 방지
    }

    public void LogCandidates(int trialIndex, CameraCandidateGenerator generator)
    {
        EnsureSession();
        if (!saveCandidateCsv || _candidatesWriter == null) return;
        if (generator == null || generator.topCandidates == null) return;

        var list = generator.topCandidates;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (c == null) continue;

            string candidateId = "c" + i.ToString(CultureInfo.InvariantCulture);
            Vector3 euler = c.rotation.eulerAngles;
            // 스크린샷은 CaptureCandidateScreenshot()이 같은 규칙으로 저장하므로 경로를 미리 기록.
            string shotPath = "screenshots/T" + trialIndex.ToString("00", CultureInfo.InvariantCulture)
                              + "_" + candidateId + ".png";

            string row = string.Join(",", new string[]
            {
                Csv(participantId), Csv(_sessionId),
                trialIndex.ToString(CultureInfo.InvariantCulture), Csv(candidateId),
                F(c.position.x), F(c.position.y), F(c.position.z),
                F(euler.x), F(euler.y), F(euler.z),
                F(c.totalScore), F(c.hOverH), F(c.elevationDeg), F(c.tiltDeg),
                F(c.angleScore), F(c.scaleScore), F(c.viewScore),
                Csv(shotPath), Csv(Now())
            });
            _candidatesWriter.WriteLine(row);
        }
        _candidatesWriter.Flush();
    }

    public void LogCandidateRating(int trialIndex, string candidateId, int emotionScore,
                                   int compositionScore, int satisfactionScore, bool isBest, string comment)
    {
        EnsureSession();
        if (!saveRatingCsv || _ratingsWriter == null) return;

        string row = string.Join(",", new string[]
        {
            Csv(participantId), Csv(_sessionId),
            trialIndex.ToString(CultureInfo.InvariantCulture), Csv(candidateId),
            emotionScore.ToString(CultureInfo.InvariantCulture),
            compositionScore.ToString(CultureInfo.InvariantCulture),
            satisfactionScore.ToString(CultureInfo.InvariantCulture),
            isBest ? "true" : "false",
            Csv(comment), Csv(Now())
        });
        _ratingsWriter.WriteLine(row);
        _ratingsWriter.Flush();
    }

    public void LogOverallTrialEvaluation(int trialIndex, string bestCandidate,
                                          int instructionReflection, int emotionExpression, int usefulness)
    {
        EnsureSession();
        if (!_hasPendingTrial || _curTrialIndex != trialIndex)
            _curTrialIndex = trialIndex;   // 안전: 현재 버퍼와 trialIndex가 어긋나면 맞춰준다.

        _curBestCandidate = bestCandidate ?? "";
        _curInstrRefl = instructionReflection;
        _curEmoExpr = emotionExpression;
        _curUseful = usefulness;
        _curOverallLogged = true;

        WriteTrialRow();      // intent + overall을 합쳐 trials.csv 1행 완성 + flush
        _hasPendingTrial = false;
    }

    public string CaptureCandidateScreenshot(int trialIndex, string candidateId, Camera previewCamera)
    {
        if (previewCamera == null)
        {
            Debug.LogWarning("[StudySessionLogger] CaptureCandidateScreenshot: previewCamera is null.");
            return "";
        }
        EnsureSession();
        if (string.IsNullOrEmpty(_screenshotFolder)) return "";

        int w = previewCamera.pixelWidth > 0 ? previewCamera.pixelWidth : Screen.width;
        int h = previewCamera.pixelHeight > 0 ? previewCamera.pixelHeight : Screen.height;
        if (w <= 0) w = 1280;
        if (h <= 0) h = 720;

        RenderTexture rt = new RenderTexture(w, h, 24);
        RenderTexture prevTarget = previewCamera.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        string relPath = "";
        try
        {
            previewCamera.targetTexture = rt;
            previewCamera.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);

            byte[] png = tex.EncodeToPNG();
            string fileName = "T" + trialIndex.ToString("00", CultureInfo.InvariantCulture)
                              + "_" + SanitizeFileName(candidateId) + ".png";
            File.WriteAllBytes(Path.Combine(_screenshotFolder, fileName), png);
            relPath = "screenshots/" + fileName;
        }
        catch (Exception e)
        {
            Debug.LogError("[StudySessionLogger] screenshot failed: " + e.Message);
        }
        finally
        {
            // 원래 카메라 상태 복원 — preview/PCCG 동작에 영향 없음
            previewCamera.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            if (Application.isPlaying) { Destroy(rt); Destroy(tex); }
            else { DestroyImmediate(rt); DestroyImmediate(tex); }
        }
        return relPath;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void EnsureSession()
    {
        if (!_sessionStarted) StartSession();
    }

    private void FinalizePendingTrial()
    {
        if (_hasPendingTrial && _curIntentLogged && !_curTrialRowWritten)
            WriteTrialRow();
        _hasPendingTrial = false;
    }

    private void WriteTrialRow()
    {
        if (_curTrialRowWritten) return;
        if (!saveTrialCsv || _trialsWriter == null) { _curTrialRowWritten = true; return; }

        string instr = _curOverallLogged ? _curInstrRefl.ToString(CultureInfo.InvariantCulture) : "";
        string emo = _curOverallLogged ? _curEmoExpr.ToString(CultureInfo.InvariantCulture) : "";
        string use = _curOverallLogged ? _curUseful.ToString(CultureInfo.InvariantCulture) : "";
        string best = _curOverallLogged ? _curBestCandidate : "";

        string row = string.Join(",", new string[]
        {
            Csv(participantId), Csv(_sessionId),
            _curTrialIndex.ToString(CultureInfo.InvariantCulture),
            Csv(_curPromptId), Csv(_curTargetClass), Csv(_curUserCommand),
            Csv(_curSourcePath), Csv(_curParsedEffect), Csv(_curIntensity), Csv(_curConfidence),
            Csv(_curViewPref), Csv(_curWarning), Csv(best),
            instr, emo, use, Csv(Now())
        });
        _trialsWriter.WriteLine(row);
        _trialsWriter.Flush();
        _curTrialRowWritten = true;
    }

    private void WriteRawJson(string userCommand, IntentOutputData output)
    {
        if (!saveRawJson || _rawWriter == null) return;
        var line = new RawLogLine
        {
            participant_id = participantId,
            session_id = _sessionId,
            trial_index = _curTrialIndex,
            prompt_id = _curPromptId,
            target_effect_class = _curTargetClass,
            user_command = userCommand ?? "",
            timestamp = Now(),
            response = output
        };
        _rawWriter.WriteLine(JsonUtility.ToJson(line));
        _rawWriter.Flush();
    }

    [Serializable]
    private class RawLogLine
    {
        public string participant_id;
        public string session_id;
        public int trial_index;
        public string prompt_id;
        public string target_effect_class;
        public string user_command;
        public string timestamp;
        public IntentOutputData response;
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

    private static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "candidate";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            sb.Append((char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') ? ch : '_');
        return sb.ToString();
    }

    private void OnApplicationQuit()
    {
        CloseSession();
    }

    public void CloseSession()
    {
        FinalizePendingTrial();
        CloseWriter(ref _trialsWriter);
        CloseWriter(ref _candidatesWriter);
        CloseWriter(ref _ratingsWriter);
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
