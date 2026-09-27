using ArChrono.Git;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Snapshots;

/// <summary><c>refs/archrono/pins/&lt;sha&gt;</c> ref'lerini yönetir: recovery için gereken nesneleri gc'den korur.</summary>
public sealed class PinManager(ArChronoStorage storage, Func<bool> isEnabled)
{
    public bool IsEnabled => isEnabled();

    public async Task EnsurePinnedAsync(GitRepository repository, IEnumerable<string> shas, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return;
        var commonDir = repository.Info.CommonDir;
        var missing = shas.Where(s => !string.IsNullOrEmpty(s)).Distinct().Where(s => !storage.Pins.Exists(commonDir, s)).ToList();
        if (missing.Count == 0) return;

        // "update" beklenen değer olmadan idempotenttir: ref zaten varsa aynı değere yazılır.
        await repository.Refs.UpdateRefsAsync(missing.Select(s => new RefUpdate(RefNames.PinsPrefix + s, s)).ToList(),
            "archrono: protect recovery data", cancellationToken, isInternal: true).ConfigureAwait(false);
        foreach (var sha in missing) storage.Pins.Add(commonDir, sha);
    }

    /// <summary>Hiçbir snapshot veya recovery point tarafından kullanılmayan pin ref'lerini siler. Asla git gc çalıştırmaz.</summary>
    public async Task<int> RemoveUnreferencedAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        var commonDir = repository.Info.CommonDir;
        var unreferenced = storage.Pins.GetUnreferenced(commonDir);
        if (unreferenced.Count == 0) return 0;

        var existing = await repository.Refs.GetRefTargetsAsync(cancellationToken, RefNames.PinsPrefix).ConfigureAwait(false);
        var deletions = unreferenced.Where(s => existing.ContainsKey(RefNames.PinsPrefix + s))
            .Select(s => new RefUpdate(RefNames.PinsPrefix + s, null)).ToList();
        if (deletions.Count > 0)
            await repository.Refs.UpdateRefsAsync(deletions, "archrono: release recovery data", cancellationToken, isInternal: true).ConfigureAwait(false);
        storage.Pins.Remove(commonDir, unreferenced);
        return unreferenced.Count;
    }

    /// <summary>"Remove ArChrono data from this repository": tüm pin ref'lerini siler.</summary>
    public async Task<int> RemoveAllAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        var existing = await repository.Refs.GetRefTargetsAsync(cancellationToken, RefNames.ArChronoPrefix).ConfigureAwait(false);
        if (existing.Count > 0)
            await repository.Refs.UpdateRefsAsync(existing.Keys.Select(r => new RefUpdate(r, null)).ToList(), "archrono: remove data", cancellationToken, isInternal: true).ConfigureAwait(false);
        storage.Pins.Remove(repository.Info.CommonDir, storage.Pins.List(repository.Info.CommonDir));
        return existing.Count;
    }
}
