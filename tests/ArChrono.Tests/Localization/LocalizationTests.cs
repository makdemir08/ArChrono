using System.Globalization;
using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.App.Infrastructure;
using ArChrono.App.Localization;
using ArChrono.Application.Settings;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Localization;
using ArChrono.Recovery.Operations;

namespace ArChrono.Tests.Localization;

/// <summary>Arayüz dili süreç genelinde tek değer olduğundan bu testler diğerleriyle paralel çalışmaz.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageCollection
{
    public const string Name = "Interface language";
}

[Collection(LanguageCollection.Name)]
public sealed class LocalizationTests
{
    [Fact]
    public void English_is_the_default_so_other_tests_stay_deterministic() =>
        Assert.Equal(AppLanguage.English, Loc.Language);

    [Theory]
    [InlineData("tr-TR", AppLanguage.Turkish)]
    [InlineData("tr", AppLanguage.Turkish)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("de-DE", AppLanguage.English)]
    public void System_language_falls_back_to_english(string culture, AppLanguage expected) =>
        Assert.Equal(expected, Loc.DetectSystemLanguage(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Git_errors_follow_the_interface_language()
    {
        Assert.Equal("This folder is not a Git repository.", GitErrorTranslator.Translate("fatal: not a git repository", "git status", 128).Title);
        using (UseLanguage(AppLanguage.Turkish))
            Assert.Equal("Bu klasör bir Git deposu değil.", GitErrorTranslator.Translate("fatal: not a git repository", "git status", 128).Title);
    }

    [Fact]
    public void Undo_titles_recognize_prefixes_from_both_languages()
    {
        Assert.Equal("Commit \"Fix\"", OperationTitles.StripUndoPrefix("Undo Commit \"Fix\""));
        Assert.Equal("main branch'ini sil", OperationTitles.StripUndoPrefix("Geri al: main branch'ini sil"));
        Assert.Equal("Redo Delete branch main", OperationTitles.Redo("Undo Delete branch main"));

        using (UseLanguage(AppLanguage.Turkish))
        {
            Assert.Equal("Geri al: Commit \"Fix\"", OperationTitles.Undo("Commit \"Fix\""));
            // Kayıtlı başlık İngilizce oluşturulmuş olsa da Türkçe arayüzde doğru yinelenir.
            Assert.Equal("Yinele: Delete branch main", OperationTitles.Redo("Undo Delete branch main"));
        }
    }

    [Fact]
    public void Formatting_uses_the_display_culture_without_changing_the_process_culture()
    {
        var processCulture = CultureInfo.CurrentCulture;
        Assert.Equal("1.5 KB", Format.Bytes(1536));
        Assert.Equal("Today", Format.DayHeader(DateTimeOffset.Now));

        using (UseLanguage(AppLanguage.Turkish))
        {
            Assert.Equal("1,5 KB", Format.Bytes(1536));
            Assert.Equal("Bugün", Format.DayHeader(DateTimeOffset.Now));
            Assert.Equal("az önce", Format.Relative(DateTimeOffset.Now));
        }

        Assert.Same(processCulture, CultureInfo.CurrentCulture);
    }

    [Fact]
    public void Xaml_extension_and_option_labels_are_localized()
    {
        var extension = new LocExtension("Open a repository", "Depo aç");
        Assert.Equal("Open a repository", extension.ProvideValue(null!));
        Assert.Equal("Night (dark)", Labels.Of(ThemePreference.Dark));

        using (UseLanguage(AppLanguage.Turkish))
        {
            Assert.Equal("Depo aç", extension.ProvideValue(null!));
            Assert.Equal("Gece (koyu)", Labels.Of(ThemePreference.Dark));
            Assert.Equal("Türkçe", Labels.Of(LanguagePreference.Turkish));
        }
    }

    [Fact]
    public void Ai_explanations_are_requested_in_the_interface_language()
    {
        var commit = new CommitInfo(new string('a', 40), [], "Ada", "ada@x", DateTimeOffset.Now, "Ada", "ada@x", DateTimeOffset.Now, "Add refresh token");
        var details = new CommitDetails(commit, "Body", [new CommitFileChange("src/TokenService.cs", null, ChangeKind.Modified, 10, 2, false)]);
        const string patch = "diff --git a/src/TokenService.cs b/src/TokenService.cs\n+x\n";
        var builder = new AiContextBuilder(new SensitivePathFilter());

        Assert.DoesNotContain("Turkish", builder.BuildExplainCommit(details, patch).Prompt.System);
        using (UseLanguage(AppLanguage.Turkish))
            Assert.Contains("Write the whole answer in Turkish", builder.BuildExplainCommit(details, patch).Prompt.System);
    }

    [Fact]
    public void Language_preference_defaults_to_system() =>
        Assert.Equal(LanguagePreference.System, new AppSettings().Language);

    private static LanguageScope UseLanguage(AppLanguage language) => new(language);

    private sealed class LanguageScope : IDisposable
    {
        private readonly AppLanguage _previous = Loc.Language;

        public LanguageScope(AppLanguage language) => Loc.SetLanguage(language);

        public void Dispose() => Loc.SetLanguage(_previous);
    }
}
