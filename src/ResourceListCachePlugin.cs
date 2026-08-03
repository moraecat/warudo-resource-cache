using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Persistence;
using Warudo.Core.Plugins;
using Warudo.Core.Resource;
using Warudo.Core.Utils;
using Warudo.Plugins.Core;
using Warudo.Plugins.Core.Utils;
using Warudo.Plugins.Community;
using SW = System.Diagnostics.Stopwatch;

namespace Moraecat.ResourceListCache
{
    // ─── 캐시 스키마 DTO ────────────────────────────────────────────────────────

    /// <summary>영속 캐시 파일 루트.</summary>
    internal class NameCacheFile
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("entries")]
        public Dictionary<string, NameCacheEntry> Entries { get; set; } = new Dictionary<string, NameCacheEntry>();
    }

    /// <summary>파일별 캐시 엔트리. m=mtime(unix ms), l=label.</summary>
    internal class NameCacheEntry
    {
        [JsonProperty("m")]
        public long Mtime { get; set; }

        [JsonProperty("l")]
        public string Label { get; set; }
    }

    // ─── NameCache ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 파일 경로 → (mtime, label) 영속 캐시.
    /// 미스 또는 mtime 불일치 시에만 ResourceHelpers.ResolveName() 을 호출한다.
    /// </summary>
    internal class NameCache
    {
        private readonly string _filePath;
        private NameCacheFile _data;
        private bool _dirty;

        public NameCache()
        {
            _filePath = Path.Combine(Application.persistentDataPath, "moraecat_resource_name_cache.json");
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    NameCacheFile loaded = JsonConvert.DeserializeObject<NameCacheFile>(json);
                    if (loaded != null && loaded.Version == 1)
                    {
                        _data = loaded;
                        if (_data.Entries == null) _data.Entries = new Dictionary<string, NameCacheEntry>();
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] NameCache load failed, starting fresh: " + e.Message);
            }
            _data = new NameCacheFile();
        }

        /// <summary>
        /// 주어진 절대경로와 mtime으로 label 을 조회/갱신한다.
        /// ResolveName 은 캐시 미스 또는 mtime 불일치 때만 호출된다.
        /// </summary>
        public string GetOrResolve(string absolutePath, long mtime)
        {
            string key = absolutePath.ToLower();
            if (_data.Entries.TryGetValue(key, out NameCacheEntry entry) && entry.Mtime == mtime)
            {
                return entry.Label;
            }

            // 캐시 미스 또는 mtime 불일치 — 비싼 호출
            string label;
            try
            {
                label = ResourceHelpers.ResolveName(absolutePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] ResolveName failed for " + absolutePath + ": " + e.Message);
                label = Path.GetFileName(absolutePath);
            }

            _data.Entries[key] = new NameCacheEntry { Mtime = mtime, Label = label };
            _dirty = true;
            return label;
        }

        /// <summary>
        /// 이번 열거에서 사용된 키 집합만 남기고 해당 쿼리 범주의 나머지를 정리한다.
        /// 다른 쿼리의 엔트리는 절대 제거하지 않는다.
        /// </summary>
        public void PruneAndSave(IEnumerable<string> activeAbsolutePaths)
        {
            // active 집합을 lower-case 화
            HashSet<string> activeKeys = new HashSet<string>(
                activeAbsolutePaths.Select(p => p.ToLower()),
                StringComparer.Ordinal);

            // 이번 열거에서 조회된 키들만 남기되,
            // 이번 열거에서 건드리지 않은 키는 유지 (다른 쿼리 보호)
            List<string> toRemove = new List<string>();
            foreach (string key in _data.Entries.Keys)
            {
                // activeKeys 에 속하지 않으면서 이번 열거 집합의 prefix 도 아니면 건드리지 않음
                // => 실제로는 "이번 쿼리에서 나온 경로" 집합만 정리 기준으로 쓴다.
                // 이 메서드는 쿼리별로 호출되므로, 이번 집합에 없는 키도 삭제하면 다른 쿼리 엔트리가 날아간다.
                // 따라서: active 집합에 없는 것은 살려두고 active 집합 전부 확인 목적으로만 호출.
                // => prune 은 active 집합이 비어있지 않을 때만 수행한다 (전체 실패 대비).
                _ = toRemove; // suppress unused warning
            }
            // 실제 prune 전략: active 경로를 포함하는 쿼리 집합 범위만 pruning.
            // 가장 안전한 구현: active 키들의 공통 폴더 prefix 를 찾아 그 prefix 에 속하는 엔트리 중
            // active 에 없는 것만 제거한다.
            if (activeKeys.Count > 0)
            {
                // 이번 열거에 참여한 경로들의 prefix 집합 (폴더 구분)
                // 단순히: active 집합에 속하지 않으면서 active 중 어느 것과 같은 "공통 경로 접두사"를 갖는 엔트리를 제거
                // 간단한 구현: active 키 중 아무거나의 디렉토리 부분을 prefix 로 삼는다
                HashSet<string> prefixes = new HashSet<string>(StringComparer.Ordinal);
                foreach (string ak in activeKeys)
                {
                    string dir = Path.GetDirectoryName(ak);
                    if (dir != null) prefixes.Add(dir.Replace('\\', '/'));
                }

                List<string> toRemoveActual = new List<string>();
                foreach (string key in _data.Entries.Keys)
                {
                    if (activeKeys.Contains(key)) continue;
                    string keyDir = Path.GetDirectoryName(key);
                    if (keyDir == null) continue;
                    keyDir = keyDir.Replace('\\', '/');
                    // 이 key 가 이번 쿼리 범위 내의 폴더에 속하는가?
                    foreach (string prefix in prefixes)
                    {
                        if (keyDir.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            toRemoveActual.Add(key);
                            break;
                        }
                    }
                }

                if (toRemoveActual.Count > 0)
                {
                    foreach (string k in toRemoveActual) _data.Entries.Remove(k);
                    _dirty = true;
                }
            }

            if (_dirty)
            {
                try
                {
                    string json = JsonConvert.SerializeObject(_data, Formatting.None);
                    File.WriteAllText(_filePath, json);
                    _dirty = false;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ResourceListCache] NameCache save failed: " + e.Message);
                }
            }
        }
    }

    // ─── CachedLocalProvider ────────────────────────────────────────────────────

    /// <summary>
    /// 로컬 PersistentData 디렉토리용 캐싱 프로바이더.
    /// 원본 PersistentDataDirectory{Prop,Environment,Character,Particle}ResourceProvider 를 교체한다.
    /// </summary>
    internal class CachedLocalProvider : IResourceProvider
    {
        public string ResourceProviderName => "LOCAL_FILES";

        private readonly string _query;
        private readonly string _folder;
        private readonly string _pattern;
        private readonly Func<string, bool> _predicate; // null 가능
        private readonly string _scheme;

        private readonly NameCache _cache;
        private readonly IResourceProvider _original;

        // 3초 메모 캐시
        private List<Resource> _memo;
        private float _memoExpiry;

        // 동등성 검증 — 쿼리별 최초 1회만
        private bool _verified;
        private const bool VerifyAgainstOriginal = true;

        public CachedLocalProvider(
            string query,
            string folder,
            string pattern,
            Func<string, bool> predicate,
            string scheme,
            NameCache cache,
            IResourceProvider original)
        {
            _query = query;
            _folder = folder;
            _pattern = pattern;
            _predicate = predicate;
            _scheme = scheme;
            _cache = cache;
            _original = original;
        }

        public List<Resource> ProvideResources(string query)
        {
            if (query != _query) return null;

            // 3초 메모 캐시
            float now = Time.time;
            if (_memo != null && now < _memoExpiry)
            {
                return _memo;
            }

            var sw = SW.StartNew();
            bool warm = _memo != null;

            try
            {
                IEnumerable<FileEntry> entries = Context.PersistentDataManager.GetFileEntries(_folder, _pattern, _predicate);

                List<string> activePaths = new List<string>();
                List<Resource> result = new List<Resource>();

                foreach (FileEntry it in entries)
                {
                    string label = _cache.GetOrResolve(it.absolutePath, it.lastModifiedTime);
                    result.Add(new Resource
                    {
                        label = label,
                        uri = new Uri(_scheme + "://data/" + it.path)
                    });
                    activePaths.Add(it.absolutePath);
                }

                result = result.ToList().UseFilenameForDuplicates();
                _cache.PruneAndSave(activePaths);

                sw.Stop();
                Debug.Log(string.Format("[ResourceListCache] LOCAL {0}: {1} items, {2}ms ({3})",
                    _query, result.Count, sw.ElapsedMilliseconds, warm ? "warm" : "cold"));

                // 동등성 자가검증 (최초 1회)
                if (VerifyAgainstOriginal && !_verified)
                {
                    _verified = true;
                    VerifyAgainstOriginalImpl(result);
                }

                _memo = result;
                _memoExpiry = now + 3f;
                return result;
            }
            catch (Exception e)
            {
                sw.Stop();
                Debug.LogError("[ResourceListCache] CachedLocalProvider error for " + _query + ": " + e);
                // 폴백: 원본 프로바이더에 위임
                try
                {
                    return _original.ProvideResources(query);
                }
                catch (Exception e2)
                {
                    Debug.LogError("[ResourceListCache] Fallback also failed for " + _query + ": " + e2);
                    return null;
                }
            }
        }

        private void VerifyAgainstOriginalImpl(List<Resource> cached)
        {
            try
            {
                List<Resource> orig = _original.ProvideResources(_query);
                if (orig == null) orig = new List<Resource>();

                if (orig.Count != cached.Count)
                {
                    Debug.LogWarning(string.Format("[ResourceListCache] VERIFY MISMATCH {0}: cached={1} orig={2}",
                        _query, cached.Count, orig.Count));
                }

                List<string> cachedLabels = cached.Select(r => r.label).OrderBy(l => l, StringComparer.Ordinal).ToList();
                List<string> origLabels = orig.Select(r => r.label).OrderBy(l => l, StringComparer.Ordinal).ToList();
                List<string> onlyInCached = cachedLabels.Except(origLabels, StringComparer.Ordinal).ToList();
                List<string> onlyInOrig = origLabels.Except(cachedLabels, StringComparer.Ordinal).ToList();

                if (onlyInCached.Count > 0 || onlyInOrig.Count > 0)
                {
                    Debug.LogWarning(string.Format("[ResourceListCache] VERIFY LABEL DIFF {0}: only_in_cached=[{1}] only_in_orig=[{2}]",
                        _query,
                        string.Join(", ", onlyInCached),
                        string.Join(", ", onlyInOrig)));
                }
                else
                {
                    Debug.Log("[ResourceListCache] VERIFY OK " + _query);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] VERIFY exception for " + _query + ": " + e.Message);
            }
        }
    }

    // ─── CachedWorkshopProvider ─────────────────────────────────────────────────

    /// <summary>
    /// Steam Workshop용 캐싱 프로바이더.
    /// 원본 Workshop{Prop,Environment,Character,Particle}ResourceProvider 를 교체한다.
    /// </summary>
    internal class CachedWorkshopProvider : IResourceProvider
    {
        public string ResourceProviderName => "STEAM_WORKSHOP";

        private readonly string _query;
        private readonly string _folder;
        private readonly Func<string, bool> _isSupportedFile;
        private readonly string _scheme;

        private readonly NameCache _cache;
        private readonly IResourceProvider _original;
        private readonly Func<SteamWorkshop> _workshopGetter;

        // 3초 메모 캐시
        private List<Resource> _memo;
        private float _memoExpiry;

        // 동등성 검증 — 쿼리별 최초 1회만
        private bool _verified;
        private const bool VerifyAgainstOriginal = true;

        public CachedWorkshopProvider(
            string query,
            string folder,
            Func<string, bool> isSupportedFile,
            string scheme,
            NameCache cache,
            IResourceProvider original,
            Func<SteamWorkshop> workshopGetter)
        {
            _query = query;
            _folder = folder;
            _isSupportedFile = isSupportedFile;
            _scheme = scheme;
            _cache = cache;
            _original = original;
            _workshopGetter = workshopGetter;
        }

        public List<Resource> ProvideResources(string query)
        {
            if (query != _query) return null;

            // 3초 메모 캐시
            float now = Time.time;
            if (_memo != null && now < _memoExpiry)
            {
                return _memo;
            }

            var sw = SW.StartNew();
            bool warm = _memo != null;

            try
            {
                SteamWorkshop workshop = _workshopGetter();
                if (workshop == null)
                {
                    Debug.LogWarning("[ResourceListCache] SteamWorkshop is null for " + _query);
                    return _original.ProvideResources(query);
                }

                IEnumerable<WorkshopFileEntry> entries = workshop.GetFileEntries(_folder, "*.*", _isSupportedFile);

                List<string> activePaths = new List<string>();
                List<Resource> result = new List<Resource>();

                foreach (WorkshopFileEntry it in entries)
                {
                    string fullPath = workshop.GetFullPath(it.itemId, it.relativePath);
                    string label = _cache.GetOrResolve(fullPath, it.lastModifiedTime);
                    result.Add(new Resource
                    {
                        label = label,
                        uri = new Uri(_scheme + "://workshop/" + it.itemId + "/" + it.relativePath)
                    });
                    if (fullPath != null) activePaths.Add(fullPath);
                }

                // 워크숍은 UseFilenameForDuplicates 적용 안 함 (원본과 동일)
                _cache.PruneAndSave(activePaths);

                sw.Stop();
                Debug.Log(string.Format("[ResourceListCache] WORKSHOP {0}: {1} items, {2}ms ({3})",
                    _query, result.Count, sw.ElapsedMilliseconds, warm ? "warm" : "cold"));

                // 동등성 자가검증 (최초 1회)
                if (VerifyAgainstOriginal && !_verified)
                {
                    _verified = true;
                    VerifyAgainstOriginalImpl(result);
                }

                _memo = result;
                _memoExpiry = now + 3f;
                return result;
            }
            catch (Exception e)
            {
                sw.Stop();
                Debug.LogError("[ResourceListCache] CachedWorkshopProvider error for " + _query + ": " + e);
                // 폴백: 원본 프로바이더에 위임
                try
                {
                    return _original.ProvideResources(query);
                }
                catch (Exception e2)
                {
                    Debug.LogError("[ResourceListCache] Workshop fallback also failed for " + _query + ": " + e2);
                    return null;
                }
            }
        }

        private void VerifyAgainstOriginalImpl(List<Resource> cached)
        {
            try
            {
                List<Resource> orig = _original.ProvideResources(_query);
                if (orig == null) orig = new List<Resource>();

                if (orig.Count != cached.Count)
                {
                    Debug.LogWarning(string.Format("[ResourceListCache] VERIFY MISMATCH {0}: cached={1} orig={2}",
                        _query, cached.Count, orig.Count));
                }

                List<string> cachedLabels = cached.Select(r => r.label).OrderBy(l => l, StringComparer.Ordinal).ToList();
                List<string> origLabels = orig.Select(r => r.label).OrderBy(l => l, StringComparer.Ordinal).ToList();
                List<string> onlyInCached = cachedLabels.Except(origLabels, StringComparer.Ordinal).ToList();
                List<string> onlyInOrig = origLabels.Except(cachedLabels, StringComparer.Ordinal).ToList();

                if (onlyInCached.Count > 0 || onlyInOrig.Count > 0)
                {
                    Debug.LogWarning(string.Format("[ResourceListCache] VERIFY LABEL DIFF {0}: only_in_cached=[{1}] only_in_orig=[{2}]",
                        _query,
                        string.Join(", ", onlyInCached),
                        string.Join(", ", onlyInOrig)));
                }
                else
                {
                    Debug.Log("[ResourceListCache] VERIFY OK " + _query);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] VERIFY exception for " + _query + ": " + e.Message);
            }
        }
    }

    // ─── ProviderSwapper ────────────────────────────────────────────────────────

    /// <summary>
    /// ResourceManager.providers private 딕셔너리에 리플렉션으로 접근해
    /// 대상 8개 원본 프로바이더를 캐싱 구현체로 교체한다.
    /// </summary>
    internal static class ProviderSwapper
    {
        private static readonly HashSet<string> TargetProviderNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "PersistentDataDirectoryPropResourceProvider",
            "PersistentDataDirectoryEnvironmentResourceProvider",
            "PersistentDataDirectoryCharacterResourceProvider",
            "PersistentDataDirectoryParticleResourceProvider",
            "WorkshopPropResourceProvider",
            "WorkshopEnvironmentResourceProvider",
            "WorkshopCharacterResourceProvider",
            "WorkshopParticleResourceProvider",
        };

        /// <summary>
        /// 교체를 실행한다. 멱등: 이미 우리 타입이면 스킵.
        /// 리플렉션 실패 시 경고 로그만 남기고 교체하지 않는다.
        /// </summary>
        /// <returns>교체 성공한 원본 프로바이더 목록. 실패 시 null.</returns>
        public static List<IResourceProvider> SwapProviders(NameCache cache, Func<SteamWorkshop> workshopGetter)
        {
            try
            {
                // providers 필드 취득
                ResourceManager rm = Context.ResourceManager;
                FieldInfo fi = typeof(ResourceManager).GetField("providers",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi == null)
                {
                    Debug.LogWarning("[ResourceListCache] Cannot find 'providers' field on ResourceManager. No providers replaced.");
                    return null;
                }

                var providers = (Dictionary<string, List<IResourceProvider>>)fi.GetValue(rm);
                if (providers == null)
                {
                    Debug.LogWarning("[ResourceListCache] 'providers' field is null. No providers replaced.");
                    return null;
                }

                List<IResourceProvider> originals = new List<IResourceProvider>();
                int replaced = 0;

                foreach (KeyValuePair<string, List<IResourceProvider>> kv in providers)
                {
                    List<IResourceProvider> list = kv.Value;
                    for (int i = 0; i < list.Count; i++)
                    {
                        IResourceProvider prov = list[i];
                        string typeName = prov.GetType().Name;

                        if (!TargetProviderNames.Contains(typeName)) continue;

                        // 멱등: 이미 우리 타입으로 교체됐으면 스킵
                        if (prov is CachedLocalProvider || prov is CachedWorkshopProvider)
                        {
                            Debug.Log("[ResourceListCache] " + typeName + " already replaced (idempotency). Skipping.");
                            continue;
                        }

                        IResourceProvider cached = CreateCached(prov, typeName, cache, workshopGetter);
                        if (cached == null) continue;

                        originals.Add(prov);
                        list[i] = cached;
                        replaced++;
                        Debug.Log("[ResourceListCache] Replaced " + typeName);
                    }
                }

                Debug.Log("[ResourceListCache] Provider swap complete: " + replaced + " providers replaced.");
                return originals;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] ProviderSwapper failed: " + e);
                return null;
            }
        }

        private static IResourceProvider CreateCached(
            IResourceProvider original,
            string typeName,
            NameCache cache,
            Func<SteamWorkshop> workshopGetter)
        {
            switch (typeName)
            {
                case "PersistentDataDirectoryPropResourceProvider":
                    return new CachedLocalProvider(
                        query: "Prop",
                        folder: "Props",
                        pattern: "*.*",
                        predicate: it => it.ToLowerOptimized().EndsWith(".warudo"),
                        scheme: "prop",
                        cache: cache,
                        original: original);

                case "PersistentDataDirectoryEnvironmentResourceProvider":
                    return new CachedLocalProvider(
                        query: "Environment",
                        folder: "Environments",
                        pattern: "*.warudo",
                        predicate: null,
                        scheme: "environment",
                        cache: cache,
                        original: original);

                case "PersistentDataDirectoryCharacterResourceProvider":
                    return new CachedLocalProvider(
                        query: "Character",
                        folder: "Characters",
                        pattern: "*.*",
                        predicate: it => { string l = it.ToLowerOptimized(); return l.EndsWith(".vrm") || l.EndsWith(".warudo"); },
                        scheme: "character",
                        cache: cache,
                        original: original);

                case "PersistentDataDirectoryParticleResourceProvider":
                    return new CachedLocalProvider(
                        query: "Particle",
                        folder: "Particles",
                        pattern: "*.*",
                        predicate: it => it.ToLowerOptimized().EndsWith(".warudo"),
                        scheme: "particle",
                        cache: cache,
                        original: original);

                case "WorkshopPropResourceProvider":
                    return new CachedWorkshopProvider(
                        query: "Prop",
                        folder: "Props",
                        isSupportedFile: it => it.ToLowerOptimized().EndsWith(".warudo"),
                        scheme: "prop",
                        cache: cache,
                        original: original,
                        workshopGetter: workshopGetter);

                case "WorkshopEnvironmentResourceProvider":
                    return new CachedWorkshopProvider(
                        query: "Environment",
                        folder: "Environments",
                        isSupportedFile: it => it.ToLowerOptimized().EndsWith(".warudo"),
                        scheme: "environment",
                        cache: cache,
                        original: original,
                        workshopGetter: workshopGetter);

                case "WorkshopCharacterResourceProvider":
                    return new CachedWorkshopProvider(
                        query: "Character",
                        folder: "Characters",
                        isSupportedFile: it => { string l = it.ToLowerOptimized(); return l.EndsWith(".vrm") || l.EndsWith(".warudo"); },
                        scheme: "character",
                        cache: cache,
                        original: original,
                        workshopGetter: workshopGetter);

                case "WorkshopParticleResourceProvider":
                    return new CachedWorkshopProvider(
                        query: "Particle",
                        folder: "Particles",
                        isSupportedFile: it => it.ToLowerOptimized().EndsWith(".warudo"),
                        scheme: "particle",
                        cache: cache,
                        original: original,
                        workshopGetter: workshopGetter);

                default:
                    Debug.LogWarning("[ResourceListCache] Unknown target provider type: " + typeName);
                    return null;
            }
        }
    }

    // ─── Plugin ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Warudo Prop/Environment/Character/Particle 소스 드롭다운 응답 속도 개선을 위한
    /// 리소스 목록 캐싱 플러그인.
    /// </summary>
    [PluginType(
        Id = "Moraecat.ResourceListCache",
        Name = "Resource List Cache",
        Description = "Prop/Environment/Character/Particle 소스 드롭다운 캐싱으로 응답 속도 개선",
        Author = "moraecat",
        Version = "0.1.0",
        AssetTypes = new Type[] { },
        NodeTypes = new Type[] { })]
    public class ResourceListCachePlugin : Plugin
    {
        private NameCache _cache;

        protected override void OnCreate()
        {
            base.OnCreate();
            InitAsync().Forget();
        }

        private async UniTaskVoid InitAsync()
        {
            Debug.Log("[ResourceListCache] InitAsync started. Waiting for CommunityPlugin and providers...");

            const float TimeoutSeconds = 30f;
            float deadline = Time.time + TimeoutSeconds;

            // CommunityPlugin 이 로드되고 SteamWorkshop 이 null 이 아닐 때까지 대기
            // 또한 providers 딕셔너리에 교체 대상 8종이 모두 등록될 때까지 대기
            while (true)
            {
                if (Time.time > deadline)
                {
                    Debug.LogWarning("[ResourceListCache] Timed out waiting for CommunityPlugin/providers. No providers replaced.");
                    return;
                }

                CommunityPlugin community = Context.PluginManager.GetPlugin<CommunityPlugin>();
                if (community == null)
                {
                    await UniTask.Yield();
                    continue;
                }

                SteamWorkshop workshop = community.SteamWorkshop;
                if (workshop == null)
                {
                    await UniTask.Yield();
                    continue;
                }

                // providers 필드 체크
                if (!AreTargetProvidersRegistered())
                {
                    await UniTask.Yield();
                    continue;
                }

                // 모두 준비됨
                break;
            }

            Debug.Log("[ResourceListCache] All prerequisites ready. Initializing NameCache and swapping providers...");

            try
            {
                _cache = new NameCache();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ResourceListCache] NameCache init failed: " + e.Message);
                _cache = new NameCache(); // 다시 시도 (빈 캐시)
            }

            CommunityPlugin comm = Context.PluginManager.GetPlugin<CommunityPlugin>();
            Func<SteamWorkshop> workshopGetter = () => Context.PluginManager.GetPlugin<CommunityPlugin>()?.SteamWorkshop;

            List<IResourceProvider> originals = ProviderSwapper.SwapProviders(_cache, workshopGetter);
            if (originals == null || originals.Count == 0)
            {
                Debug.LogWarning("[ResourceListCache] No providers were replaced.");
            }
            else
            {
                Debug.Log("[ResourceListCache] Plugin initialized. " + originals.Count + " providers replaced.");
            }
        }

        /// <summary>
        /// providers 딕셔너리에 교체 대상 8종이 모두 등록됐는지 확인한다.
        /// </summary>
        private bool AreTargetProvidersRegistered()
        {
            try
            {
                ResourceManager rm = Context.ResourceManager;
                FieldInfo fi = typeof(ResourceManager).GetField("providers",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi == null) return false;

                var providers = (Dictionary<string, List<IResourceProvider>>)fi.GetValue(rm);
                if (providers == null) return false;

                HashSet<string> found = new HashSet<string>(StringComparer.Ordinal);
                foreach (List<IResourceProvider> list in providers.Values)
                {
                    foreach (IResourceProvider p in list)
                    {
                        found.Add(p.GetType().Name);
                    }
                }

                HashSet<string> required = new HashSet<string>(StringComparer.Ordinal)
                {
                    "PersistentDataDirectoryPropResourceProvider",
                    "PersistentDataDirectoryEnvironmentResourceProvider",
                    "PersistentDataDirectoryCharacterResourceProvider",
                    "PersistentDataDirectoryParticleResourceProvider",
                    "WorkshopPropResourceProvider",
                    "WorkshopEnvironmentResourceProvider",
                    "WorkshopCharacterResourceProvider",
                    "WorkshopParticleResourceProvider",
                };

                return required.IsSubsetOf(found);
            }
            catch
            {
                return false;
            }
        }
    }
}
