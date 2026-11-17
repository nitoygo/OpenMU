// <copyright file="AccountLinkController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.AccountLinking;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Web.AdminPanel.Auth;

/// <summary>
/// The public API to manage the links of accounts to users of external services, e.g. for a website
/// which signs the players in with Discord. The caller verified the user, e.g. with OAuth, and for
/// linking an existing account also its password.
/// </summary>
/// <remarks>
/// It follows the rules of <see cref="AccountLinkService"/>: a user is linked to one account, and an account
/// to one user per service. Unlike the code from the game, these calls never move or replace a link.
/// </remarks>
[ApiController]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationDefaults.ApiSchemes, Policy = AdminPolicies.AuthenticatedAccountLink)]
public partial class AccountLinkController : ControllerBase
{
    // Error codes in the bodies, so that a caller can tell them from a 404 of an older server without these routes.
    private const string NotLinked = "not-linked";
    private const string AccountNotFound = "account-not-found";

    private readonly IPersistenceContextProvider _persistenceContextProvider;
    private readonly AccountLinkService _linkService;
    private readonly ILogger<AccountLinkController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccountLinkController"/> class.
    /// </summary>
    /// <param name="persistenceContextProvider">The persistence context provider.</param>
    /// <param name="logger">The logger.</param>
    public AccountLinkController(IPersistenceContextProvider persistenceContextProvider, ILogger<AccountLinkController> logger)
    {
        this._persistenceContextProvider = persistenceContextProvider;
        this._linkService = new AccountLinkService(this.CreateContext);
        this._logger = logger;
    }

    /// <summary>
    /// Gets the account which is linked to a user of an external service.
    /// </summary>
    /// <param name="provider">The name of the external service, e.g. <c>discord</c>.</param>
    /// <param name="externalUserId">The identifier of the user in the external service.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The login name and state of the account; 404 with the error <c>not-linked</c>, if the user isn't linked.</returns>
    [HttpGet("api/account-links/{provider}/{externalUserId}")]
    public async Task<IActionResult> GetAccountOfUserAsync(string provider, string externalUserId, CancellationToken cancellationToken)
    {
        if (!IsValidProvider(provider))
        {
            return this.BadRequest();
        }

        using var context = this.CreateContext();
        if (await context.GetAccountExternalLinkByUserAsync(provider, externalUserId, cancellationToken).ConfigureAwait(false) is not { } link
            || await context.GetAccountLoginStateAsync(link.AccountId, cancellationToken).ConfigureAwait(false) is not { } account)
        {
            return NotFoundError(NotLinked);
        }

        return this.Ok(new LinkedAccountInfo(account.LoginName, account.State.ToString()));
    }

    /// <summary>
    /// Creates an account for a user of an external service and links them. The account has no usable password
    /// until one is set (<see cref="AccountLinkService.NoPasswordHash"/>).
    /// </summary>
    /// <param name="provider">The name of the external service, e.g. <c>discord</c>.</param>
    /// <param name="externalUserId">The identifier of the user in the external service.</param>
    /// <param name="request">The login name of the account and the name of the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// 201 with the link;
    /// 400, if the request is invalid;
    /// 409 with the error <c>user-linked</c> or <c>login-name-taken</c>, if the user is already linked or the login name is taken.
    /// </returns>
    [HttpPost("api/account-links/{provider}/{externalUserId}/account")]
    public async Task<IActionResult> CreateAccountAsync(string provider, string externalUserId, [FromBody] LinkedAccountRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidProvider(provider) || !IsValidUserId(externalUserId))
        {
            return this.BadRequest();
        }

        var status = await this._linkService.CreateLinkedAccountAsync(request.LoginName, provider, externalUserId, request.ExternalUserName, cancellationToken).ConfigureAwait(false);
        if (status != DirectLinkStatus.Linked)
        {
            return ToConflict(status);
        }

        this._logger.LogInformation("API client {Client} created account {LoginName}, linked to {Provider} user {UserId}.", this.User.Identity?.Name, request.LoginName, provider, externalUserId);
        return this.StatusCode(StatusCodes.Status201Created, new AccountLinkInfo(provider, externalUserId, request.ExternalUserName, DateTime.UtcNow));
    }

    /// <summary>
    /// Gets the confirmed links of an account.
    /// </summary>
    /// <param name="loginName">The login name of the account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The links; 404 with the error <c>account-not-found</c>, if the account doesn't exist.</returns>
    [HttpGet("api/accounts/{loginName}/links")]
    public async Task<IActionResult> GetLinksAsync(string loginName, CancellationToken cancellationToken)
    {
        using var context = this.CreateContext();
        if (await context.GetAccountIdByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false) is not { } accountId)
        {
            return NotFoundError(AccountNotFound);
        }

        var links = await context.GetAccountExternalLinksAsync(accountId, cancellationToken).ConfigureAwait(false);
        return this.Ok(links
            .Where(link => link.ExternalUserId is not null)
            .Select(link => new AccountLinkInfo(link.Provider, link.ExternalUserId!, link.ExternalUserName, link.LinkedAt))
            .ToList());
    }

    /// <summary>
    /// Links an account to a user of an external service. Linking the same user again changes nothing.
    /// </summary>
    /// <param name="loginName">The login name of the account.</param>
    /// <param name="provider">The name of the external service, e.g. <c>discord</c>.</param>
    /// <param name="request">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// 200 with the link;
    /// 400, if the request is invalid;
    /// 404 with the error <c>account-not-found</c>, if the account doesn't exist;
    /// 409 with the error <c>user-linked</c> or <c>account-linked</c>, if the user is linked to another account, or the account to another user of the service.
    /// </returns>
    [HttpPut("api/accounts/{loginName}/links/{provider}")]
    public async Task<IActionResult> LinkAsync(string loginName, string provider, [FromBody] AccountLinkRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidProvider(provider) || !IsValidUserId(request.ExternalUserId))
        {
            return this.BadRequest();
        }

        var status = await this._linkService.LinkDirectlyAsync(loginName, provider, request.ExternalUserId, request.ExternalUserName, cancellationToken).ConfigureAwait(false);
        switch (status)
        {
            case DirectLinkStatus.Linked:
                this._logger.LogInformation("API client {Client} linked account {LoginName} to {Provider} user {UserId}.", this.User.Identity?.Name, loginName, provider, request.ExternalUserId);
                return this.Ok(new AccountLinkInfo(provider, request.ExternalUserId, request.ExternalUserName, DateTime.UtcNow));
            case DirectLinkStatus.AccountNotFound:
                return NotFoundError(AccountNotFound);
            default:
                return ToConflict(status);
        }
    }

    /// <summary>
    /// Removes the link of an account to a service, including a pending code from the game.
    /// </summary>
    /// <param name="loginName">The login name of the account.</param>
    /// <param name="provider">The name of the external service, e.g. <c>discord</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>204; 404 with the error <c>account-not-found</c> or <c>not-linked</c>.</returns>
    [HttpDelete("api/accounts/{loginName}/links/{provider}")]
    public async Task<IActionResult> UnlinkAsync(string loginName, string provider, CancellationToken cancellationToken)
    {
        Guid? accountId;
        using (var context = this.CreateContext())
        {
            accountId = await context.GetAccountIdByLoginNameAsync(loginName, cancellationToken).ConfigureAwait(false);
        }

        // The event lets e.g. the Discord bot remove the roles of the user, as when unlinking in the game.
        var eventPublisher = this.HttpContext?.RequestServices?.GetService<IEventPublisher>();
        if (accountId is not { } id)
        {
            return NotFoundError(AccountNotFound);
        }

        if (await this._linkService.UnlinkAccountAsync(id, provider, eventPublisher).ConfigureAwait(false) is not { } userId)
        {
            return NotFoundError(NotLinked);
        }

        this._logger.LogInformation("API client {Client} unlinked account {LoginName} from {Provider} user {UserId}.", this.User.Identity?.Name, loginName, provider, userId);
        return this.NoContent();
    }

    private static bool IsValidProvider(string provider) => ProviderPattern().IsMatch(provider);

    private static bool IsValidUserId(string externalUserId) => externalUserId.Length is > 0 and <= AccountLinkRequest.UserMaximumLength;

    private static NotFoundObjectResult NotFoundError(string error) => new(new { error });

    private static ConflictObjectResult ToConflict(DirectLinkStatus status)
    {
        var error = status switch
        {
            DirectLinkStatus.UserLinkedToOtherAccount => "user-linked",
            DirectLinkStatus.AccountLinkedToOtherUser => "account-linked",
            DirectLinkStatus.LoginNameTaken => "login-name-taken",
            _ => status.ToString(),
        };
        return new ConflictObjectResult(new { error });
    }

    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex ProviderPattern();

    private IPlayerContext CreateContext() => this._persistenceContextProvider.CreateNewPlayerContext(new GameConfiguration());
}
