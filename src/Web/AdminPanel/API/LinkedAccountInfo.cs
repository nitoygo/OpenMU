// <copyright file="LinkedAccountInfo.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

/// <summary>
/// The account which is linked to a user of an external service.
/// </summary>
/// <param name="LoginName">The login name of the account.</param>
/// <param name="State">The state of the account, e.g. <c>Normal</c> or <c>Banned</c>.</param>
public sealed record LinkedAccountInfo(string LoginName, string State);
