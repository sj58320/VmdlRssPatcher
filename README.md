# VmdlRssPatcher

CS2 캐릭터 모델의 컴파일된 `*.vmdl_c` 파일에서 HUD 및 월드 애니메이션 그래프 참조를 RSS 경로로 변경하는 Windows 명령줄 도구입니다.

## 변경되는 경로

| 대상 | 기존 경로 예시 | 변경 경로 |
|---|---|---|
| `hudmodel` | `animation/graphs/viewmodel/viewmodel.vnmgraph` | `animation/rss/graphs/viewmodel.vnmgraph` |
| `worldmodel` | `animation/graphs/worldmodel/worldmodel.vnmgraph` | `animation/rss/graphs/worldmodel.vnmgraph` |
| 빈 기본 엔트리 | 기존 경로가 `worldmodel.vnmgraph`인 경우 | `animation/rss/graphs/worldmodel.vnmgraph` |

`uimodel`은 변경하지 않습니다.

빈 식별자(`m_sIdentifier = ""`)는 `DefaultAnimGraph2`를 의미합니다. 빈 엔트리라고 해서 무조건 worldmodel인 것은 아니므로, 이 프로그램은 기존 그래프 경로가 실제 `worldmodel.vnmgraph`로 끝나는 경우에만 RSS worldmodel로 변경합니다.


## CS2 기본 그래프로 복원 (`--stock`)

RSS 커스텀 무기를 사용하지 않는 에셋용으로 HUD 및 월드 그래프 참조를 기본 경로로 복원할 수 있습니다.

```powershell
VmdlRssPatcher.exe --stock --dry-run "D:\KZ_assets"
VmdlRssPatcher.exe --stock "D:\KZ_assets"
```

- HUD: `animation/graphs/viewmodel/viewmodel.vnmgraph` (ID `C5111C601E968C98`)
- 월드: `animation/graphs/worldmodel/worldmodel.vnmgraph` (ID `87CE5FF43C25BA7D`)
- `--stock`을 생략하면 기존과 동일하게 RSS 경로를 적용합니다.
- 기본 그래프 파일은 설치된 CS2 VPK에서, ID는 RSS 패치 전 모델 백업에서 확인했습니다.
- DATA와 RERL을 함께 수정하며 `uimodel`과 나머지 모델 블록을 보존합니다.
- RSS 또는 기본 경로 이외의 대상 그래프를 가진 모델은 자동 복원하지 않습니다.
- 기존 엔트리가 없는 구형 모델에 새 그래프를 추가하거나 AnimGraph1을 변환하지 않습니다.
- 복원 직전 파일은 `.stockpatch.bak`으로 보관하며 기존 `.rsspatch.bak`은 유지합니다.
- 이 모드는 원본 파일 전체의 복구가 아닌 두 그래프 참조의 전환입니다.
- 커스텀 무기 파일 삭제, VPK 패키징, 워크숍 업로드는 수행하지 않습니다.
- 파일 구조 검증과 실제 게임에서의 캐릭터/팔/댄스 동작 검증은 별개입니다.

## 다운로드

저장소의 **Releases**에서 `VmdlRssPatcher.exe` 또는 ZIP 파일을 다운로드합니다.

배포된 EXE는 Windows x64용 자체 포함 프로그램입니다. 일반적인 Windows 10/11 64비트 환경에서는 별도의 .NET 설치가 필요하지 않으며 네트워크 연결도 사용하지 않습니다.

## 사용법

1. `VmdlRssPatcher.exe`를 패치할 에셋 폴더에 넣습니다.
2. EXE를 실행합니다.
3. 프로그램이 EXE가 있는 폴더와 모든 하위 폴더에서 `*.vmdl_c`를 검색합니다.
4. 변경 대상과 기존·새 경로를 확인합니다.
5. `변경하시겠습니까? (y/n)` 질문에 `y`를 입력하면 적용됩니다. 다른 값을 입력하면 아무 파일도 수정하지 않습니다.

다른 폴더를 직접 지정할 수도 있습니다.

```powershell
VmdlRssPatcher.exe "D:\패치할\에셋"
```

파일을 수정하지 않고 결과만 확인하려면 다음과 같이 실행합니다.

```powershell
VmdlRssPatcher.exe --dry-run "D:\확인할\에셋"
```

## 안전 동작

- 적용 전에 전체 변경 대상을 출력하고 한 번의 `y/n` 확인을 받습니다.
- 이미 RSS 경로와 Resource ID가 정확히 적용된 파일은 수정하지 않고 `이미 적용됨`으로 출력합니다.
- 관련 엔트리가 없는 모델에 새 엔트리를 임의로 추가하지 않습니다.
- 모호하게 중복된 엔트리는 자동 수정하지 않습니다.
- 최초 변경 시 원본 옆에 `<파일명>.rsspatch.bak` 백업을 생성합니다.
- 기존 백업이 있으면 덮어쓰지 않습니다.
- 압축된 Binary KV3 `DATA`와 `RERL` 외부 참조를 함께 수정합니다.
- 수정 결과를 메모리에서 다시 파싱해 경로와 Resource ID를 검증한 다음 원본을 교체합니다.
- 이미 적용된 파일을 다시 실행해도 파일 내용은 변하지 않습니다.

## 소스에서 빌드

필요 환경:

- Windows x64
- .NET 10 SDK

빌드:

```powershell
dotnet build -c Release .\src\VmdlRssPatcher\VmdlRssPatcher.csproj
```

Windows x64 단일 EXE 게시:

```powershell
dotnet publish -c Release .\src\VmdlRssPatcher\VmdlRssPatcher.csproj `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishAot=false
```

## 구현 정보

- [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat)을 사용해 Source 2 리소스와 Binary KV3를 파싱하고 다시 직렬화합니다.
- RSS Resource ID:
  - viewmodel: `D6AA21250E1DE440`
  - worldmodel: `00E023B440DAE5CD`
- 원본에서 `RERL`과 `DATA`만 교체하고 나머지 모델 블록은 그대로 보존합니다.

## 주의사항

- Windows x64 전용입니다.
- 파일 또는 폴더 쓰기 권한이 필요합니다.
- 서명되지 않은 개인 프로그램이므로 Windows SmartScreen 경고가 표시될 수 있습니다.
- 실제 서버 에셋에 적용하기 전에 생성된 `.rsspatch.bak` 파일을 보관하는 것을 권장합니다.