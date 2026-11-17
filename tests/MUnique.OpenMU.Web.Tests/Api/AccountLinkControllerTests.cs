// <copyright file="AccountLinkControllerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Api;

using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.AccountLinking;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Web.AdminPanel.API;
using MUnique.OpenMU.Web.AdminPanel.Auth;
using MUnique.OpenMU.Web.AdminPanel.Services;
using MUnique.OpenMU.Web.Tests.AdminAuth;

/// <summary>
/// Tests for the <see cref="AccountLinkController"/>, which manages the links of accounts to external services through the public API.
/// </summary>
[TestFixture]
public class AccountLinkControllerTests
{
    private const string Discord = AccountLinkService.DiscordProvider;

    private InMemoryPersistenceContextProvider _persistenceContextProvider = null!;

    private AccountLinkController _controller = null!;

    /// <summary>
    /// Sets up an in-memory database with two accounts, and the controller.
    /// </summary>
    /// <returns>The task.</returns>
    [SetUp]
    public async Task SetUpAsync()
    {
        this._persistenceContextProvider = new InMemoryPersistenceContextProvider();
        this._controller = new AccountLinkController(this._persistenceContextProvider, Mock.Of<ILogger<AccountLinkController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        using var context = this.CreateContext();
        foreach (var name in new[] { "player", "other" })
        {
            var account = context.CreateNew<Account>();
            account.LoginName = name;
            account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(name, 4);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A linked user resolves to the login name and state of the account; an unlinked one is not found.
    /// </summary>
    [Test]
    public async Task LinkedUserResolvesToAccountAsync()
    {
        Assert.That(await this._controller.GetAccountOfUserAsync(Discord, "123", CancellationToken.None).ConfigureAwait(false), Is.InstanceOf<NotFoundObjectResult>());

        await this.LinkAsync("player", "123").ConfigureAwait(false);

        var account = (LinkedAccountInfo)((OkObjectResult)await this._controller.GetAccountOfUserAsync(Discord, "123", CancellationToken.None).ConfigureAwait(false)).Value!;
        Assert.That(account, Is.EqualTo(new LinkedAccountInfo("player", nameof(AccountState.Normal))));
    }

    /// <summary>
    /// Linking shows up in the links of the account, and linking the same user again changes nothing.
    /// </summary>
    [Test]
    public async Task LinkIsListedAndRepeatableAsync()
    {
        Assert.That(await this.LinkAsync("player", "123").ConfigureAwait(false), Is.InstanceOf<OkObjectResult>());
        Assert.That(await this.LinkAsync("player", "123").ConfigureAwait(false), Is.InstanceOf<OkObjectResult>());

        var links = (IReadOnlyList<AccountLinkInfo>)((OkObjectResult)await this._controller.GetLinksAsync("player", CancellationToken.None).ConfigureAwait(false)).Value!;
        Assert.That(links.Select(link => (link.Provider, link.ExternalUserId, link.ExternalUserName)), Is.EqualTo(new[] { (Discord, "123", "user-123") }));
        Assert.That(links[0].LinkedAt, Is.Not.Null);
    }

    /// <summary>
    /// A user who is linked to another account is refused, not moved.
    /// </summary>
    [Test]
    public async Task UserLinkedToOtherAccountIsRefusedAsync()
    {
        await this.LinkAsync("other", "123").ConfigureAwait(false);

        Assert.That(GetError(await this.LinkAsync("player", "123").ConfigureAwait(false)), Is.EqualTo("user-linked"));
        Assert.That(await this.GetLoginNameOfUserAsync("123").ConfigureAwait(false), Is.EqualTo("other"));
    }

    /// <summary>
    /// An account which is linked to another user of the service is refused, the link isn't replaced.
    /// </summary>
    [Test]
    public async Task AccountLinkedToOtherUserIsRefusedAsync()
    {
        await this.LinkAsync("player", "123").ConfigureAwait(false);

        Assert.That(GetError(await this.LinkAsync("player", "456").ConfigureAwait(false)), Is.EqualTo("account-linked"));
        Assert.That(await this.GetLoginNameOfUserAsync("123").ConfigureAwait(false), Is.EqualTo("player"));
    }

    /// <summary>
    /// A pending code from the game is replaced by the direct link, so it can't link another user later.
    /// </summary>
    [Test]
    public async Task PendingCodeIsConsumedByDirectLinkAsync()
    {
        var service = new AccountLinkService(this.CreateContext);
        var code = await service.CreateCodeAsync(await this.GetAccountIdAsync("player").ConfigureAwait(false), Discord, null).ConfigureAwait(false);

        Assert.That(await this.LinkAsync("player", "123").ConfigureAwait(false), Is.InstanceOf<OkObjectResult>());

        Assert.That(await service.LinkAsync(Discord, code, "456", "user-456").ConfigureAwait(false), Is.Null);
        Assert.That(await this.GetLoginNameOfUserAsync("123").ConfigureAwait(false), Is.EqualTo("player"));
    }

    /// <summary>
    /// An unknown account is answered with not found.
    /// </summary>
    [Test]
    public async Task UnknownAccountIsNotFoundAsync()
    {
        Assert.That(GetError(await this.LinkAsync("unknown", "123").ConfigureAwait(false)), Is.EqualTo("account-not-found"));
        Assert.That(await this._controller.GetLinksAsync("unknown", CancellationToken.None).ConfigureAwait(false), Is.InstanceOf<NotFoundObjectResult>());
        Assert.That(await this._controller.UnlinkAsync("unknown", Discord, CancellationToken.None).ConfigureAwait(false), Is.InstanceOf<NotFoundObjectResult>());
    }

    /// <summary>
    /// A provider name which isn't a plain lower case word is refused.
    /// </summary>
    [Test]
    public async Task InvalidProviderIsRefusedAsync()
    {
        var result = await this._controller.LinkAsync("player", "Discord!", new AccountLinkRequest { ExternalUserId = "123", ExternalUserName = "x" }, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    /// <summary>
    /// Unlinking removes the link once.
    /// </summary>
    [Test]
    public async Task UnlinkRemovesLinkAsync()
    {
        await this.LinkAsync("player", "123").ConfigureAwait(false);

        Assert.That(await this._controller.UnlinkAsync("player", Discord, CancellationToken.None).ConfigureAwait(false), Is.InstanceOf<NoContentResult>());
        Assert.That(GetError(await this._controller.UnlinkAsync("player", Discord, CancellationToken.None).ConfigureAwait(false)), Is.EqualTo("not-linked"));
        Assert.That(await this.GetLoginNameOfUserAsync("123").ConfigureAwait(false), Is.Null);
    }

    /// <summary>
    /// A created account is linked to the user and has no usable password.
    /// </summary>
    [Test]
    public async Task CreatedAccountIsLinkedWithoutPasswordAsync()
    {
        var result = await this.CreateAccountAsync("newbie", "123").ConfigureAwait(false);

        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(StatusCodes.Status201Created));
        Assert.That(await this.GetLoginNameOfUserAsync("123").ConfigureAwait(false), Is.EqualTo("newbie"));
        using var context = this.CreateContext();
        var account = await context.GetAccountByLoginNameAsync("newbie").ConfigureAwait(false);
        Assert.That(account!.PasswordHash, Is.EqualTo(AccountLinkService.NoPasswordHash));
        Assert.That(account.State, Is.EqualTo(AccountState.Normal));
    }

    /// <summary>
    /// No password matches the hash of an account without password, and checking it doesn't fail.
    /// </summary>
    /// <param name="password">The password.</param>
    [TestCase("")]
    [TestCase("newbie")]
    [TestCase("0000000000")]
    public void NoPasswordHashMatchesNothing(string password)
    {
        Assert.That(BCrypt.Net.BCrypt.Verify(password, AccountLinkService.NoPasswordHash), Is.False);
    }

    /// <summary>
    /// Creating an account is refused for a taken login name and for an already linked user, and then creates nothing.
    /// </summary>
    [Test]
    public async Task CreateAccountConflictsAsync()
    {
        await this.LinkAsync("other", "123").ConfigureAwait(false);

        Assert.That(GetError(await this.CreateAccountAsync("player", "456").ConfigureAwait(false)), Is.EqualTo("login-name-taken"));
        Assert.That(GetError(await this.CreateAccountAsync("newbie", "123").ConfigureAwait(false)), Is.EqualTo("user-linked"));
        Assert.That(await this.GetLoginNameOfUserAsync("456").ConfigureAwait(false), Is.Null);
        using var context = this.CreateContext();
        Assert.That(await context.GetAccountIdByLoginNameAsync("newbie").ConfigureAwait(false), Is.Null);
    }

    /// <summary>
    /// The endpoints require their dedicated policy.
    /// </summary>
    [Test]
    public void EndpointsRequireAccountLinkPolicy()
    {
        var policies = typeof(AccountLinkController).GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy);

        Assert.That(policies, Is.EqualTo(new[] { AdminPolicies.AuthenticatedAccountLink }));
        Assert.That(typeof(AccountLinkController).GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>()), Is.Empty);
    }

    /// <summary>
    /// Only an API key with the account link role fulfills the policy of the endpoints.
    /// </summary>
    /// <param name="role">The role of the API key.</param>
    /// <returns><c>true</c>, if the key is authorized.</returns>
    [TestCase(nameof(ApiKeyRole.Viewer), ExpectedResult = false)]
    [TestCase(nameof(ApiKeyRole.Operator), ExpectedResult = false)]
    [TestCase(nameof(ApiKeyRole.Administrator), ExpectedResult = false)]
    [TestCase(nameof(ApiKeyRole.CashShop), ExpectedResult = false)]
    [TestCase(nameof(ApiKeyRole.LoginTicket), ExpectedResult = false)]
    [TestCase(nameof(ApiKeyRole.AccountLink), ExpectedResult = true)]
    public async Task<bool> OnlyAccountLinkKeyIsAuthorizedAsync(string role)
    {
        var keys = new InMemoryApiKeyRepository();
        var key = ApiKeyGenerator.GenerateKey();
        await keys.AddAsync(new ApiKey { Id = Guid.NewGuid(), Name = "site", KeyHash = ApiKeyGenerator.Hash(key), Roles = role }).ConfigureAwait(false);
        var client = await new ApiKeyRegistry(keys).FindAsync(key).ConfigureAwait(false);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(client!.CreateClaims(), ApiKeyAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AdminPanel:Auth:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "openmu-account-link-test-keys") })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminPanelAuth(configuration);
        await using var serviceProvider = services.BuildServiceProvider();
        var authorization = serviceProvider.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(principal, AdminPolicies.AuthenticatedAccountLink).ConfigureAwait(false)).Succeeded;
    }

    /// <summary>
    /// The account link role doesn't imply any other role, and no other role implies it.
    /// </summary>
    [Test]
    public void AccountLinkRoleStandsAlone()
    {
        Assert.That(AdminRoles.GetEffectiveRoles(AdminRoles.AccountLink), Is.EqualTo(new[] { AdminRoles.AccountLink }));
        Assert.That(AdminRoles.All.Append(AdminRoles.CashShop).Append(AdminRoles.LoginTicket).SelectMany(AdminRoles.GetEffectiveRoles), Does.Not.Contain(AdminRoles.AccountLink));
    }

    private static string? GetError(IActionResult result)
    {
        var value = ((ObjectResult)result).Value!;
        return value.GetType().GetProperty("error")!.GetValue(value) as string;
    }

    private Task<IActionResult> LinkAsync(string loginName, string userId)
        => this._controller.LinkAsync(loginName, Discord, new AccountLinkRequest { ExternalUserId = userId, ExternalUserName = "user-" + userId }, CancellationToken.None);

    private Task<IActionResult> CreateAccountAsync(string loginName, string userId)
        => this._controller.CreateAccountAsync(Discord, userId, new LinkedAccountRequest { LoginName = loginName, ExternalUserName = "user-" + userId }, CancellationToken.None);

    private async Task<string?> GetLoginNameOfUserAsync(string userId)
        => (await this._controller.GetAccountOfUserAsync(Discord, userId, CancellationToken.None).ConfigureAwait(false) as OkObjectResult)?.Value is LinkedAccountInfo info ? info.LoginName : null;

    private async Task<Guid> GetAccountIdAsync(string loginName)
    {
        using var context = this.CreateContext();
        return (await context.GetAccountIdByLoginNameAsync(loginName).ConfigureAwait(false))!.Value;
    }

    private MUnique.OpenMU.Persistence.IPlayerContext CreateContext() => this._persistenceContextProvider.CreateNewPlayerContext(new GameConfiguration());
}
