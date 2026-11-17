// <copyright file="LinkedAccountRequest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.API;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// A request to create an account for a user of an external service, whom the caller verified.
/// </summary>
public class LinkedAccountRequest
{
    /// <summary>
    /// Gets or sets the login name of the new account, with the same rules as in <see cref="AccountRegistration"/>.
    /// </summary>
    [Required]
    [MinLength(AccountRegistration.MinimumLength)]
    [MaxLength(AccountRegistration.LoginNameMaximumLength)]
    [RegularExpression(@"^[\x21-\x7E]+$")]
    public string LoginName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the user in the external service.
    /// </summary>
    [Required]
    [MaxLength(AccountLinkRequest.UserMaximumLength)]
    public string ExternalUserName { get; set; } = string.Empty;
}
