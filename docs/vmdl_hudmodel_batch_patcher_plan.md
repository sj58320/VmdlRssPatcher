# RSS VMDL hudmodel 일괄 패처 설계

## 1. 목적

RSS 인간 캐릭터 모델의 compiled VMDL(`.vmdl_c`)에 들어 있는 `hudmodel`
애니메이션 그래프 참조를 다음 RSS 공통 루트로 일괄 교체한다.

```text
기존: animation/graphs/viewmodel/viewmodel.vnmgraph
변경: animation/rss/graphs/viewmodel.vnmgraph
```

이 작업은 캐릭터 모델마다 ModelDoc 디컴파일 및 재컴파일을 반복하지 않고,
원본의 메시, 스켈레톤, 3인칭 애니메이션, 모프 및 물리 데이터를 보존하면서
애니메이션 그래프 참조만 교체하는 것을 목표로 한다.

ResourceManager는 그래프를 프리캐시하거나 메모리에 올릴 수 있지만 이미 컴파일된
VMDL 내부의 `hudmodel` 바인딩을 바꿀 수 없다. 따라서 이 도구는 서버 런타임
플러그인이 아니라 에셋 배포 전에 실행하는 오프라인 패처로 만든다.

## 2. 동작 원리

### 2.1 Source 2 compiled resource 구조

`.vmdl_c`는 단순한 모델 바이너리가 아니라 여러 block을 가진 compiled resource다.
대표적으로 다음 데이터가 한 파일 안에 들어 있다.

```text
MRPH              모프 데이터
MVTX / MIDX       메시의 vertex/index 데이터
MDAT              메시 메타데이터
ANIM / ASEQ       3인칭 애니메이션과 시퀀스
AGRP              애니메이션 그룹
PHYS              물리 및 충돌 데이터
DATA              모델 설정과 animation graph 참조
RERL              외부 리소스 경로와 Resource ID 목록
RED2              컴파일 의존성 및 편집 정보
```

이번 패처가 의미상 변경해야 하는 것은 `DATA`와 `RERL`의 특정 항목 하나뿐이다.
나머지 block은 원본과 동일한 의미를 유지해야 한다.

### 2.2 DATA의 animation graph 목록

인간 캐릭터 VMDL의 DATA에는 일반적으로 다음과 같은 animation graph 참조가 있다.

```text
m_refAnimGraphs
├─ identifier=""           -> 기본 3인칭 worldmodel graph
├─ identifier="uimodel"    -> UI 모델 graph
├─ identifier="hudmodel"   -> 1인칭 viewmodel graph
└─ identifier="worldmodel" -> 명시적인 3인칭 worldmodel graph
```

커스텀 칼의 1인칭 VNMGraph를 사용하려면 캐릭터 모델의 `hudmodel`이 RSS 공통
viewmodel 루트를 가리켜야 한다. 기본 graph와 `worldmodel`은 3인칭 캐릭터
애니메이션을 담당하므로 변경하면 안 된다.

### 2.3 루트 그래프만 연결하는 이유

캐릭터 VMDL에 모든 커스텀 VNMGraph와 VNMClip을 하나씩 등록하는 구조가 아니다.
캐릭터 VMDL은 RSS 공통 루트 VNMGraph 하나만 참조한다.

```text
캐릭터 VMDL
└─ hudmodel
   └─ animation/rss/graphs/viewmodel.vnmgraph
      ├─ 기본 viewmodel 처리
      ├─ 커스텀 무기 variation graph
      ├─ 커스텀 inspect graph
      └─ variation에서 사용하는 VNMSkel/VNMClip
```

새 커스텀 무기를 추가할 때는 RSS 루트 graph와 그 하위 의존성을 업데이트한다.
모든 인간 캐릭터가 같은 RSS 루트를 사용하고 있다면 캐릭터 VMDL을 다시 패치할
필요가 없다.

단, 파일을 특정 이름으로 VPK에 넣는 것만으로 variation이 자동 등록되는 것은
아니다. RSS 루트 graph가 해당 variation graph를 실제로 참조하도록 컴파일되어
있어야 하며, 무기의 `animclass`도 variation 이름과 일치해야 한다.

### 2.4 RERL을 함께 수정해야 하는 이유

DATA의 `m_hGraph`에는 사람이 읽는 경로가 보이지만 compiled resource는 RERL의
외부 참조 정보도 사용한다. RERL 항목에는 다음 두 값이 함께 들어 있다.

```text
Resource path
Resource ID
```

DATA 경로만 바꾸거나 RERL 경로만 바꾸면 서로 불일치하여 그래프가 기본값으로
돌아가거나 리소스 로딩에 실패할 수 있다. 패처는 DATA와 RERL을 한 트랜잭션으로
변경해야 한다.

compiled resource 내부에서는 확장자 `_c`가 없는 논리 경로를 사용한다.

```text
올바른 참조: animation/rss/graphs/viewmodel.vnmgraph
실제 VPK 파일: animation/rss/graphs/viewmodel.vnmgraph_c
```

엔진이 논리 경로를 통해 해당 compiled `_c` 파일을 찾는다.

### 2.5 ResourceManager만으로 해결되지 않는 이유

ResourceManager 또는 `IEntityResourceManifest::AddResource()`는 다음 작업을 한다.

- 리소스를 manifest에 등록한다.
- 서버와 클라이언트가 사용할 리소스를 프리캐시한다.
- 로드된 리소스를 resident 상태로 유지한다.

하지만 ResourceManager는 이미 컴파일된 캐릭터 VMDL의 DATA를 수정하지 않는다.
그래프가 메모리에 존재하더라도 캐릭터 VMDL의 `hudmodel`이 Valve 기본 graph를
가리키면 RSS graph는 선택되지 않는다.

따라서 필요한 작업은 두 단계다.

1. 오프라인 패처로 캐릭터 VMDL의 `hudmodel` 바인딩 변경
2. CS2Fixes resource manifest로 RSS graph와 관련 리소스 프리캐시

### 2.6 일반 재컴파일을 피해야 하는 이유

VRF/Source2Viewer로 compiled VMDL을 텍스트 모델로 추출한 뒤 다시 컴파일하면
원본 제작에 사용된 ModelDoc 정보가 완전히 복원되지 않을 수 있다.

이 경우 다음 데이터가 축소되거나 사라질 수 있다.

- 여러 메시 block
- 모프 및 표정 데이터
- 원본 스켈레톤 매핑
- 3인칭 ANIM/ASEQ/AGRP
- 물리 및 충돌 데이터
- 제작 도구 전용 메타데이터

페비 테스트에서 이 방식으로 만든 파일은 크기가 크게 줄었고 3인칭 애니메이션이
굳었다. 따라서 패처는 모델을 새로 컴파일하지 않고 VRF로 원본 compiled block을
읽은 후 graph reference만 변경하여 다시 직렬화한다.

## 3. 검증된 기준

페비 솔라5 모델을 기준으로 다음 방식이 검증되었다.

- 원본 VMDL을 ValveResourceFormat(VRF)으로 읽는다.
- DATA의 `m_refAnimGraphs`에서 `m_sIdentifier == "hudmodel"`인 항목만 찾는다.
- 해당 항목의 `m_hGraph`만 RSS 루트 그래프로 교체한다.
- RERL의 기존 그래프 경로와 Resource ID도 함께 교체한다.
- VRF `Resource.Serialize()`로 compiled resource를 다시 작성한다.

검증된 Resource ID는 다음과 같다.

```text
기존 ID: 0xC5111C601E968C98
변경 ID: 0xD6AA21250E1DE440
```

페비 원본과 VRF 패치본의 `Source2Viewer -a` 결과는 57,717줄이 동일했다.
의미가 바뀐 부분은 DATA와 RERL의 그래프 경로/ID 한 항목뿐이었다.

ModelDoc 디컴파일 후 재컴파일한 페비 모델은 약 3.03MB에서 1.74MB로 줄면서
일부 메시, 모프, 물리 및 애니메이션 데이터가 달라졌고 3인칭 애니메이션이
굳었다. 따라서 이 방식은 일괄 패처에서 사용하지 않는다.

## 4. 구현 기술

- 언어: C#
- 런타임: .NET
- 파서/직렬화: ValveResourceFormat 및 ValveKeyValue
- 보조 검증: Source2Viewer-CLI
- 결과 보고서: JSON과 CSV

개발에 사용할 VRF 경로:

```text
C:\Users\c\Desktop\First_NmClipCompiler\NmClipCompiler_RE\vendor\ValveResourceFormat
```

에셋 기본 경로:

```text
C:\Users\c\Desktop\RSS ZE ASSET(custom_weapon)
```

## 5. 입력 모델 선정

### 권장 방식

스토어에 등록된 인간 캐릭터 모델 경로를 설정 파일에서 추출하여 명시적인
대상 목록을 만든다. 좀비, 무기, 프롭 및 맵 모델은 대상에 포함하지 않는다.

### 보조 방식

캐릭터 디렉터리를 스캔하되 다음 조건을 모두 만족하는 파일만 후보로 잡는다.

1. 확장자가 `.vmdl_c`이다.
2. DATA에 `m_refAnimGraphs`가 존재한다.
3. `m_sIdentifier == "hudmodel"`인 항목이 정확히 하나 존재한다.
4. 그 항목이 기존 Valve viewmodel 그래프를 가리킨다.

이미 RSS 그래프를 사용하는 모델은 `ALREADY_PATCHED`로 기록하고 건너뛴다.
다른 서버나 제작자의 커스텀 그래프를 사용하는 모델은 자동 변경하지 않고
`CUSTOM_GRAPH_CONFLICT`로 분류한다.

## 6. 처리 알고리즘

각 모델에 대해 다음 순서로 처리한다.

1. 파일 SHA-256과 크기를 기록한다.
2. VRF로 파일을 읽고 전체 block 목록을 기록한다.
3. DATA에서 `hudmodel` 항목을 찾는다.
4. `m_hGraph`를 RSS 루트 경로로 교체한다.
5. 기존 값의 KV3 `Resource` flag를 그대로 유지한다.
6. RERL에서 기존 경로 항목을 찾는다.
7. RERL 경로와 Resource ID를 RSS 값으로 교체한다.
8. staging 디렉터리에 새 `.vmdl_c`를 직렬화한다.
9. 새 파일을 다시 VRF로 읽어 파싱 가능 여부를 확인한다.
10. 원본과 패치본의 의미 데이터 검증을 수행한다.
11. 모든 검증을 통과한 파일만 실제 에셋 경로로 교체한다.

DATA 또는 RERL에서 대상이 0개 또는 2개 이상 발견되면 자동 수정하지 않는다.

## 7. 안전장치

### Dry run

기본 실행은 파일을 수정하지 않는 `--dry-run`으로 한다. 후보 파일 수,
패치 가능 여부, 충돌 및 실패 사유만 보고한다.

### 원본 백업

실제 변경 전 원본을 다음과 같은 별도 백업 루트에 복사한다.

```text
backups/vmdl_hudmodel_YYYYMMDD_HHMMSS/
```

원본 상대 경로를 그대로 유지하여 동일한 디렉터리 구조로 복원할 수 있게 한다.
기존 원본 파일 옆에 백업 파일을 대량 생성하지 않는다.

### Staging

패치 결과는 먼저 staging에 생성한다.

```text
staging/vmdl_hudmodel_YYYYMMDD_HHMMSS/
```

전체 검증이 끝나기 전에는 실제 에셋 모델을 덮어쓰지 않는다.

### 원자적 교체

검증에 성공한 staging 파일을 임시 이름으로 복사한 뒤 같은 볼륨에서 rename하여
교체한다. 실행 도중 중단되어 원본이 반쯤 기록되는 상황을 방지한다.

### 복원

`restore` 명령으로 특정 실행 ID의 백업을 원래 위치에 복원할 수 있게 한다.
복원 전 현재 파일의 SHA-256도 별도로 기록한다.

## 8. 검증 규칙

다음 조건을 모두 만족해야 `PASS`로 처리한다.

1. 패치본을 VRF와 Source2Viewer가 오류 없이 읽는다.
2. block의 종류와 개수가 원본과 같다.
3. `MRPH`, 모든 `MVTX/MIDX/MDAT`, `ANIM`, `ASEQ`, `AGRP`, `PHYS`가 유지된다.
4. DATA의 모든 값은 `hudmodel.m_hGraph`를 제외하고 동일하다.
5. RERL은 대상 그래프의 경로와 ID를 제외하고 동일하다.
6. `uimodel`, 기본 graph 및 `worldmodel` 참조는 변경되지 않는다.
7. 패치본의 `hudmodel` 경로가 RSS 루트를 가리킨다.
8. RSS 루트 VNMGraph가 에셋에 실제로 존재한다.

VRF 재직렬화 과정에서 block 크기, offset, 압축률 및 `-0.0/0.0` 표기는
달라질 수 있다. 이것들은 의미 변경으로 판단하지 않는다.

## 9. 보고서

각 실행은 다음 정보를 JSON과 CSV로 남긴다.

- 실행 ID와 실행 시각
- 입력 에셋 루트
- 모델 상대 경로
- 처리 전/후 SHA-256
- 처리 전/후 파일 크기
- 기존 hudmodel 경로
- 변경 hudmodel 경로
- DATA 패치 개수
- RERL 패치 개수
- block 검증 결과
- 최종 상태
- 실패 또는 건너뛴 사유

상태 예시:

```text
PATCHED
ALREADY_PATCHED
NO_HUDMODEL
MULTIPLE_HUDMODEL
CUSTOM_GRAPH_CONFLICT
RERL_MISSING
VALIDATION_FAILED
PARSE_FAILED
```

## 10. 명령행 인터페이스 초안

```powershell
# 후보만 조사
VmdlHudGraphBatchPatcher.exe scan `
  --asset-root "C:\Users\c\Desktop\RSS ZE ASSET(custom_weapon)" `
  --dry-run

# 명시된 인간 캐릭터 목록을 staging에 패치
VmdlHudGraphBatchPatcher.exe patch `
  --asset-root "C:\Users\c\Desktop\RSS ZE ASSET(custom_weapon)" `
  --manifest ".\human_character_models.txt" `
  --target-graph "animation/rss/graphs/viewmodel.vnmgraph" `
  --staging ".\staging" `
  --backup ".\backups"

# staging 결과 재검증
VmdlHudGraphBatchPatcher.exe verify `
  --run-id "20260724_210000"

# 검증된 파일을 에셋에 반영
VmdlHudGraphBatchPatcher.exe apply `
  --run-id "20260724_210000"

# 해당 실행 이전 상태로 복원
VmdlHudGraphBatchPatcher.exe restore `
  --run-id "20260724_210000"
```

## 11. 단계별 개발 계획

### 1단계: 단일 파일 패처

- 페비 솔라5 원본을 입력으로 사용한다.
- DATA와 RERL 한 항목만 변경한다.
- 원본/패치본 의미 비교를 자동화한다.
- 기존 수동 검증 결과와 동일한지 확인한다.

### 2단계: 스캔 및 dry run

- 캐릭터 폴더 스캔을 구현한다.
- 모델별 graph 상태를 분류한다.
- 실제 파일은 수정하지 않고 보고서만 생성한다.

### 3단계: 소규모 검증

구조가 서로 다른 인간 캐릭터 모델 3~5개만 선택해 테스트한다.

- 기본 대기/이동/점프/사망 애니메이션
- 1인칭 기본 총기
- 커스텀 칼 장착
- 다른 무기로 교체 후 칼 재장착
- 리스폰
- 재접속
- 다른 플레이어가 보는 3인칭 애니메이션

### 4단계: 전체 staging 생성

- 인간 캐릭터 전체를 staging에 패치한다.
- 자동 검증 실패 모델은 원본 유지 및 수동 검토 대상으로 분리한다.
- 실제 에셋에는 아직 반영하지 않는다.

### 5단계: 에셋 반영

- 전체 검증 결과를 확인한다.
- 통과 파일만 실제 에셋에 반영한다.
- VPK를 다시 생성하고 workshop 테스트 애드온에 먼저 올린다.
- 테스트 서버에서 확인한 뒤 운영 애드온을 업데이트한다.

## 12. 에셋 및 플러그인 조건

패처로 캐릭터 VMDL을 변경하는 것만으로 모든 작업이 끝나는 것은 아니다.

- `animation/rss/graphs/viewmodel.vnmgraph`가 VPK에 포함되어야 한다.
- 루트 그래프가 각 커스텀 무기의 variation graph를 참조해야 한다.
- 각 무기의 `animclass`와 variation 이름이 일치해야 한다.
- 필요한 VNMGraph, VNMSkel, VNMClip과 soundevent가 모두 포함되어야 한다.
- CS2Fixes의 resource manifest에도 RSS 루트 그래프와 필요한 soundevent를
  등록해야 한다.
- 클라이언트가 갱신된 workshop 애드온을 내려받아야 한다.

캐릭터 VMDL에 RSS 공통 루트를 한 번 연결한 이후에는 같은 루트 경로를 유지하는
한 새 커스텀 무기를 추가할 때 캐릭터 모델을 다시 패치할 필요가 없다.

## 13. 구현 핵심 의사코드

아래는 실제 구현자가 따라갈 수 있는 핵심 흐름이다.

```csharp
using var resource = new Resource();
resource.Read(inputStream);

var model = resource.DataBlock as Model;
var dataPatchCount = 0;

// KVObject를 재귀 순회한다.
// m_sIdentifier가 hudmodel인 object를 정확히 하나 찾는다.
if (identifier == "hudmodel" && graphPath == oldGraphPath)
{
    var replacement = new KVObject(newGraphPath)
    {
        // 기존 resource flag를 반드시 보존한다.
        Flag = oldGraphValue.Flag,
    };

    objectValue["m_hGraph"] = replacement;
    dataPatchCount++;
}

var rerlPatchCount = 0;
foreach (var reference in resource.ExternalReferences.ResourceRefInfoList)
{
    if (reference.Name != oldGraphPath)
        continue;

    reference.Name = newGraphPath;
    reference.Id = newGraphResourceId;
    rerlPatchCount++;
}

if (dataPatchCount != 1 || rerlPatchCount != 1)
{
    // 모호한 모델은 절대 자동 저장하지 않는다.
    return ValidationFailed;
}

resource.Serialize(stagingStream);
```

구현 시 주의사항:

- 고정 RSS 경로만 지원할 때는 검증된 Resource ID 상수를 사용할 수 있다.
- 목표 경로를 자유롭게 입력받게 만들 경우 Source 2 Resource ID 계산을 구현하고
  알려진 경로/ID 쌍으로 단위 테스트해야 한다.
- `KVFlag.Resource`를 잃으면 문자열은 보여도 resource handle로 동작하지 않을 수 있다.
- DATA 또는 RERL 패치 개수가 정확히 1이 아니면 저장하지 않는다.
- 원본 파일에 직접 쓰지 말고 staging에 완성한 후 검증한다.
- VRF 직렬화 결과는 압축률과 block offset이 달라질 수 있으므로 단순 바이너리
  동일성 대신 해석된 의미 데이터를 비교한다.

## 14. 개발자 인수인계용 핵심 요약

1. 이 도구는 ResourceManager 플러그인이 아니라 에셋 제작용 오프라인 도구다.
2. 모든 VNMGraph를 캐릭터 VMDL에 등록하지 않는다.
3. 캐릭터 VMDL의 `hudmodel`을 RSS 공통 root graph 하나로 바꾼다.
4. DATA와 RERL의 경로/Resource ID를 반드시 동시에 바꾼다.
5. 기본 graph, `uimodel`, `worldmodel`은 절대 변경하지 않는다.
6. ModelDoc 재컴파일 대신 VRF compiled resource 재직렬화를 사용한다.
7. 메시, 스켈레톤, ANIM/ASEQ/AGRP, PHYS가 보존되지 않으면 실패 처리한다.
8. dry run, staging, 백업, 보고서, restore를 필수 기능으로 만든다.
9. 최초에는 대표 모델 3~5개로 검증한 뒤 전체 인간 모델에 적용한다.
10. 한 번 RSS 공통 루트를 연결하면 이후 무기 추가는 root graph 업데이트로 처리한다.

## 15. 완료 기준

- 등록된 인간 캐릭터 모델 전체가 분류된다.
- 자동 패치 대상은 원본 데이터 보존 검증을 모두 통과한다.
- 커스텀 칼의 1인칭 모션이 정상 동작한다.
- 캐릭터의 3인칭 애니메이션이 굳거나 T 포즈가 되지 않는다.
- 기존 총기, 사운드, 리스폰 및 재접속 동작에 회귀가 없다.
- 실패 모델은 자동으로 원본이 유지되고 보고서에 원인이 남는다.
- 단일 명령으로 전체 복원이 가능하다.
