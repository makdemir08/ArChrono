using System.Text.RegularExpressions;
using ArChrono.Git.Process;
using ArChrono.Localization;

namespace ArChrono.Git.Errors;

/// <summary>stderr kalıplarını <see cref="GitError"/>'a çevirir. Ham çıktı teknik detay olarak korunur.</summary>
public static partial class GitErrorTranslator
{
    private sealed record Rule(Func<string, Match?> Matcher, Func<Match, (GitErrorCode Code, string Title, string Explanation, string? Suggestion)> Build);

    private static Match? Find(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant) is { Success: true } m ? m : null;

    private static readonly Rule[] Rules =
    [
        new(t => Find(t, @"not a git repository"),
            _ => (GitErrorCode.NotARepository, Loc.T("This folder is not a Git repository.", "Bu klasör bir Git deposu değil."),
                Loc.T("Git could not find a repository in this folder or any of its parents.", "Git bu klasörde veya üst klasörlerinde bir depo bulamadı."),
                Loc.T("Choose a different folder, or initialize a new repository here.", "Başka bir klasör seçin ya da burada yeni bir depo oluşturun."))),

        new(t => Find(t, @"must be run in a work tree"),
            _ => (GitErrorCode.BareRepositoryNotSupported, Loc.T("Bare repositories are not supported yet.", "Bare depolar henüz desteklenmiyor."),
                Loc.T("This repository has no working directory, so there is nothing to show or snapshot.", "Bu deponun çalışma dizini yok; gösterilecek veya anlık görüntüsü alınacak bir şey bulunmuyor."),
                Loc.T("Clone it into a normal folder to work with it.", "Üzerinde çalışmak için normal bir klasöre klonlayın."))),

        new(t => Find(t, @"untracked working tree files would be (?:overwritten|removed)"),
            _ => (GitErrorCode.UntrackedFilesWouldBeOverwritten, Loc.T("New files in your folder are in the way.", "Klasörünüzdeki yeni dosyalar engel oluyor."),
                Loc.T("Some files that are not tracked by Git would be overwritten by this operation.", "Git tarafından takip edilmeyen bazı dosyaların üzerine bu işlem yazacaktı."),
                Loc.T("Move or commit those files first. ArChrono snapshots keep a copy of your current work.", "Önce bu dosyaları taşıyın veya commit edin. ArChrono anlık görüntüleri mevcut çalışmanızın bir kopyasını saklar."))),

        new(t => Find(t, @"local changes to the following files would be overwritten|Your local changes would be overwritten|would be overwritten by (?:checkout|merge)"),
            _ => (GitErrorCode.LocalChangesWouldBeOverwritten, Loc.T("Your uncommitted changes would be lost.", "Commit edilmemiş değişiklikleriniz kaybolacaktı."),
                Loc.T("Git stopped because this operation would overwrite changes you have not committed yet.", "Bu işlem henüz commit etmediğiniz değişikliklerin üzerine yazacağı için Git durdu."),
                Loc.T("Save your current work (commit or put it aside with stash) and try again.", "Mevcut çalışmanızı kaydedin (commit edin veya stash ile kenara koyun) ve tekrar deneyin."))),

        new(t => Find(t, @"^CONFLICT \(|Automatic merge failed|could not apply|Resolve all conflicts manually"),
            _ => (GitErrorCode.MergeConflict, Loc.T("Some files need your decision.", "Bazı dosyalar kararınızı bekliyor."),
                Loc.T("The same parts of some files were changed on both sides, so Git could not combine them automatically.", "Bazı dosyaların aynı bölümleri iki tarafta da değiştiği için Git bunları otomatik birleştiremedi."),
                Loc.T("Resolve the conflicts, or undo to go back to the state before this operation.", "Çakışmaları çözün ya da bu işlemden önceki duruma dönmek için geri alın."))),

        new(t => Find(t, @"you need to resolve your current index first|Committing is not possible because you have unmerged files|unmerged files"),
            _ => (GitErrorCode.UnmergedFiles, Loc.T("There are unresolved conflicts.", "Çözülmemiş çakışmalar var."),
                Loc.T("Git cannot continue while some files are still marked as conflicted.", "Bazı dosyalar hâlâ çakışmalı olarak işaretliyken Git devam edemez."),
                Loc.T("Finish resolving the conflicts or abort the current operation.", "Çakışmaları çözmeyi bitirin veya mevcut işlemi iptal edin."))),

        new(t => Find(t, @"stale info|\(stale info\)"),
            _ => (GitErrorCode.PushRejectedStaleLease, Loc.T("The remote branch changed since you last fetched.", "Uzak branch son fetch'inizden bu yana değişti."),
                Loc.T("Force push was stopped to protect someone else's commits (force-with-lease).", "Başka birinin commit'lerini korumak için force push durduruldu (force-with-lease)."),
                Loc.T("Fetch, review the new commits, then try again.", "Fetch yapın, yeni commit'leri inceleyin ve tekrar deneyin."))),

        new(t => Find(t, @"\[rejected\].*(?:non-fast-forward|fetch first)|Updates were rejected because"),
            _ => (GitErrorCode.PushRejectedNonFastForward, Loc.T("The remote has commits you don't have yet.", "Uzak depoda sizde henüz olmayan commit'ler var."),
                Loc.T("Your push was rejected because the remote branch contains work that is not in your local branch.", "Uzak branch yerel branch'inizde olmayan çalışmalar içerdiği için push reddedildi."),
                Loc.T("Pull first to combine the changes, then push again.", "Değişiklikleri birleştirmek için önce pull yapın, sonra tekrar push edin."))),

        new(t => Find(t, @"Authentication failed|Permission denied \(publickey|could not read Username|terminal prompts disabled|Invalid username or password|HTTP Basic: Access denied"),
            _ => (GitErrorCode.AuthenticationFailed, Loc.T("The remote did not accept your credentials.", "Uzak sunucu kimlik bilgilerinizi kabul etmedi."),
                Loc.T("Git could not sign in to the remote repository.", "Git uzak depoda oturum açamadı."),
                Loc.T("Check your SSH key or credential helper (Git Credential Manager / macOS Keychain), then retry.", "SSH anahtarınızı veya kimlik bilgisi yardımcınızı (Git Credential Manager / macOS Anahtar Zinciri) kontrol edip tekrar deneyin."))),

        new(t => Find(t, @"Could not resolve host|Connection timed out|Network is unreachable|Failed to connect to|Connection refused|unable to access .*: (?:SSL|Could not)"),
            _ => (GitErrorCode.NetworkUnavailable, Loc.T("The remote could not be reached.", "Uzak sunucuya ulaşılamadı."),
                Loc.T("Git could not connect to the remote server.", "Git uzak sunucuya bağlanamadı."),
                Loc.T("Check your internet connection or VPN and try again.", "İnternet bağlantınızı veya VPN'inizi kontrol edip tekrar deneyin."))),

        new(t => Find(t, @"does not appear to be a git repository|No such remote|repository '.*' not found"),
            _ => (GitErrorCode.RemoteNotFound, Loc.T("The remote repository was not found.", "Uzak depo bulunamadı."),
                Loc.T("The remote name or URL does not point to an accessible repository.", "Uzak depo adı veya URL erişilebilir bir depoyu göstermiyor."),
                Loc.T("Check the remote URL in repository settings.", "Depo ayarlarındaki uzak URL'yi kontrol edin."))),

        new(t => Find(t, @"index\.lock'?: File exists|Unable to create '.*\.lock'"),
            _ => (GitErrorCode.RepositoryLocked, Loc.T("Another Git process is using this repository.", "Bu depoyu başka bir Git işlemi kullanıyor."),
                Loc.T("Git found a lock file, which usually means another Git command is still running.", "Git bir kilit dosyası buldu; bu genellikle başka bir Git komutunun hâlâ çalıştığı anlamına gelir."),
                Loc.T("Wait for the other command to finish. If none is running, the lock file may be left over from a crash.", "Diğer komutun bitmesini bekleyin. Çalışan bir komut yoksa kilit dosyası bir çökmeden kalmış olabilir."))),

        new(t => Find(t, @"is not fully merged"),
            _ => (GitErrorCode.BranchNotFullyMerged, Loc.T("This branch has commits that are not merged anywhere.", "Bu branch'te hiçbir yere merge edilmemiş commit'ler var."),
                Loc.T("Deleting it would make those commits hard to find with plain Git.", "Silmek, bu commit'leri düz Git ile bulmayı zorlaştırır."),
                Loc.T("ArChrono keeps a recovery point, so you can force delete and still undo.", "ArChrono bir kurtarma noktası saklar; zorla silseniz bile geri alabilirsiniz."))),

        new(t => Find(t, @"is already (?:checked out|used by worktree) at '(?<path>[^']+)'"),
            m => (GitErrorCode.BranchCheckedOutElsewhere, Loc.T("This branch is open in another worktree.", "Bu branch başka bir worktree'de açık."),
                Loc.T($"The branch is currently checked out at {m.Groups["path"].Value}.", $"Branch şu anda {m.Groups["path"].Value} konumunda açık."),
                Loc.T("Switch that worktree to a different branch first.", "Önce o worktree'yi başka bir branch'e geçirin."))),

        new(t => Find(t, @"nothing to commit|no changes added to commit|nothing added to commit"),
            _ => (GitErrorCode.NothingToCommit, Loc.T("There is nothing to commit.", "Commit edilecek bir şey yok."),
                Loc.T("No changes are staged for the commit.", "Commit için stage edilmiş değişiklik yok."),
                Loc.T("Stage the files you want to include first.", "Önce eklemek istediğiniz dosyaları stage edin."))),

        new(t => Find(t, @"Please tell me who you are|unable to auto-detect email address|empty ident name"),
            _ => (GitErrorCode.IdentityNotConfigured, Loc.T("Git doesn't know your name and email yet.", "Git henüz adınızı ve e-postanızı bilmiyor."),
                Loc.T("Every commit records an author name and email.", "Her commit bir yazar adı ve e-postası kaydeder."),
                Loc.T("Set user.name and user.email in Settings → Git.", "Ayarlar → Git bölümünden user.name ve user.email değerlerini girin."))),

        new(t => Find(t, @"refusing to merge unrelated histories"),
            _ => (GitErrorCode.UnrelatedHistories, Loc.T("These branches have no common history.", "Bu branch'lerin ortak geçmişi yok."),
                Loc.T("Git refuses to combine branches that never shared a commit.", "Git, hiç ortak commit'i olmayan branch'leri birleştirmeyi reddeder."),
                Loc.T("If this is intentional, merge with 'Allow unrelated histories'.", "Bilerek yapıyorsanız 'Allow unrelated histories' ile merge edin."))),

        new(t => Find(t, @"cannot (?:rebase|pull with rebase): You have unstaged changes|Please commit or stash them|contains uncommitted changes|Your index contains uncommitted changes"),
            _ => (GitErrorCode.DirtyWorkingTree, Loc.T("You have uncommitted changes.", "Commit edilmemiş değişiklikleriniz var."),
                Loc.T("This operation needs a clean working directory.", "Bu işlem temiz bir çalışma dizini gerektirir."),
                Loc.T("Commit your changes or put them aside (stash) first. Use 'Autostash' to do this automatically.", "Önce değişikliklerinizi commit edin veya kenara koyun (stash). Bunu otomatik yapmak için 'Autostash' kullanın."))),

        new(t => Find(t, @"(?:a branch named|tag) '(?<name>[^']+)' already exists|already exists"),
            m => (GitErrorCode.AlreadyExists, Loc.T("That name is already taken.", "Bu ad zaten kullanılıyor."),
                m.Groups["name"].Success ? Loc.T($"'{m.Groups["name"].Value}' already exists.", $"'{m.Groups["name"].Value}' zaten var.") : Loc.T("An item with this name already exists.", "Bu adda bir öğe zaten var."),
                Loc.T("Choose a different name.", "Başka bir ad seçin."))),

        new(t => Find(t, @"is not a valid branch name|not a valid (?:tag|ref) name|bad revision|invalid reference"),
            _ => (GitErrorCode.InvalidName, Loc.T("That name is not valid.", "Bu ad geçerli değil."),
                Loc.T("Git names cannot contain spaces, '..', '~', '^', ':' or end with '.lock'.", "Git adları boşluk, '..', '~', '^', ':' içeremez ve '.lock' ile bitemez."),
                null)),

        new(t => Find(t, @"did not match any file\(s\) known to git|unknown revision|not a valid object name|couldn't find remote ref|invalid upstream|Needed a single revision"),
            _ => (GitErrorCode.ReferenceNotFound, Loc.T("Git could not find that commit, branch, or file.", "Git bu commit'i, branch'i veya dosyayı bulamadı."),
                Loc.T("The name may be misspelled, or it may have been deleted.", "Ad yanlış yazılmış ya da silinmiş olabilir."),
                Loc.T("Look for it in the Recovery Center — deleted branches can be restored there.", "Kurtarma Merkezi'nde arayın; silinen branch'ler oradan geri getirilebilir."))),

        new(t => Find(t, @"It seems that there is already a rebase-merge directory|You have not concluded your merge|cherry-pick is already in progress|revert is already in progress|MERGE_HEAD exists"),
            _ => (GitErrorCode.OperationInProgress, Loc.T("Another operation is still in progress.", "Başka bir işlem hâlâ devam ediyor."),
                Loc.T("A merge, rebase, cherry-pick or revert was started and not finished.", "Bir merge, rebase, cherry-pick veya revert başlatıldı ve tamamlanmadı."),
                Loc.T("Continue or abort it first.", "Önce devam ettirin veya iptal edin."))),

        new(t => Find(t, @"There is no merge to abort|No rebase in progress|no cherry-pick or revert in progress"),
            _ => (GitErrorCode.NoOperationInProgress, Loc.T("There is nothing to continue or abort.", "Devam ettirilecek veya iptal edilecek bir işlem yok."),
                Loc.T("No merge, rebase, cherry-pick or revert is in progress.", "Devam eden bir merge, rebase, cherry-pick veya revert yok."),
                null)),

        new(t => Find(t, @"is not a stash-like commit|No stash entries found|stash@\{\d+\} is not a valid reference"),
            _ => (GitErrorCode.StashNotFound, Loc.T("That stash no longer exists.", "Bu stash artık yok."),
                Loc.T("The stash entry could not be found.", "Stash kaydı bulunamadı."),
                Loc.T("Deleted stashes can often be recovered in the Recovery Center.", "Silinen stash'ler çoğu zaman Kurtarma Merkezi'nden geri getirilebilir."))),
    ];

    public static GitError Translate(GitCommandResult result)
    {
        var raw = SecretMasker.Mask(result.CombinedOutput.Trim());
        var commandLine = result.Command.DisplayText;

        if (result.ExitCode == -2)
            return new GitError(GitErrorCode.TimedOut, Loc.T("The operation took too long.", "İşlem çok uzun sürdü."), Loc.T("Git did not finish in time and was stopped.", "Git zamanında bitmediği için durduruldu."), Loc.T("Try again, or run the command in the terminal to see what is happening.", "Tekrar deneyin ya da neler olduğunu görmek için komutu terminalde çalıştırın."), commandLine, result.ExitCode, raw);
        if (result.ExitCode == -3)
            return new GitError(GitErrorCode.Cancelled, Loc.T("The operation was cancelled.", "İşlem iptal edildi."), Loc.T("Git was stopped before it finished.", "Git bitmeden durduruldu."), null, commandLine, result.ExitCode, raw);
        if (result.ExitCode == -1)
            return new GitError(GitErrorCode.GitNotFound, Loc.T("Git could not be started.", "Git başlatılamadı."), Loc.T("ArChrono could not run the git executable.", "ArChrono git programını çalıştıramadı."), Loc.T("Install Git or set its location in Settings → Git.", "Git'i kurun veya konumunu Ayarlar → Git bölümünden belirtin."), commandLine, result.ExitCode, raw);

        return Translate(raw, commandLine, result.ExitCode);
    }

    public static GitError Translate(string rawOutput, string commandLine, int exitCode)
    {
        foreach (var rule in Rules)
        {
            if (rule.Matcher(rawOutput) is not { } match) continue;
            var (code, title, explanation, suggestion) = rule.Build(match);
            return new GitError(code, title, explanation, suggestion, commandLine, exitCode, rawOutput);
        }

        var firstLine = rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        firstLine = FatalPrefix().Replace(firstLine, string.Empty);
        return new GitError(GitErrorCode.Unknown, Loc.T("Git reported a problem.", "Git bir sorun bildirdi."),
            string.IsNullOrEmpty(firstLine) ? Loc.T("The command did not complete successfully.", "Komut başarıyla tamamlanamadı.") : Capitalize(firstLine),
            null, commandLine, exitCode, rawOutput);
    }

    [GeneratedRegex(@"^(?:fatal|error):\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FatalPrefix();

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
