using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using ExecPlan.Api.Auth;
using ExecPlan.Api.Hubs;
using ExecPlan.Api.Middleware;
using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure;
using ExecPlan.Infrastructure.Seed;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

// Replace the Infrastructure no-op IRealtimeNotifier with the SignalR-backed one (Api layer owns the
// hub coupling; Application stays SignalR-free). Must run AFTER AddInfrastructure/AddApplication so it
// wins the last-registration-wins resolution.
builder.Services.RemoveAll<IRealtimeNotifier>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

builder.Services.AddControllers();
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(o =>
        o.DataAnnotationLocalizerProvider = (_, f) => f.Create(typeof(ExecPlan.Api.Resources.SharedResource)));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSignalR();

// Arabic-first (CLAUDE.md convention 7): .NET's default HtmlEncoder is conservative and renders every
// non-Basic-Latin character (i.e. every Arabic string this whole admin area renders) as a numeric HTML
// character reference (e.g. "الإجمالي" -> "&#x627;&#x644;..."). That is valid, correctly-rendering HTML,
// but needlessly bloats every Arabic-first page and defeats anything that inspects the raw response body
// for literal Arabic text (e.g. an integration test). Allowing the full Unicode range keeps the
// mandatory HTML-metacharacter escaping (<>&"') while stopping the extra non-ASCII escaping.
builder.Services.AddWebEncoders(options =>
{
    options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
});

// Arabic-first localization (CLAUDE.md convention 7): supported ar/en, default ar (RTL), resolved
// cookie → query → accept-language.
builder.Services.AddLocalization();
var supportedCultures = new[] { new CultureInfo("ar"), new CultureInfo("en") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("ar");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new QueryStringRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});

var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // JwtTokenFactory (Infrastructure) writes "sub"/"name" as their short JWT names and the role
        // claim as the long-form ClaimTypes.Role URI. MapInboundClaims=false stops the handler from
        // remapping "sub"->ClaimTypes.NameIdentifier/"name"->ClaimTypes.Name, so CurrentUser (which
        // reads JwtRegisteredClaimNames.Sub directly) and RoleClaimType below see exactly what was
        // issued.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = JwtRegisteredClaimNames.Name,
        };

        // SignalR's WebSocket/SSE handshake cannot send an Authorization header, so the hub client
        // carries the JWT on the access_token query string instead. Lift it onto ctx.Token for any
        // request under /hubs so the bearer handler validates it normally.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    })
    .AddCookie(AuthPolicies.AdminCookieScheme, options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Admin, p => p.RequireRole(UserRole.SystemAdmin.ToString()));
    options.AddPolicy(AuthPolicies.Manager, p => p.RequireRole(UserRole.PlanManager.ToString()));
    options.AddPolicy(AuthPolicies.Leader, p => p.RequireRole(UserRole.TeamLeader.ToString()));
    options.AddPolicy(AuthPolicies.Member, p => p.RequireRole(UserRole.TeamMember.ToString()));
    options.AddPolicy(AuthPolicies.ManagerOrAdmin, p => p.RequireRole(
        UserRole.PlanManager.ToString(), UserRole.SystemAdmin.ToString()));
});

var app = builder.Build();

// Dev/eval-only startup seed (Task 21): a fresh checkout gets one known login per role + a showcase
// storm-response plan ready to activate. DataSeeder.SeedAsync is itself idempotent (no-ops once any
// User row exists), so this is safe to run on every Development start; it never runs in Production
// unless explicitly opted into via Seed:Enabled (e.g. a hosted eval/demo environment).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Seed:Enabled"))
{
    await DataSeeder.SeedAsync(app.Services);
}

// The app runs behind Cloudflare (TLS terminates at the edge; the origin sees plain HTTP). Restore the
// real client scheme/IP from the X-Forwarded-* headers BEFORE anything reads Request.Scheme — otherwise
// the app thinks it's on HTTP and emits http:// redirect Locations, drops the Secure flag on the
// antiforgery cookie, and would generate http:// absolute URLs (reset/activation links, etc.).
// Must be the first middleware so every downstream stage sees the corrected values.
// SECURITY: KnownNetworks/KnownProxies are cleared, so these headers are trusted from ANY upstream. That
// is only safe because the origin accepts traffic solely from Cloudflare — keep the server firewalled to
// Cloudflare's IP ranges (or add them to KnownProxies) so a direct-to-origin request can't spoof them.
var forwardedHeaderOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeaderOptions.KnownNetworks.Clear();
forwardedHeaderOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaderOptions);

// Consistent AppException → HTTP mapping for the whole pipeline. Registered before auth so it also
// wraps the authentication/authorization middleware and any controller-level throws.
app.UseMiddleware<AppExceptionMiddleware>();

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Resolve the request culture (cookie → query → accept-language) before the endpoints run.
app.UseRequestLocalization();

app.MapControllers();
app.MapControllerRoute("adminArea", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapGet("/", ctx => { ctx.Response.Redirect("/admin"); return Task.CompletedTask; });
app.MapHub<DashboardHub>("/hubs/dashboard");

await app.RunAsync();

public partial class Program { }
