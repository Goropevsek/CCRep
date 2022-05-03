﻿namespace Microsoft.Teams.Apps.CompanyCommunicator.Common.Services.Analytics
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface IAnalyticsService
    {
        Task<int> GetUniqueViewsCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetTotalViewsCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetUniqueClicksCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetAcknowledgementsCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetReactionsCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<KustoQueryResult> GetPollVoteResultByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetFullyCorrectQuizAnswersCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));

        Task<int> GetUniquePollVotesCountByNotificationIdAsync(string notificationId, CancellationToken cancellationToken = default(CancellationToken));
    }
}
