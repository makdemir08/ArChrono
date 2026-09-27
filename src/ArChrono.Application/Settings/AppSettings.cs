using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.Git.Services;
using ArChrono.Recovery.Retention;
using ArChrono.Storage.Stores;

namespace ArChrono.Application.Settings;

public enum UiMode
{
    /// <summary>Doğal dil, açıklamalar, Git terimleri arka planda.</summary>
    Guided,
    /// <summary>Gerçek Git terminolojisi ve komut önizlemeleri.</summary>
    Pro,
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum LanguagePreference
{
    /// <summary>İşletim sisteminin dili (Türkçe değilse İngilizce).</summary>
    System,
    English,
    Turkish,
}

public enum SnapshotRetention
{
    OneDay,
    SevenDays,
    ThirtyDays,
    NinetyDays,
    UntilDiskLimit,
}

public sealed record TimeMachineSettings
{
    public bool Enabled { get; init; } = true;
    public int IntervalMinutes { get; init; } = 5;
    public SnapshotRetention Keep { get; init; } = SnapshotRetention.ThirtyDays;
    public double MaxStorageGigabytes { get; init; } = 5;
    public int MaxFileSizeMegabytes { get; init; } = 25;
    public bool PinInRepository { get; init; } = true;

    public RetentionPolicy ToPolicy() => new()
    {
        MaxAge = Keep switch
        {
            SnapshotRetention.OneDay => TimeSpan.FromDays(1),
            SnapshotRetention.SevenDays => TimeSpan.FromDays(7),
            SnapshotRetention.ThirtyDays => TimeSpan.FromDays(30),
            SnapshotRetention.NinetyDays => TimeSpan.FromDays(90),
            _ => null,
        },
        MaxStorageBytes = (long)(MaxStorageGigabytes * 1024 * 1024 * 1024),
    };
}

public sealed record AiSettings
{
    public bool Enabled { get; init; }
    public AiProviderSettings Provider { get; init; } = new();
    public bool AlwaysPreview { get; init; } = true;
    public CommitMessageStyle CommitStyle { get; init; } = CommitMessageStyle.Conventional;
    public IReadOnlyList<string> ExcludedPatterns { get; init; } = SensitivePathFilter.DefaultPatterns;
}

public sealed record GitSettings
{
    public string? ExecutablePath { get; init; }
    public PullMode PullMode { get; init; } = PullMode.Default;
    public bool AutoFetch { get; init; } = true;
    public int AutoFetchMinutes { get; init; } = 10;
    public bool Autostash { get; init; } = true;
}

public sealed record AppSettings
{
    public UiMode Mode { get; init; } = UiMode.Guided;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public LanguagePreference Language { get; init; } = LanguagePreference.System;
    public bool OnboardingCompleted { get; init; }
    public string? TerminalApplication { get; init; }
    public bool TelemetryOptIn { get; init; }
    public TimeMachineSettings TimeMachine { get; init; } = new();
    public AiSettings Ai { get; init; } = new();
    public GitSettings Git { get; init; } = new();
}

public sealed class SettingsService(SettingsStore store)
{
    private const string Key = "app";
    private AppSettings? _current;

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => _current ??= Load();

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var updated = change(Current);
        _current = updated;
        store.Set(Key, updated);
        Changed?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        try
        {
            return store.Get<AppSettings>(Key) ?? new AppSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            return new AppSettings();
        }
    }
}
