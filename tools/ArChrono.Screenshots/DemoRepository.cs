using System.Diagnostics;

namespace ArChrono.Screenshots;

/// <summary>Ekran görüntüleri için gerçekçi geçmişi olan küçük bir repository.</summary>
internal static class DemoRepository
{
    private static int _minute;

    public const string TokenServiceV3 = """
        namespace Acme.Portal.Auth;

        public sealed class TokenService(ITokenStore store, IClock clock)
        {
            private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
            private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

            public async Task<TokenPair> IssueAsync(User user)
            {
                var access = Token.Create(user, clock.Now + AccessTokenLifetime);
                var refresh = RefreshToken.Create(user, clock.Now + RefreshTokenLifetime);
                await store.SaveAsync(refresh);
                return new TokenPair(access, refresh);
            }

            public async Task<TokenPair?> RefreshAsync(string refreshToken)
            {
                var stored = await store.FindAsync(refreshToken);
                if (stored is null || stored.ExpiresAt < clock.Now) return null;
                await store.RevokeAsync(stored);
                return await IssueAsync(stored.User);
            }
        }
        """;

    public const string LoginPageV2 = """
        @page "/login"
        <h1>Sign in</h1>
        <EditForm Model="model" OnValidSubmit="SignInAsync">
            <DataAnnotationsValidator />
            <InputText @bind-Value="model.Email" placeholder="Email" />
            <InputText @bind-Value="model.Password" type="password" />
            <label><InputCheckbox @bind-Value="model.RememberMe" /> Keep me signed in</label>
            <button type="submit" disabled="@busy">Sign in</button>
        </EditForm>
        """;

    public static void Create(string root)
    {
        Git(root, "init", "--initial-branch=main");
        Git(root, "config", "user.name", "Ada Yılmaz");
        Git(root, "config", "user.email", "ada@acme.dev");
        Git(root, "config", "commit.gpgsign", "false");

        Write(root, "README.md", "# Acme Portal\n\nCustomer portal.\n");
        Write(root, ".gitignore", "bin/\nobj/\n.env\n");
        Write(root, "src/Web/Program.cs", "var app = WebApplication.Create(args);\napp.Run();\n");
        Commit(root, "Initial project structure", "Ada Yılmaz");

        Write(root, "src/Auth/AuthService.cs", "public class AuthService { }\n");
        Commit(root, "Add authentication service skeleton", "Can Demir");
        Git(root, "tag", "v0.1.0");

        Git(root, "checkout", "-q", "-b", "develop");
        Write(root, "src/Web/LoginPage.razor", "@page \"/login\"\n<h1>Sign in</h1>\n");
        Commit(root, "Add login page", "Can Demir");

        Git(root, "checkout", "-q", "-b", "feature/login");
        Write(root, "src/Web/LoginPage.razor", "@page \"/login\"\n<h1>Sign in</h1>\n<EditForm Model=\"model\" />\n");
        Commit(root, "Add login form validation", "Ada Yılmaz");
        Write(root, "src/Auth/TokenService.cs", "public class TokenService { }\n");
        Commit(root, "Introduce token service", "Ada Yılmaz");
        Write(root, "src/Auth/TokenService.cs", TokenServiceV3.Replace("RefreshAsync", "RenewAsync"));
        Commit(root, "Add refresh token support", "Ada Yılmaz");

        Git(root, "checkout", "-q", "develop");
        Write(root, "src/Billing/InvoiceService.cs", "public class InvoiceService { }\n");
        Commit(root, "Add invoice service", "Mehmet Kaya");
        Merge(root, "feature/login", "Merge branch 'feature/login' into develop", "Can Demir");

        Git(root, "checkout", "-q", "-b", "feature/payment");
        Write(root, "src/Billing/PaymentGateway.cs", "public class PaymentGateway { }\n");
        Commit(root, "Add payment gateway abstraction", "Mehmet Kaya");
        Write(root, "src/Billing/PaymentGateway.cs", "public class PaymentGateway { public Task ChargeAsync() => Task.CompletedTask; }\n");
        Commit(root, "Charge cards through the gateway", "Mehmet Kaya");

        Git(root, "checkout", "-q", "main");
        Write(root, "README.md", "# Acme Portal\n\nCustomer portal for Acme.\n\n## Getting started\n");
        Commit(root, "Improve README", "Zeynep Arslan");
        Merge(root, "develop", "Merge branch 'develop'", "Zeynep Arslan");
        Git(root, "tag", "-a", "v0.2.0", "-m", "Release 0.2.0");
        Write(root, "src/Web/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/health\", () => \"ok\");\napp.Run();\n");
        Commit(root, "Add health endpoint", "Ada Yılmaz");
        Write(root, "src/Auth/TokenService.cs", TokenServiceV3);
        Commit(root, "Fix refresh token expiration", "Ada Yılmaz");

        Git(root, "branch", "experiment/cache", "HEAD~1");
        Git(root, "checkout", "-q", "experiment/cache");
        Write(root, "src/Auth/TokenCache.cs", "public class TokenCache { }\n");
        Commit(root, "Spike: in-memory token cache", "Ada Yılmaz");
        Git(root, "checkout", "-q", "main");

        Write(root, "src/Web/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddAuthentication();\nvar app = builder.Build();\napp.MapGet(\"/health\", () => \"ok\");\napp.Run();\n");
        Git(root, "stash", "push", "-m", "wip: authentication middleware");
        Write(root, "docs/notes.md", "# Notes\n");
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Commit(string root, string message, string author)
    {
        Git(root, "add", "-A");
        GitAs(root, author, "commit", "-q", "-m", message);
    }

    private static void Merge(string root, string branch, string message, string author) =>
        GitAs(root, author, "merge", "--no-ff", "-q", "-m", message, branch);

    private static void GitAs(string root, string author, params string[] args)
    {
        var date = DateTimeOffset.Now.AddHours(-30).AddMinutes(_minute += 95).ToString("yyyy-MM-ddTHH:mm:sszzz");
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_AUTHOR_NAME"] = author;
        info.Environment["GIT_COMMITTER_NAME"] = author;
        info.Environment["GIT_AUTHOR_EMAIL"] = author.Split(' ')[0].ToLowerInvariant() + "@acme.dev";
        info.Environment["GIT_AUTHOR_DATE"] = date;
        info.Environment["GIT_COMMITTER_DATE"] = date;
        using var process = Process.Start(info)!;
        process.WaitForExit();
    }

    private static void Git(string root, params string[] args) => Program.Git(root, args);
}
