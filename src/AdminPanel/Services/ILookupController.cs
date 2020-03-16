// <copyright file="ILookupController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AdminPanel.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>
    /// Interface for a lookup controller which provides methods to find objects by some text and type.
    /// </summary>
    public interface ILookupController
    {
        /// <summary>
        /// Looks up objects with a specific text and type.
        /// </summary>
        /// <param name="text">The search text.</param>
        /// <typeparam name="T">The type of the searched object.</typeparam>
        /// <returns>All objects which meet the criteria.</returns>
        Task<IEnumerable<T>> GetSuggestionsAsync<T>(string text)
            where T : class;
    }
}