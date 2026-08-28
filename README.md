# My_3DGS_Projects

**공유 폴더 ➡️ 로컬로 내려받아서 실행 ／ 깃허브 ➡️ 로컬로 내려받아서 실행**

3D Gaussian Splatting 씬에서 **자연어 지시문을 감정 기반 카메라 배치로 변환**하는 프리비즈 시스템의 Unity 클라이언트. (VRST 투고용)

서버 · 분석 코드는 별도 저장소: **[seniiya/3DGS_Project_Server](https://github.com/seniiya/3DGS_Project_Server.git)**

```
[이 저장소: Unity]  지시문 입력
        │  POST /parse-intent   (IntentParserClient.cs)
        ▼
[서버 저장소: FastAPI]  감정 분류 → effect_class / intensity / source_path
        │  프로파일 반환
        ▼
[이 저장소]  후보 카메라 5개 생성 → 키 매핑 셔플 → 참가자가 1–5 키로 비교·선택
        │  trials / candidates / key_presses / ratings 로깅
        ▼
[서버 저장소]  분석 스크립트 → out/*.csv
```

---

## 실행

Unity 에디터로 이 프로젝트를 열고 `Assets/Scenes/SampleScene.unity` 를 실행한다.
사전에 서버가 떠 있어야 한다 (`uvicorn server:app --port 8000`).
엔드포인트는 `IntentParserClient.cs` 에서 지정한다.

> 저장소를 클론한 직후에는 **메시·3DGS 자산이 비어 있다.** 아래 "저장소에 포함되지 않은 자산" 참고.

---

## `Assets/Scripts/` — 시스템 본체

| 파일 | 역할 |
|---|---|
| `CameraCandidateGenerator.cs` | **핵심.** 감정 프로파일을 받아 후보 카메라 포즈를 생성·점수화·정렬한다. 구면 샘플링 → 앵글/스케일/뷰 점수 합산(`total_score`) → 상위 후보 선정. `ShuffleCandidateKeyMapping()` 이 매 시행 **키→후보 표시 매핑을 Fisher-Yates로 셔플**해 화면 위치 편향을 제거하고, 그 매핑을 `key_mapping` 문자열 / `assigned_key` 컬럼으로 기록한다. |
| `CameraProfileData.cs` | 감정 클래스별 카메라 프로파일 정의 (앵글 범위, 거리, 높이비 등 목표값). |
| `FibonacciSphere.cs` | 피보나치 구면 샘플링 — 후보 카메라 위치를 균일 분포로 생성. |
| `IntentParserClient.cs` | 서버 `/parse-intent` 호출 클라이언트. 지시문 전송 및 파싱 결과 수신. |
| `StudySessionLogger.cs` | **실험 로깅.** 세션당 `trials.csv` / `candidates.csv` / `key_presses.csv` / `raw_responses.jsonl` 을 기록한다. 서버 저장소의 `user_master.py` 가 이 출력을 입력으로 받는다. |
| `PCCGFigureDebugViewer.cs` | 후보 생성 과정 시각화 · 논문 그림 생성용 디버그 뷰어. |
| `SimpleWaypointWalker.cs` | 씬 내 캐릭터 이동(웨이포인트 순회) — 촬영 대상 액션 제공. |
| `SnapCharacterToPlane.cs` | 캐릭터를 바닥 평면에 정렬. 3DGS 메시의 바닥 높이 보정용. |

`Assets/idle.cs` 는 캐릭터 대기 애니메이션 제어용 보조 스크립트다.

---

## `Assets/StudyProtocol/` — 실험 프로토콜

| 파일 | 역할 |
|---|---|
| `evaluation_protocol.md` | 사용자 실험 진행 절차서. |
| `trial_order.csv` | 시행 순서 / 프롬프트 ID(`T01`–`T20`) 정의. |
| `rating_trials_template.csv` | 시행 단위 평가 기록지 양식 (`user_best_key`, `trial_satisfaction`). |
| `rating_candidates_template.csv` | 후보 단위 평가 기록지 양식 (키별 만족도). |
| `survey_pre_template.csv` | 사전 설문 양식 (인구통계·촬영 경험·VR 경험 등). |
| `survey_post_template.csv` | 사후 설문 양식 (Q1–Q9 리커트 + Q10·Q11 서술형). |

---

## 그 외 폴더

| 경로 | 내용 |
|---|---|
| `Assets/Scenes/` | 씬 파일. `SampleScene.unity` 가 실험에 쓰인 메인 씬. `Lab Room.unity`, `test 3dgs.unity`, `New Scene.unity` 는 개발·테스트용. |
| `Assets/GaussianAssets/` | 3DGS 스플랫 애셋 정의(`.asset`). 실제 가중치(`.bytes`, `.ply`, `.spz`)는 미포함. |
| `Assets/Gsplat/` | 3DGS 렌더링 관련 리소스. |
| `Assets/Adventure_Character/` | 촬영 대상 캐릭터 (프리팹·머티리얼·텍스처). 메시 원본은 미포함. |
| `Assets/Animation/` | 캐릭터 애니메이션 클립 (`Idle`, `Sitting`, `Acknowledging` 등). `AC_Man.controller`·`ManAnimator.controller` 가 상태를 관리. |
| `Assets/Mesh/` | 프록시 메시 정의 — 3DGS 씬의 충돌·바닥 판정용. 메시 원본은 미포함. |
| `Assets/Settings/` | 렌더 파이프라인(URP) 설정. |
| `Assets/TextMesh Pro/` | UI 폰트 애셋. |
| `ProjectSettings/`, `Packages/` | Unity 프로젝트·패키지 설정. |

---

## 저장소에 포함되지 않은 자산

GitHub 용량 한계(파일당 100MB)와 재배포 권리 문제로 **바이너리 자산은 추적하지 않는다** (`.gitignore` 참고). 코드·설정·씬 정의는 모두 포함되어 있다.

| 제외 대상 | 비고 |
|---|---|
| `Assets/Ida Faber/` | 3DGS 원본 캡처 자산, **약 3.2GB** (1,406 파일). |
| `*.fbx`, `*.FBX` | 메시·리그 원본. `character.fbx` 47MB, `Space_realmeshfbx.fbx` 28MB, `labRoom2.fbx` 16MB 등. |
| `*.ply`, `*.bytes`, `*.spz` | 3DGS 스플랫 가중치. |
| `Library/`, `Temp/`, `Build/` 등 | Unity 자동 생성물. |

`.meta` 파일은 계속 추적한다 — Unity GUID가 보존되므로 자산을 같은 경로에 되돌려 놓으면 씬 참조가 그대로 복원된다.

---

## 실험 데이터 관련

참가자 실험 로그와 분석 결과는 모두 **서버 저장소**의 `out/` 에 있다. 참가자는 `participant_id`(1–12)로만 식별되며 원시 데이터는 개인정보 때문에 공개되지 않는다.

주의할 점 하나: 키 셔플(`ShuffleCandidateKeyMapping()`)은 **P3 세션부터 적용**됐다. P1·P2 데이터에는 `key_mapping`·`assigned_key`·`key_presses.csv` 가 없고 key1이 항상 시스템 1순위이므로, 후보 랭크를 다루는 분석에서는 두 참가자를 분리해야 한다. 자세한 내용은 서버 저장소 README 의 "알려진 데이터 제약" 참고.
