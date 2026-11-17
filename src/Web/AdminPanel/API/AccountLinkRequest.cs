// <copyright file="AccountLinkRequest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// A request to link an account to a user of an external service, whom the caller verified.
/// </summary>
public class AccountLinkRequest
{
    /// <summary>
    /// The maximum length of the identifier and the name of a user.
    /// </summary>
    internal const int UserMaximumLength = 100;

    /// <summary>
    /// Gets or sets the identifier of the user in the external service.
    /// </summary>
    [Required]
    [MaxLength(UserMaximumLength)]
    public string ExternalUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the user in the external service.
    /// </summary>
    [Required]
    [MaxLength(UserMaximumLength)]
    public string ExternalUserName { get; set; } = string.Empty;
}
