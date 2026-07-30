# VmdlHudGraphBatchPatcher

`vmdl_hudmodel_batch_patcher_plan.md` 설계를 구현한 HUD-only 안전 배치 패처입니다.

기존 `VmdlRssPatcher v1.0.0`은 그대로 유지됩니다. 이 도구는 별도 실행 파일이며
기본 graph, `uimodel`, `worldmodel`을 절대 변경하지 않습니다.

## 고정 변경 대상

```text
DATA
  m_sIdentifier = "hudmodel"

  animation/graphs/viewmodel/viewmodel.vnmgraph
  -> animation/rss/graphs/viewmodel.vnmgraph

RERL
  Resource ID C5111C601E968C98
  ->          D6AA21250E1DE440
```

안전상 `--target-graph`에는 위 RSS 고정 경로만 사용할 수 있습니다.

다른 서버나 제작자의 커스텀 HUD 그래프는 `CUSTOM_GRAPH_CONFLICT`로 분류하고
자동 변경하지 않습니다.

## 요구 파일

- `VmdlHudGraphBatchPatcher.exe`
- Source2Viewer CLI 19.2 배포 폴더 (`Source2Viewer-CLI.exe` 및 동봉 DLL)

엄격 검증 과정에서 ValveResourceFormat과 Source2Viewer를 모두 사용합니다.
[ValveResourceFormat Releases](https://github.com/ValveResourceFormat/ValveResourceFormat/releases)에서 Source2Viewer CLI 19.2를 받아 배포 폴더를 그대로 유지하세요. `Source2Viewer-CLI.exe`를 패처 EXE 옆에 두거나 매번
`--source2viewer <경로>`로 지정해야 합니다.

패처 EXE는 Windows x64 자체 포함 파일이므로 별도의 .NET 설치가 필요하지
않습니다.

## 전체 작업 흐름

### 1. Scan — 기본 실행, 파일 변경 없음

```powershell
VmdlHudGraphBatchPatcher.exe scan `
  --asset-root "D:\RSS ZE ASSET" `
  --manifest ".\human_character_models.txt"
```

명령을 생략해도 `scan`으로 실행됩니다.

```powershell
VmdlHudGraphBatchPatcher.exe --asset-root "D:\RSS ZE ASSET"
```

manifest를 생략하면 에셋 루트의 모든 `*.vmdl_c`를 재귀 검색하되, staging,
backup 및 runs 루트는 제외합니다.

manifest는 에셋 루트 기준 상대 경로를 한 줄에 하나씩 기록합니다.

```text
# 인간 캐릭터 모델
characters/models/asmodeus/firefly/firefly.vmdl_c
characters/models/rss/hoshino/hoshino.vmdl_c
```

### 2. Patch — backup과 staging 생성

```powershell
VmdlHudGraphBatchPatcher.exe patch `
  --asset-root "D:\RSS ZE ASSET" `
  --manifest ".\human_character_models.txt" `
  --source2viewer "D:\Tools\Source2Viewer-CLI.exe" `
  --staging ".\staging" `
  --backup ".\backups" `
  --run-root ".\runs"
```

Patch 단계는 실제 에셋을 덮어쓰지 않습니다.

```text
backups/vmdl_hudmodel_<RUN_ID>/<원본 상대 경로>
staging/vmdl_hudmodel_<RUN_ID>/<원본 상대 경로>
runs/<RUN_ID>/run.json
runs/<RUN_ID>/report.json
runs/<RUN_ID>/report.csv
```

적용 전에 전체 후보를 출력하고 `y/n` 확인을 받습니다. 자동화 환경에서는
명시적으로 `--yes`를 사용해야 합니다.

### 3. Verify — staging 전체 재검증

```powershell
VmdlHudGraphBatchPatcher.exe verify `
  --run-id 20260731_120000_000 `
  --run-root ".\runs" `
  --source2viewer "D:\Tools\Source2Viewer-CLI.exe"
```

### 4. Apply — 검증 통과 파일만 실제 에셋에 반영

```powershell
VmdlHudGraphBatchPatcher.exe apply `
  --run-id 20260731_120000_000 `
  --run-root ".\runs"
```

Patch 이후 실제 에셋 파일의 SHA-256이 달라졌으면 `SOURCE_CHANGED`로 분류하고
덮어쓰지 않습니다. 검증된 staging 파일을 목적지와 같은 볼륨의 임시 파일로
복사한 뒤 rename하여 원자적으로 교체합니다.

### 5. Restore — 실행 이전 상태로 일괄 복원

```powershell
VmdlHudGraphBatchPatcher.exe restore `
  --run-id 20260731_120000_000 `
  --run-root ".\runs"
```

복원 전 현재 파일은 다음 위치에 별도로 보관됩니다.

```text
runs/<RUN_ID>/restore_snapshots/<RESTORE_ID>/<상대 경로>
```

## 자동 검증 규칙

각 staging 파일은 다음 조건을 모두 만족해야 `PATCHED`, 이후 재검증에서
`VERIFIED`가 됩니다.

1. VRF가 원본과 패치본을 모두 읽는다.
2. Source2Viewer `-a`가 패치본 전체 block을 오류 없이 읽는다.
3. block 종류, 개수 및 순서가 같다.
4. `DATA`와 `RERL`을 제외한 모든 원본 block payload가 바이트 단위로 같다.
5. `MRPH`, `MVTX`, `MIDX`, `MDAT`, `ANIM`, `ASEQ`, `AGRP`, `PHYS`가 유지된다.
6. DATA는 `hudmodel.m_hGraph` 외 모든 의미 데이터가 같다.
7. 기존 `m_hGraph`의 KV3 flag가 그대로 유지된다.
8. RERL은 대상 경로와 Resource ID 한 항목 외 모두 같다.
9. 기본 graph, `uimodel`, `worldmodel`이 유지된다.
10. RSS 루트 `animation/rss/graphs/viewmodel.vnmgraph_c`가 실제 에셋에 존재하고
    Source2Viewer로 읽힌다.

## 결과 상태

주요 상태:

```text
PATCHABLE
PATCHED
VERIFIED
APPLIED
RESTORED
ALREADY_PATCHED
NO_HUDMODEL
MULTIPLE_HUDMODEL
CUSTOM_GRAPH_CONFLICT
RERL_MISSING
RERL_ID_MISMATCH
VALIDATION_FAILED
PARSE_FAILED
SOURCE_CHANGED
```

## JSON 및 CSV 보고서

보고서에는 다음 정보가 기록됩니다.

- 실행 ID와 실행 시각
- 에셋 루트와 모델 상대 경로
- 처리 전후 SHA-256과 파일 크기
- 기존 및 변경 HUD 그래프
- 기존 KV3 flag
- DATA/RERL 패치 개수
- 전체 block 종류, 크기 및 SHA-256
- VRF/Source2Viewer/의미 데이터 검증 결과
- 적용 및 복원 결과와 실패 사유

각 명령 실행 시점의 보고서는 `runs/<RUN_ID>/history`에도 보관됩니다.

## 소스 빌드

필요 환경:

- Windows x64
- .NET 10 SDK

```powershell
dotnet build -c Release `
  .\src\VmdlHudGraphBatchPatcher\VmdlHudGraphBatchPatcher.csproj
```

단일 자체 포함 EXE:

```powershell
dotnet publish -c Release `
  .\src\VmdlHudGraphBatchPatcher\VmdlHudGraphBatchPatcher.csproj `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishAot=false
```

## 에셋 적용 후 별도 확인 사항

이 프로그램은 compiled VMDL의 HUD 바인딩만 변경합니다. 다음 항목은 에셋과
서버에서 별도로 확인해야 합니다.

- RSS 루트 graph가 각 커스텀 무기 variation을 참조하는지
- 무기 `animclass`가 variation 이름과 일치하는지
- 관련 VNMGraph, VNMSkel, VNMClip 및 soundevent가 VPK에 포함됐는지
- CS2Fixes resource manifest에 필요한 리소스가 등록됐는지
- 테스트 Workshop 및 테스트 서버에서 1인칭·3인칭·리스폰·재접속 회귀가 없는지
