# Warudo Resource List Cache

[한국어](README.md) · [English](README.en.md) · **日本語**

**Download: [ResourceListCachePlugin.cs](https://github.com/moraecat/warudo-resource-cache/releases/latest/download/ResourceListCachePlugin.cs)**

---

MODやワークショップの購読が多い環境で、Prop / Environment / Character / Particleの**ソースドロップダウンが数秒固まる問題**を解決するWarudo Playgroundプラグインです。

## 原因と解決

- 原因: ドロップダウンを開くたびに、表示名のためだけに全MODファイルを開いて解析しています (二重呼び出しバグで2倍、キャッシュは1秒のみ)
- 解決: リフレクションで標準プロバイダー8種をキャッシュ版に置き換えます。表示名を(パス+更新日時)キーで永続キャッシュし、**新規・変更ファイルのみ**解析します。ゲームファイルは変更しません
- 実測 (ローカルMOD 300件 + ワークショップ購読348件): ローカルProp 189件が数秒→**約19ms**、ワークショップ479件→**約110ms**。リスト内容は標準と同一です

## インストール

1. 上のDownloadリンクから`ResourceListCachePlugin.cs`を取得し、`Warudo_Data/StreamingAssets/Playground/`にコピー
2. Warudoを再起動 (削除はファイルを消して再起動)

ログタグは`[ResourceListCache]`、キャッシュファイルは`AppData/LocalLow/HakuyaLabs/Warudo/moraecat_resource_name_cache.json` (削除しても自動再生成)。

## 注意

- 内部APIにリフレクションで依存しています。アップデートで動かなくなった場合は何も置き換えず標準動作に戻ります (フェイルセーフ)
- uModプラグインMODでは`System.Reflection`が禁止のため`.warudo`化は不可 — Playground専用です
- Playgroundスクリプトはサンドボックスなしで実行されます。単一ファイルなので、導入前にソースをご確認ください

---

Author: [moraecat](https://github.com/moraecat) · License: MIT
