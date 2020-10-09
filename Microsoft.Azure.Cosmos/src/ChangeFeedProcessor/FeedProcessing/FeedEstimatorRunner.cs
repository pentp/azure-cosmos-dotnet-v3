﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed.FeedProcessing
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using static Microsoft.Azure.Cosmos.Container;

    /// <summary>
    /// Obtains the <see cref="ChangeFeedEstimator"/> estimation as a periodic Task and notifies it to a <see cref="ChangesEstimationHandler"/>..
    /// </summary>
    internal sealed class FeedEstimatorRunner
    {
        private static readonly TimeSpan defaultMonitoringDelay = TimeSpan.FromSeconds(5);
        private readonly ChangeFeedEstimator remainingWorkEstimator;
        private readonly TimeSpan monitoringDelay;
        private readonly ChangesEstimationHandler dispatchEstimation;

        public FeedEstimatorRunner(
            ChangesEstimationHandler dispatchEstimation,
            ChangeFeedEstimator remainingWorkEstimator,
            TimeSpan? estimationPeriod = null)
        {
            this.dispatchEstimation = dispatchEstimation;
            this.remainingWorkEstimator = remainingWorkEstimator;
            this.monitoringDelay = estimationPeriod ?? FeedEstimatorRunner.defaultMonitoringDelay;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await this.EstimateAsync(cancellationToken);
                }
                catch (TaskCanceledException canceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Extensions.TraceException(new Exception("exception within estimator", canceledException));

                    // ignore as it is caused by client
                }

                await Task.Delay(this.monitoringDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task EstimateAsync(CancellationToken cancellationToken)
        {
            long estimation;
            using (FeedIterator<ChangeFeedProcessorState> feedIterator = this.remainingWorkEstimator.GetCurrentStateIterator())
            {
                FeedResponse<ChangeFeedProcessorState> estimations = await feedIterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                // Gets all results in first page
                estimation = estimations.Count == 0 ? 1 : estimations.Sum(estimation => estimation.EstimatedLag);
            }
            try
            {
                await this.dispatchEstimation(estimation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception userException)
            {
                Extensions.TraceException(userException);
                DefaultTrace.TraceWarning("Exception happened on ChangeFeedEstimatorDispatcher.DispatchEstimation");
            }
        }
    }
}