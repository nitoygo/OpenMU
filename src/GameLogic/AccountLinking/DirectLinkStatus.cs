// <copyright file="DirectLinkStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.AccountLinking;

/// <summary>
/// The result of linking a user of an external service directly, without a code from the game.
/// </summary>
public enum DirectLinkStatus
{
    /// <summary>
    /// The user is linked to the account now, or already was.
    /// </summary>
    Linked,

    /// <summary>
    /// The account doesn't exist.
    /// </summary>
    AccountNotFound,

    /// <summary>
    /// The user is already linked to another account.
    /// </summary>
    UserLinkedToOtherAccount,

    /// <summary>
    /// The account is already linked to another user of the service.
    /// </summary>
    AccountLinkedToOtherUser,

    /// <summary>
    /// An account with the login name already exists.
    /// </summary>
    LoginNameTaken,
}
