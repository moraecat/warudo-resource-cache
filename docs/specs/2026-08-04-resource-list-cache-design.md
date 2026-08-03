# Warudo 리소스 목록 캐싱 프로바이더 — 설계 문서

날짜: 2026-08-04
상태: 설계 승인됨 (모래캣)
배포: Playground 스크립트로 개발/검증 → 효과 확인 후 정식 플러그인(.warudo)화

## 1. 문제

Prop/Environment/Character/Particle 에셋의 Source 드롭다운(및 블루프린트 AutoComplete 포트)이
열릴 때마다 수 초씩 걸림. 원인 (디컴파일 분석으로 확정):

1. `ResourceManager.ProvideResources` 캐시 TTL이 1초 → 사실상 매번 풀스캔 (메인스레드 동기)
2. 프로바이더당 `ProvideResources(query)`를 2번 호출하는 버그 → 비용 2배
3. `ResourceHelpers.ResolveName()`이 .warudo마다 uMod 헤더 파싱(파일 열기), .vrm마다 VRM 메타 파싱
4. 실측 규모: 로컬 Props 189개 + Environments 32 + Characters 31 + Particles 62,
   Workshop 구독 348개 아이템 / .warudo 817개 (88GB) → 드롭다운 1회당 파일 열기 수백~천 회 ×2

## 2. 접근법 (승인: C안 — 프로바이더 교체)

리플렉션으로 `ResourceManager`의 private `providers` (Dictionary<string, List<IResourceProvider>>)에
접근해, 대상 원본 프로바이더 8개 인스턴스를 자체 캐싱 프로바이더로 같은 자리에 교체.

- 교체 대상 (쿼리 4종 × 로컬/워크숍):
  - CorePlugin: `PersistentDataDirectory{Prop,Environment,Character,Particle}ResourceProvider`
  - CommunityPlugin: `Workshop{Prop,Environment,Character,Particle}ResourceProvider`
- 유지 (교체 안 함): BuiltIn* 프로바이더(인메모리 카탈로그, 이미 빠름), Image/Sound/Video 등
  파일명만 쓰는 싼 쿼리 전부

핵심: "재구현"이지만 원본의 public 부품을 그대로 호출해 복제 범위를 최소화한다.

| 재사용 부품 | 접근 경로 | 역할 |
|---|---|---|
| `Context.PersistentDataManager.GetFileEntries(folder, pattern, predicate)` | public | 로컬 폴더 재귀 열거 (mtime 포함, 파일 열기 없음) |
| `Context.PluginManager.GetPlugin<CommunityPlugin>().SteamWorkshop` | public property | 워크숍 인스턴스 획득 |
| `SteamWorkshop.GetFileEntries(folder, pattern, predicate)` / `GetFullPath(itemId, relPath)` | public | 구독 아이템 열거 |
| `ResourceHelpers.ResolveName(path)` | public static | 비싼 이름 해석 — **캐시 미스일 때만 호출** |
| `ResourceListExtensions.UseFilenameForDuplicates(list)` | public 확장 | 중복 파일명 처리 (로컬만, 원본과 동일) |

## 3. 원본 동작 사양 (동일하게 유지해야 함)

디컴파일 소스 기준 (경로: `decompiled\` 하위):

로컬 프로바이더 (`CorePlugin.cs` 510-596행) — `ResourceProviderName = "LOCAL_FILES"`:

| 쿼리 | 폴더 | 열거 방식 | URI |
|---|---|---|---|
| Prop | Props | `"*.*"` + predicate `.warudo` | `prop://data/` + it.path |
| Environment | Environments | `"*.warudo"` 패턴 (predicate 없음) | `environment://data/` + it.path |
| Character | Characters | `"*.*"` + predicate `.vrm∥.warudo` | `character://data/` + it.path |
| Particle | Particles | `"*.*"` + predicate `.warudo` | `particle://data/` + it.path |

- 전부 `.ToList().UseFilenameForDuplicates()` 적용
- predicate 비교는 `ToLowerOptimized()` 후 `EndsWith`
- `it.path` = StreamingAssets 기준 상대경로, `/` 구분자

워크숍 프로바이더 (`CommunityPlugin.cs` 26-166행) — `ResourceProviderName = "STEAM_WORKSHOP"`:

| 쿼리 | FolderName | 지원 확장자 | URI |
|---|---|---|---|
| Prop | Props | .warudo | `prop://workshop/{itemId}/{relativePath}` |
| Environment | Environments | .warudo | `environment://workshop/{itemId}/{relativePath}` |
| Character | Characters | .vrm, .warudo | `character://workshop/{itemId}/{relativePath}` |
| Particle | Particles | .warudo | `particle://workshop/{itemId}/{relativePath}` |

- label = `ResolveName(Workshop.GetFullPath(it.itemId, it.relativePath))`
- 워크숍은 UseFilenameForDuplicates **적용 안 함** (원본도 안 함)
- `Resource` = { category(null 가능), label, uri } (`Warudo.Core.Resource.Resource`)

## 4. 컴포넌트 설계 (Playground 단일 파일)

파일: `Warudo_Data\StreamingAssets\Playground\ResourceListCachePlugin.cs`
네임스페이스: `Moraecat.ResourceListCache` (기존 Playground 플러그인 컨벤션)

### 4.1 ResourceListCachePlugin ([PluginType])

- Id "Moraecat.ResourceListCache", AssetTypes/NodeTypes 없음
- `OnCreate()`에서 async 초기화: `CommunityPlugin.SteamWorkshop != null` 및 providers에
  원본 8종 등록 확인까지 UniTask로 폴링 대기 (프레임 단위, 타임아웃 ~30초) → 교체 실행
- 멱등: 이미 우리 타입이면 스킵 (Playground 핫로드 대응)

### 4.2 NameCache (파일별 이름 영속 캐시)

- 키: 절대경로 소문자 정규화, 값: { mtime(unix ms), label }
- 조회: mtime 일치 → 라벨 반환 / 불일치·미스 → `ResourceHelpers.ResolveName()` 호출 후 갱신
- 영속화: `Application.persistentDataPath\moraecat_resource_name_cache.json`
  - 스키마: `{ "version": 1, "entries": { "<path>": { "m": <mtime>, "l": "<label>" } } }`
  - provide 완료 후 변경분 있을 때만 저장, 저장 시 이번 열거에 없던 엔트리 정리(prune)
  - 손상 시 버리고 빈 캐시 재구축
- mtime은 열거 결과(FileEntry.lastModifiedTime)를 그대로 사용 — 추가 IO 없음

### 4.3 CachedLocalProvider / CachedWorkshopProvider (IResourceProvider ×8)

- §3 사양표대로 열거→NameCache 조립→(로컬만) UseFilenameForDuplicates
- `ResourceProviderName` 원본과 동일 ("LOCAL_FILES"/"STEAM_WORKSHOP") → UI 표시 동일
- 쿼리 불일치 시 null 반환 (원본과 동일)
- **3초 메모 캐시**: 쿼리별 결과 리스트를 3초간 보관 — ProvideResources 이중호출 버그 흡수
- 예외 시: 로그 + 보관해둔 원본 프로바이더 인스턴스로 위임 (기능 항상 유지)

### 4.4 ProviderSwapper

- `Context.ResourceManager` → private 필드 `providers` 리플렉션 획득
- 전체 리스트 순회, `GetType().Name`이 대상 8종과 일치하는 인스턴스를 같은 인덱스에 교체
- 원본 인스턴스는 폴백/복원용으로 static 보관
- 교체 결과(성공 개수) 로그

## 5. 검증

1. **동등성 자가검증** (const 플래그, Playground 단계만 ON): 쿼리별 최초 1회에 한해 원본도
   실행해 (개수, 라벨 멀티셋) 비교, 불일치 시 Warning 로그로 diff 출력
2. **성능 로그**: Stopwatch로 쿼리별 소요 ms 로그 (콜드/웜 구분)
3. 실기 확인 (사용자): Prop/Environment/Character/Particle 드롭다운 목록 동일성·체감 속도,
   새 모드 설치 시 해당 파일만 재파싱되는지
4. 컴파일 사전 검증: csc로 Warudo_Data\Managed DLL 참조해 문법/타입 검증 (Playground 반입 전)

## 6. 제약·주의

- 코드에 이모지 금지 (cp949), 로그는 ASCII/한글
- Warudo 실행/재시작은 사용자가 직접 (빌드·실행 트리거 금지 원칙)
- 디컴파일 기준이라 게임 업데이트 시 필드명/타입명 변화 가능 → 리플렉션 실패 시 아무것도
  바꾸지 않고 경고 로그만 (안전 실패)
- ResolveName 실패 파일은 파일명 폴백이 캐시됨 — mtime 변경 시 자동 재파싱이라 수용

## 7. 구현 계획 (subagent 작업 단위)

- Task 1: `ResourceListCachePlugin.cs` 구현 (§4 전체, 단일 파일)
- Task 2: 스펙 대조 코드 리뷰 (URI 포맷·predicate·중복처리·멱등성·폴백 경로 중점)
- Task 3: csc 컴파일 검증 (Warudo Managed DLL 참조), 수정 반영
- Task 4: Playground 배포 + 사용자 실기 검증 안내
