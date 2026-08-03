#!/usr/bin/env bash
# compile-check.sh — csc 로 ResourceListCachePlugin.cs 컴파일 검증
# 성공 기준: 에러 0. 경고는 무시.
# 재실행 가능.

set -e

MANAGED="E:/Steam/steamapps/common/Warudo/Warudo_Data/Managed"
CSC="C:/Program Files/dotnet/sdk/8.0.421/Roslyn/bincore/csc.dll"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$REPO/build/ResourceListCachePlugin.dll"

mkdir -p "$REPO/build"

echo "[compile-check] Compiling src/ResourceListCachePlugin.cs ..."

dotnet "$CSC" \
  -noconfig -nostdlib \
  -target:library \
  -langversion:9 \
  -out:"$OUT" \
  -r:"$MANAGED/mscorlib.dll" \
  -r:"$MANAGED/netstandard.dll" \
  -r:"$MANAGED/System.dll" \
  -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/System.Runtime.dll" \
  -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/Warudo.Core.dll" \
  -r:"$MANAGED/Warudo.Plugins.Core.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" \
  -r:"$MANAGED/UniTask.dll" \
  -r:"$MANAGED/Newtonsoft.Json.dll" \
  "$REPO/src/ResourceListCachePlugin.cs"

echo "[compile-check] Done. Output: $OUT"
