# 3DGS 자연어 감정 연출 카메라 후보 생성 시스템 — 정성 사용자 평가 프로토콜 & 설문지

> 본 문서는 종이 설문 / Google Form / 외부 Excel로 그대로 전환할 수 있도록 정리한 최종본이다.
> Unity 로그 스키마(§12)는 실제 구현된 `StudySessionLogger.cs`와 일치한다.

---

## 1. 평가 목적

사용자가 입력한 자연어 감정 연출 명령을 시스템이 해석하고, 그 결과 생성된 카메라 후보 장면이
사용자의 의도 감정을 얼마나 잘 전달하는지 확인한다. 두 관점:

1. **Intent parsing agreement** — 주어진 목표 감정 조건에 대해 입력한 명령을 시스템이 해당 감정 클래스로 해석했는지.
2. **Camera candidate evaluation** — 생성된 카메라 후보가 목표 감정과 명령을 시각적으로 반영했는지.

> 주의: 이는 fine-tuned emotion classifier의 정확도 평가가 **아니다**. 주어진 target emotion condition과
> 시스템의 parsed effect_class가 얼마나 일치하는지를 보는 **command-condition agreement**로 해석한다.

---

## 2. 사용 시스템

- Python 서버: **`server_gpt.py`** (GPT-4o + NRC-VAD 기반 intent parsing). 실행:
  `uvicorn server_gpt:app --port 8000` (server 쪽 `.venv` 사용).
- `local_classifier.py` 기반 모델(`server.py`)은 성능 검증이 충분치 않아 **본 평가에 미사용**.
- Unity 내부 rating UI는 만들지 않는다. 후보별/trial별/사전·사후 평가는 **외부 설문/Excel**에서 수집한다.

**Unity가 저장하는 로그(자동):** `trials.csv`, `candidates.csv`, `raw_responses.jsonl`, `screenshots/`
(`ratings.csv`는 사용하지 않음 — §12 참조).

---

## 3. 참가자 및 전체 구성

- 참가자 1명당 **20 trial** = 4 effect class(Tense / Delighted / Sadness / Relaxed) × 5.
- 같은 감정 클래스를 연속 제시하지 않는다(피로·후보 유사 방지). 권장 혼합순서는 **T→R→S→D 5회 반복**
  (전체 순서·프롬프트는 `trial_order.csv` 참조).
- 후보 수는 시간 단축을 위해 **trial당 3개(C1, C2, C3)**. (Unity: `candidatesPerTrial=3`, `CameraCandidateGenerator.topK=3`)
- 1명 기준 예상 소요 약 **30–45분**.

---

## 4. 사전 설문 (실험 시작 전) — `survey_pre_template.csv`

| 문항 | 내용 | 응답 | CSV 컬럼 |
|---|---|---|---|
| PRE01 | 실험 설명을 읽었으며 참여에 동의 | 예 / 아니오 | consent |
| PRE02 | 익명 참가자 ID | 단답 (예: P01) | participant_id |
| PRE03 | 영상 콘텐츠 관심도 | 1–5 | content_interest |
| PRE04 | 영화/카메라 구도 친숙도 | 1–5 | camera_familiarity |
| PRE05 | 연출/영상제작/촬영/편집 경험 | 없음 / 취미 수준 / 수업·프로젝트 / 실무 | production_experience |
| PRE06 | 3D/Unity/VR·AR/previz 친숙도 | 1–5 | unity_previz_familiarity |
| PRE07 | 간단한 영어 명령 작성 가능 여부 | 예 / 어느 정도 가능 / 아니오 | english_ability |
| PRE08 | 감정 의도를 한 문장으로 표현할 자신감 | 1–5 | emotion_expression_confidence |

5점 척도 공통: 1=전혀 그렇지 않음 … 5=매우 그러함.

---

## 5. 명령 입력 방식 (Guided free input)

참가자 안내문(그대로 제시):

> "각 trial마다 목표 감정 인상이 제시됩니다. 해당 감정이 장면에서 느껴지도록 영어 자연어 연출 명령을
> 한 문장으로 입력해 주세요. 카메라 배치를 지시한다고 생각하고 작성하면 됩니다. 목표 감정 단어만 단독으로
> 쓰지 말고, 장면·인물·분위기를 함께 묘사해 주세요. 예시 문장은 참고용이며 그대로 복사하지 마세요."

- 좋은 예: *Make the scene feel unsafe and oppressive.* / *Show the character in a quiet lonely moment.* /
  *Make the scene feel peaceful and safe.* / *Make the moment feel lively and full of energy.*
- 비권장: *Tense.* / *Sadness.* / *Move the camera.* / *Show the man.*

---

## 6. Russell 4분면 힌트 (단어 복붙 금지, 본인 표현 유도)

| 클래스 | 의미 | 힌트 단어 | 입력 방향 | 예시 |
|---|---|---|---|---|
| **Tense** | negative valence + high arousal | afraid, alarmed, angry, tense, frustrated, annoyed, distressed | 위협감·불안감·압박감·위험한 분위기·불편함·긴장감 | Make the scene feel unsafe and oppressive. |
| **Delighted** | positive valence + high arousal | astonished, excited, aroused, happy, delighted, glad, pleased | 활기·즐거움·희망감·생동감·에너지·고양감 | Make the moment feel lively and full of energy. |
| **Sadness** | negative valence + low arousal | miserable, sad, depressed, gloomy, bored, droopy | 외로움·공허함·상실감·쓸쓸함·감정적 거리감·고립감 | Make the character seem isolated in the empty room. |
| **Relaxed** | positive valence + low arousal | content, satisfied, at ease, serene, calm, relaxed, tired, sleepy | 평온함·안정감·편안함·안도감·고요함·긴장이 풀린 느낌 | Show the character in a quiet and comfortable moment. |

---

## 7. Trial 프롬프트 20개

각 trial에서 아래 목표 감정 조건 중 하나를 제시(제시 순서는 `trial_order.csv`의 혼합순서).

**Tense** — T1 위협받는 느낌 / T2 불안·불편 / T3 압박 / T4 위험한 분위기 / T5 구도의 불편·긴장
**Delighted** — D1 생동감·활기 / D2 희망적 / D3 즐겁고 긍정적 / D4 에너지·고양 / D5 기대·들뜸
**Sadness** — S1 외로움 / S2 비어있고 공허 / S3 감정적 거리 / S4 우울·쓸쓸 / S5 상실·고립
**Relaxed** — R1 평온 / R2 편안 / R3 차분 / R4 안전·안정 / R5 안도·이완

(전체 한국어 프롬프트 문장은 `trial_order.csv`의 `prompt_text` 열 참조.)

---

## 8. 각 Trial 진행 순서

1. 실험자가 target effect class와 trial prompt 제시.
2. 참가자가 영어 자연어 명령 한 문장 입력 → **Send**.
3. Unity가 `server_gpt.py`로 전송, 서버가 source_path / parsed_effect_class / intensity / confidence /
   view_preference / warning / profile 반환.
4. Unity가 profile로 PCCG 후보(C1–C3) 생성.
5. **자동 로그**: `trials.csv`(명령+파싱결과) + `candidates.csv`(후보 메타) + `raw_responses.jsonl`(원본) 기록.
   (Send 1회 = trial_index 1..20 자동 증가)
6. 참가자가 C1 → C2 → C3 순서로 확인.
7. 참가자가 **외부 설문/Excel**에 후보별 평가(§9) + trial 전체 평가(§10) 작성, best 후보 선택.
8. 다음 trial로.

(선택) 후보별 스크린샷이 필요하면 `screenshots/T{trial:00}_C{n}.png`로 저장 가능.

---

## 9. 후보별 평가 (C1·C2·C3 각각) — `rating_candidates_template.csv`

| 문항 | 내용 | 척도 | CSV 컬럼 |
|---|---|---|---|
| CAND_Q1 | 이 후보에서 목표 감정이 어느 정도 느껴졌는가 | 1–5 | emotion_convey_score |
| CAND_Q2 | 이 구도가 의도한 감정 연출에 얼마나 적합한가 | 1–5 | composition_score |
| CAND_Q3 | previz 카메라 제안으로서 얼마나 만족스러운가 | 1–5 | satisfaction_score |
| (best) | 이 후보가 trial의 best로 선택되었는가 | TRUE/FALSE | is_best |
| (비고) | 자유 코멘트 | 텍스트 | comment |

행 단위: 참가자 × trial × candidate(C1/C2/C3) → 참가자당 20×3 = 60행.

---

## 10. Trial 전체 평가 (C1–C3 모두 본 뒤) — `rating_trials_template.csv`

| 문항 | 내용 | 응답 | CSV 컬럼 |
|---|---|---|---|
| TRIAL_Q1 | 연출 의도와 가장 잘 맞는 후보 | C1 / C2 / C3 / 없음 | best_candidate |
| TRIAL_Q2 | 전체 후보군이 내 명령을 반영한 정도 | 1–5 | instruction_reflection |
| TRIAL_Q3 | 전체 후보군이 목표 감정을 표현한 정도 | 1–5 | emotion_expression |
| TRIAL_Q4 | 후보군이 카메라 배치 탐색에 유용한 정도 | 1–5 | usefulness |
| TRIAL_Q5 | best 선택 이유(또는 적합 후보 없음 이유) | 자유 서술 | reason |

행 단위: 참가자 × trial → 참가자당 20행.

---

## 11. 사후 설문 (20 trial 종료 후) — `survey_post_template.csv`

| 문항 | 내용 | 응답 | CSV 컬럼 |
|---|---|---|---|
| POST01 | 감정 연출 의도용 카메라 배치 탐색에 도움이 됨 | 1–5 | overall_usefulness |
| POST02 | 후보들이 전반적으로 내 명령을 반영함 | 1–5 | overall_command_reflection |
| POST03 | 후보들이 전반적으로 의도한 감정을 전달함 | 1–5 | overall_emotion_conveyance |
| POST04 | 감정 의도를 명령으로 표현하는 것이 쉬웠음 | 1–5 | command_input_ease |
| POST05 | trial 프롬프트가 충분히 명확했음 | 1–5 | prompt_clarity |
| POST06 | 여러 후보 비교가 선택에 도움이 됨 | 1–5 | candidate_comparison_usefulness |
| POST07 | 후보 간 차이가 명확·의미 있게 느껴짐 | 1–5 | candidate_diversity |
| POST08 | 표현하기 가장 쉬웠던 감정 | T/D/S/R / 차이 없음 | easiest_emotion |
| POST09 | 표현하기 가장 어려웠던 감정 | T/D/S/R / 차이 없음 | hardest_emotion |
| POST10 | 시스템이 반영하기 어려웠던 명령/상황 | 자유 서술 | system_limitations |
| POST11 | previz 도구로서 개선점 | 자유 서술 | improvements |

5점 척도 공통: 1=전혀 동의하지 않음 … 5=매우 동의함.

---

## 12. 로그 / 외부 평가표 스키마

### Unity 자동 저장 — `Application.persistentDataPath/StudyLogs/<participant>_<session>_<yyyyMMdd_HHmmss>/`

**trials.csv** (LogTrialIntent 시점 즉시 1행 + flush)
```
participant_id, session_id, trial_index, prompt_id, target_effect_class, user_command,
source_path, parsed_effect_class, intensity, confidence, view_preference, warning, timestamp
```

**candidates.csv** (후보당 1행, 최대 3행)
```
participant_id, session_id, trial_index, prompt_id, target_effect_class, candidate_id,
position_x, position_y, position_z, rotation_x, rotation_y, rotation_z,
total_score, h_over_h, elevation_deg, tilt_deg, angle_score, scale_score, view_score,
screenshot_path, timestamp
```
- `candidate_id` = **C1 / C2 / C3** (외부 평가표와 동일 표기로 조인).

**raw_responses.jsonl** — 한 줄 = `{participant_id, session_id, trial_index, prompt_id,
target_effect_class, user_command, timestamp, response:<서버 응답 원본 JSON 그대로>}`.

**screenshots/** — `T{trial:00}_C{n}.png` (선택).

> Unity는 `ratings.csv`를 만들지 않는다.

### 외부 수집(설문/Excel) — 본 폴더의 템플릿
- `rating_candidates_template.csv` (§9, 후보별)
- `rating_trials_template.csv` (§10, trial 전체)
- `survey_pre_template.csv` (§4), `survey_post_template.csv` (§11)

조인 키: `participant_id` + `session_id` + `trial_index` (+ `candidate_id`). prompt_id / target_effect_class는
Unity 로그와 외부표 양쪽에 두어 정합성 검증에 사용.

---

## 13. 분석 계획

### 13.1 Intent parsing agreement (`trials.csv`)
성공 조건: `source_path == "affective"` **AND** `parsed_effect_class == target_effect_class`.
- 전체 command-condition agreement / 감정별 agreement
- source_path 분포, Unknown 비율, cinematographic 유입 비율, warning 사례
- 표현 주의: classifier accuracy로 강하게 말하지 않고 **command-condition agreement**로 기술.

### 13.2 후보별 감정 전달 평가 (외부 후보표)
- emotion_convey / composition / satisfaction 각각 전체·감정별 평균 ± 표준편차.

### 13.3 Best candidate 분석
- 각 trial best 후보의 emotion_convey / composition / satisfaction 평균(전체·감정별).

### 13.4 Parsing 성공 ↔ 후보 평가 관계
- parsed_effect_class == target 인 trial vs 불일치 trial로 나눠 후보 점수 비교
  (예: matched vs mismatched의 emotion_convey_score 평균).

### 13.5 신뢰성 메모(권장)
- 참가자 N 보고, Likert는 평균±SD와 함께 분포/중앙값 병기, 소표본이면 비모수 비교(Mann–Whitney 등).

---

## 14. 결과 보고 (논문)

1. 실험 구성 요약(참가자 수, trial 수, 후보 수, effect class 구성)
2. Intent parsing agreement(전체/감정별, source_path 분포)
3. 후보 평가(emotion conveyance / composition / satisfaction, 전체·감정별 평균±SD)
4. Best candidate 결과(전체·감정별 평균)
5. 사후 설문(유용성/명령 반영/감정 전달/입력 난이도/비교 유용성/다양성)
6. 자유 의견 정리(잘 된 점 / 어려웠던 명령 / 개선점)

---

## 부록 A. 실행 체크리스트

1. 서버: `uvicorn server_gpt:app --port 8000` 가동(헬스 `/`로 provider 확인).
2. Unity: `CameraCandidateGenerator.topK = 3`.
3. `StudySessionLogger`: `participantId`/`sessionName` 입력 → 컴포넌트 우클릭 **"Fill recommended 20-trial plan"**
   (또는 `trial_order.csv` 보고 수동 입력), `candidatesPerTrial=3`, save 플래그 확인.
4. `IntentParserClient`의 `studyLogger` 슬롯 연결.
5. 사전 설문(§4) → 20 trial(§8, Send마다 자동 로그) → 후보/trial 평가(§9,§10 외부표) → 사후 설문(§11).
6. 종료 시 `StudyLogs/<...>/`에 trials/candidates/raw(jsonl)/screenshots 확인, `ratings.csv` 없음 확인.

## 부록 B. 5점 척도 라벨(설문 표기용)

- 관심/친숙/자신/적합/만족/감정전달: 1 전혀 ~ 3 보통 ~ 5 매우.
- 동의형(POST): 1 전혀 동의 안 함 ~ 5 매우 동의함.
