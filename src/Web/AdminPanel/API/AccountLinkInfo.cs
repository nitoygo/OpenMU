// <copyright file="AccountLinkInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

/// <summary>
/// A confirmed link of an account to a user of an external service.
/// </summary>
/// <param name="Provider">The name of the external service, e.g. <c>discord</c>.</param>
/// <param name="ExternalUserId">The identifier of the user in the external service.</param>
/// <param name="ExternalUserName">The name of the user at the time of linking.</param>
/// <param name="LinkedAt">The timestamp at which the user was linked.</param>
public sealed record AccountLinkInfo(string Provider, string ExternalUserId, string? ExternalUserName, DateTime? LinkedAt);

