# Warudo Resource List Cache

**한국어** · [English](README.en.md) · [日本語](README.ja.md)

**Download: [ResourceListCachePlugin.cs](https://github.com/moraecat/warudo-resource-cache/raw/main/ResourceListCachePlugin.cs)**

---

모드·워크숍 구독이 많을 때 Prop / Environment / Character / Particle의 **소스 드롭다운이 수 초씩 멈추는 문제**를 해결하는 Warudo Playground 플러그인입니다.

## 원인과 해결

- 원인: 드롭다운을 열 때마다 모든 모드 파일을 열어 표시 이름을 파싱합니다 (이중 호출 버그로 2배, 결과 캐시는 1초뿐)
- 해결: 리플렉션으로 기본 프로바이더 8종을 캐싱 버전으로 교체합니다. 표시 이름을 (경로+수정시각) 키로 영속 캐시해 **새로 추가되거나 변경된 파일만** 파싱합니다. 게임 파일은 수정하지 않습니다
- 실측 (로컬 300개 + 워크숍 구독 348개 환경): Prop 로컬 189개 수 초 → **약 19ms**, 워크숍 479개 → **약 110ms**. 목록 내용은 원본과 동일합니다

## 설치

1. 위 Download 링크로 받은 `ResourceListCachePlugin.cs`를 `Warudo_Data/StreamingAssets/Playground/`에 복사
2. Warudo 재시작 (제거: 파일 삭제 후 재시작)

로그 태그는 `[ResourceListCache]`, 캐시 파일은 `AppData/LocalLow/HakuyaLabs/Warudo/moraecat_resource_name_cache.json` (삭제해도 자동 재생성).

## 주의

- 내부 API에 리플렉션으로 의존합니다. Warudo 업데이트로 깨지면 아무것도 교체하지 않고 기본 동작으로 복귀합니다 (안전 실패)
- uMod 플러그인 모드는 `System.Reflection`이 금지라 `.warudo` 패키징이 불가능합니다 — Playground 전용
- Playground 스크립트는 샌드박스 없이 실행됩니다. 단일 파일이니 설치 전에 소스를 확인하세요

---

Author: [moraecat](https://github.com/moraecat) · License: MIT
