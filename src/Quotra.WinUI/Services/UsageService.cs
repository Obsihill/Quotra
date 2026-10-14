using System.IO;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Quotra.WinUI.Models;

namespace Quotra.WinUI.Services;

/// <summary>
/// 프로바이더 카탈로그와 사용량 소스를 묶는 곳.
///
/// 카탈로그(<c>Data/providers.json</c>)는 **무엇을 보여줄지**만 정의한다(이름·색·한도 창 라벨).
/// **얼마나 썼는지**는 <see cref="IUsageSource"/> 구현체만 채운다 — 목업 난수는 없다.
/// 소스가 등록되지 않은 프로바이더는 값이 <c>null</c> 인 채로 남고 화면에는 "미연동"으로 나온다.
/// </summary>
public sealed class UsageService
{
    private readonly List<ProviderUsage> _providers = [];
    private readonly Dictionary<string, IUsageSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _catalogPath;
    private readonly string _embeddedName;
    private DispatcherQueueTimer? _timer;
    private bool _refreshing;
    private readonly Dictionary<string, int> _credentialRevisions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>API 키 입력형 소스(설정 창이 프로바이더를 자동으로 나열하는 근거). 카탈로그 순서를 따른다.</summary>
    public IReadOnlyList<(string Id, IKeyedUsageSource Source)> KeyedSources
    {
        get
        {
            List<(string Id, IKeyedUsageSource Source)> list = [];
            foreach (ProviderUsage provider in _providers)
            {
                if (_sources.TryGetValue(provider.Id, out IUsageSource? source) && source is IKeyedUsageSource keyed)
                {
                    list.Add((provider.Id, keyed));
                }
            }

            return list;
        }
    }

    /// <summary>자격증명이 바뀐 프로바이더의 표시 값을 비운다(다음 조회 결과가 채운다).</summary>
    public void ClearValues(string id)
    {
        _credentialRevisions[id] = _credentialRevisions.TryGetValue(id, out int count) ? count + 1 : 1;
        foreach (var provider in _providers.Where(p => p.Id == id))
        {
            provider.Primary = provider.Secondary = null;
            provider.PrimaryResetsAt = provider.SecondaryResetsAt = null;
            provider.Plan = provider.CatalogPlan;
            provider.Note = provider.CatalogNote;
            provider.UpdatedAt = null;
            provider.Error = null;
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public UsageService(string? catalogPath = null)
    {
        _catalogPath = catalogPath ?? Path.Combine(AppContext.BaseDirectory, "Data", "providers.json");
        _embeddedName = Path.GetFileName(_catalogPath);
    }

    /// <summary>화면에 반영할 프로바이더(카탈로그의 Enabled 만).</summary>
    public IReadOnlyList<ProviderUsage> Providers => _providers;

    /// <summary>읽어들인 카탈로그 경로.</summary>
    public string CatalogPath => _catalogPath;

    /// <summary>등록된 소스(프로바이더 id → 소스).</summary>
    public IReadOnlyDictionary<string, IUsageSource> Sources => _sources;

    /// <summary>카탈로그를 못 읽었을 때의 안내(정상이면 null).</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>마지막 갱신 시각.</summary>
    public DateTimeOffset? LastRefreshAt { get; private set; }

    /// <summary>값이 갱신될 때마다 발생.</summary>
    public event EventHandler? Updated;

    /// <summary>카탈로그에서 프로바이더 정의를 읽는다. 값은 비어 있다(소스가 채운다).</summary>
    public void Load()
    {
        List<ProviderUsage>? catalog = ReadFileCatalog() ?? ReadEmbeddedCatalog();

        _providers.Clear();

        if (catalog is null)
        {
            LoadWarning = $"{Path.GetFileName(_catalogPath)} 을 읽지 못했습니다 — 프로바이더 없이 시작합니다.";
            return;
        }

        foreach (ProviderUsage provider in catalog.Where(p => p.Enabled))
        {
            provider.IsLinked = _sources.ContainsKey(provider.Id);
            provider.CatalogPlan = provider.Plan;
            provider.CatalogNote = provider.Note;
            _providers.Add(provider);
        }
    }

    /// <summary>프로바이더 한 곳에 사용량 소스를 연결한다(확장 지점).</summary>
    public void Register(string providerId, IUsageSource source)
    {
        _sources[providerId] = source;
        Log.Write($"source 등록: {providerId} ← {source.DisplayName} [{source.ShortName}]");

        foreach (ProviderUsage provider in _providers.Where(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)))
        {
            provider.IsLinked = true;
        }
    }

    /// <summary>카탈로그의 모든 프로바이더에 같은 소스 팩토리를 붙인다(개발용 하네스).</summary>
    public void RegisterAll(Func<ProviderUsage, IUsageSource> factory)
    {
        foreach (ProviderUsage provider in _providers)
        {
            Register(provider.Id, factory(provider));
        }
    }

    /// <summary>주기 갱신 시작(즉시 1회 + 반복). 소스가 없으면 할 일이 없다.</summary>
    public void StartRefresh(DispatcherQueue queue, TimeSpan interval)
    {
        _ = RefreshAsync();

        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>등록된 소스에서 값을 가져와 모델에 반영한다. 소스가 없으면 아무 값도 만들지 않는다.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        Dictionary<string, int> revisions = new(_credentialRevisions, StringComparer.OrdinalIgnoreCase);
        bool credentialsChanged = false;

        try
        {
            List<(ProviderUsage Provider, IUsageSource Source)> jobs = [];

            foreach (ProviderUsage provider in _providers)
            {
                if (_sources.TryGetValue(provider.Id, out IUsageSource? source))
                {
                    jobs.Add((provider, source));
                }
            }

            if (jobs.Count == 0)
            {
                // 연동된 프로바이더가 없다 — 값은 null 로 남는다(화면은 "미연동").
                return;
            }

            // 소스가 UI 스레드를 붙잡지 않도록 워커에서 시작한다.
            var tasks = jobs
                .Select(job => (job.Provider, Task: Task.Run(() => job.Source.FetchAsync(CancellationToken.None))))
                .ToList();

            try
            {
                await Task.WhenAll(tasks.Select(t => t.Task));
            }
            catch
            {
                // 개별 실패는 아래에서 프로바이더별로 처리한다.
            }

            DateTimeOffset now = DateTimeOffset.Now;

            foreach ((ProviderUsage provider, Task<ProviderSnapshot> task) in tasks)
            {
                if (revisions.GetValueOrDefault(provider.Id) != _credentialRevisions.GetValueOrDefault(provider.Id)) continue;
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    Apply(provider, task.Result, now);
                }
                else
                {
                    Exception failure = task.Exception?.GetBaseException() ?? new InvalidOperationException("알 수 없는 오류");
                    ApplyFailure(provider, failure);
                    Log.Write($"{(failure is ProviderNotConnectedException ? "등록 안 됨" : "조회 실패")}: {provider.Name} — {failure.Message}");
                }
            }

            LastRefreshAt = now;
            credentialsChanged = tasks.Any(t => revisions.GetValueOrDefault(t.Provider.Id) != _credentialRevisions.GetValueOrDefault(t.Provider.Id));
            Updated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshing = false;
            if (credentialsChanged) _ = RefreshAsync();
        }
    }

    internal static void ApplyFailure(ProviderUsage provider, Exception failure)
    {
        if (failure is not ProviderNotConnectedException)
        {
            provider.Error = failure.Message;
            return;
        }
        provider.IsLinked = false;
        provider.Primary = provider.Secondary = null;
        provider.PrimaryResetsAt = provider.SecondaryResetsAt = null;
        provider.UpdatedAt = null;
        provider.HistoryKey = null;
        provider.Plan = provider.CatalogPlan;
        provider.Note = failure.Message;
        provider.Error = null;
    }

    internal static void Apply(ProviderUsage provider, ProviderSnapshot snapshot, DateTimeOffset now)
    {
        provider.IsLinked = true;
        provider.Error = null;
        provider.UpdatedAt = snapshot.CapturedAt ?? now;

        provider.Primary = snapshot.Primary is { } primary ? Math.Clamp(primary.Used, 0, 1) : null;
        provider.PrimaryResetsAt = snapshot.Primary?.ResetsAt;
        provider.Secondary = snapshot.Secondary is { } secondary ? Math.Clamp(secondary.Used, 0, 1) : null;
        provider.SecondaryResetsAt = snapshot.Secondary?.ResetsAt;
        if (snapshot.PrimaryLabel is not null) provider.PrimaryLabel = snapshot.PrimaryLabel;
        if (snapshot.SecondaryLabel is not null) provider.SecondaryLabel = snapshot.SecondaryLabel;

        // 플랜·설명은 조회 결과가 알려주면 그 값, 아니면 카탈로그 기본값으로 되돌린다 —
        // 이전 조회의 문구가 남아 사실과 달라지는 것을 막는다(예: API 키를 지운 뒤 "월간 잔여 ..%" 잔존).
        provider.Plan = string.IsNullOrWhiteSpace(snapshot.Plan) ? provider.CatalogPlan : snapshot.Plan;
        provider.Note = string.IsNullOrWhiteSpace(snapshot.Note) ? provider.CatalogNote : snapshot.Note;

        provider.HistoryKey = snapshot.HistoryKey;
    }

    // ------------------------------------------------------------ 카탈로그 읽기

    private List<ProviderUsage>? ReadFileCatalog()
    {
        try
        {
            return File.Exists(_catalogPath) ? Deserialize(File.ReadAllText(_catalogPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadWarning = $"{Path.GetFileName(_catalogPath)} 을 읽지 못했습니다 ({ex.GetType().Name}).";
            Log.Write(ex, "카탈로그 읽기 실패");
            return null;
        }
    }

    private List<ProviderUsage>? ReadEmbeddedCatalog()
    {
        try
        {
            var assembly = typeof(UsageService).Assembly;
            string? name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith(_embeddedName, StringComparison.Ordinal));

            if (name is null)
            {
                return null;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return Deserialize(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            Log.Write(ex, "내장 카탈로그 읽기 실패");
            return null;
        }
    }

    private static List<ProviderUsage>? Deserialize(string json) =>
        JsonSerializer.Deserialize<List<ProviderUsage>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
}
