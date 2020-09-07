﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.ObjectModel;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Client policy is combination of endpoint change retry + throttling retry.
    /// </summary>
    internal sealed class ClientRetryPolicy : IDocumentClientRetryPolicy
    {
        private const int RetryIntervalInMS = 1000; // Once we detect failover wait for 1 second before retrying request.
        private const int MaxRetryCount = 120;
        private const int MaxServiceUnavailableRetryCount = 1;

        private readonly IDocumentClientRetryPolicy throttlingRetry;
        private readonly GlobalEndpointManager globalEndpointManager;
        private readonly bool enableEndpointDiscovery;
        private int failoverRetryCount;

        private int sessionTokenRetryCount;
        private int serviceUnavailableRetryCount;
        private bool isReadRequest;
        private bool canUseMultipleWriteLocations;
        private Uri locationEndpoint;
        private RetryContext retryContext;

        private IClientSideRequestStatistics sharedStatistics;

        public ClientRetryPolicy(
            GlobalEndpointManager globalEndpointManager,
            bool enableEndpointDiscovery,
            RetryOptions retryOptions)
        {
            this.throttlingRetry = new ResourceThrottleRetryPolicy(
                retryOptions.MaxRetryAttemptsOnThrottledRequests,
                retryOptions.MaxRetryWaitTimeInSeconds);

            this.globalEndpointManager = globalEndpointManager;
            this.failoverRetryCount = 0;
            this.enableEndpointDiscovery = enableEndpointDiscovery;
            this.sessionTokenRetryCount = 0;
            this.serviceUnavailableRetryCount = 0;
            this.canUseMultipleWriteLocations = false;
        }

        /// <summary> 
        /// Should the caller retry the operation.
        /// </summary>
        /// <param name="exception">Exception that occurred when the operation was tried</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True indicates caller should retry, False otherwise</returns>
        public async Task<ShouldRetryResult> ShouldRetryAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            this.retryContext = null;
            // Received Connection error (HttpRequestException), initiate the endpoint rediscovery
            if (exception is HttpRequestException)
            {
                DefaultTrace.TraceWarning("Endpoint not reachable. Refresh cache and retry");
                return await this.ShouldRetryOnEndpointFailureAsync(this.isReadRequest, false).ConfigureAwait(false);
            }

            DocumentClientException clientException = exception as DocumentClientException;

            if (clientException?.RequestStatistics != null)
            {
                this.sharedStatistics = clientException.RequestStatistics;
            }

            ShouldRetryResult shouldRetryResult = await this.ShouldRetryInternalAsync(
                clientException?.StatusCode,
                clientException?.GetSubStatus()).ConfigureAwait(false);
            if (shouldRetryResult != null)
            {
                return shouldRetryResult;
            }

            return await this.throttlingRetry.ShouldRetryAsync(exception, cancellationToken).ConfigureAwait(false);
        }

        /// <summary> 
        /// Should the caller retry the operation.
        /// </summary>
        /// <param name="cosmosResponseMessage"><see cref="ResponseMessage"/> in return of the request</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True indicates caller should retry, False otherwise</returns>
        public async Task<ShouldRetryResult> ShouldRetryAsync(
            ResponseMessage cosmosResponseMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.retryContext = null;

            ShouldRetryResult shouldRetryResult = await this.ShouldRetryInternalAsync(
                    cosmosResponseMessage?.StatusCode,
                    cosmosResponseMessage?.Headers.SubStatusCode).ConfigureAwait(false);
            if (shouldRetryResult != null)
            {
                return shouldRetryResult;
            }

            return await this.throttlingRetry.ShouldRetryAsync(cosmosResponseMessage, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Method that is called before a request is sent to allow the retry policy implementation
        /// to modify the state of the request.
        /// </summary>
        /// <param name="request">The request being sent to the service.</param>
        public void OnBeforeSendRequest(DocumentServiceRequest request)
        {
            this.isReadRequest = request.IsReadOnlyRequest;
            this.canUseMultipleWriteLocations = this.globalEndpointManager.CanUseMultipleWriteLocations(request);

            if (request.RequestContext.ClientRequestStatistics == null)
            {
                if (this.sharedStatistics == null)
                {
                    this.sharedStatistics = new CosmosClientSideRequestStatistics();
                }

                request.RequestContext.ClientRequestStatistics = this.sharedStatistics;
            }
            else
            {
                this.sharedStatistics = request.RequestContext.ClientRequestStatistics;
            }

            // clear previous location-based routing directive
            request.RequestContext.ClearRouteToLocation();

            if (this.retryContext != null)
            {
                // set location-based routing directive based on request retry context
                request.RequestContext.RouteToLocation(this.retryContext.RetryCount, this.retryContext.RetryRequestOnPreferredLocations);
            }

            // Resolve the endpoint for the request and pin the resolution to the resolved endpoint
            // This enables marking the endpoint unavailability on endpoint failover/unreachability
            this.locationEndpoint = this.globalEndpointManager.ResolveServiceEndpoint(request);
            request.RequestContext.RouteToLocation(this.locationEndpoint);
        }

        private Task<ShouldRetryResult> ShouldRetryInternalAsync(
            HttpStatusCode? statusCode,
            SubStatusCodes? subStatusCode)
        {
            if (!statusCode.HasValue
                && (!subStatusCode.HasValue
                || subStatusCode.Value == SubStatusCodes.Unknown))
            {
                return Task.FromResult<ShouldRetryResult>(null);
            }

            // Received 403.3 on write region, initiate the endpoint rediscovery
            if (statusCode == HttpStatusCode.Forbidden
                && subStatusCode == SubStatusCodes.WriteForbidden)
            {
                DefaultTrace.TraceWarning("Endpoint not writable. Refresh cache and retry");
                return this.ShouldRetryOnEndpointFailureAsync(false, true);
            }

            // Regional endpoint is not available yet for reads (e.g. add/ online of region is in progress)
            if (statusCode == HttpStatusCode.Forbidden
                && subStatusCode == SubStatusCodes.DatabaseAccountNotFound
                && (this.isReadRequest || this.canUseMultipleWriteLocations))
            {
                DefaultTrace.TraceWarning("Endpoint not available for reads. Refresh cache and retry");
                return this.ShouldRetryOnEndpointFailureAsync(true, false);
            }

            if (statusCode == HttpStatusCode.NotFound
                && subStatusCode == SubStatusCodes.ReadSessionNotAvailable)
            {
                return Task.FromResult(this.ShouldRetryOnSessionNotAvailable());
            }

            // Received 503.0 due to client connect timeout or Gateway
            if (statusCode == HttpStatusCode.ServiceUnavailable
                && subStatusCode == SubStatusCodes.Unknown)
            {
                return Task.FromResult(this.ShouldRetryOnServiceUnavailable());
            }

            return Task.FromResult<ShouldRetryResult>(null);
        }

        private async Task<ShouldRetryResult> ShouldRetryOnEndpointFailureAsync(bool isReadRequest, bool forceRefresh)
        {
            if (!this.enableEndpointDiscovery || this.failoverRetryCount > MaxRetryCount)
            {
                DefaultTrace.TraceInformation("ShouldRetryOnEndpointFailureAsync() Not retrying. Retry count = {0}", this.failoverRetryCount);
                return ShouldRetryResult.NoRetry();
            }

            this.failoverRetryCount++;

            if (this.locationEndpoint != null)
            {
                if (isReadRequest)
                {
                    this.globalEndpointManager.MarkEndpointUnavailableForRead(this.locationEndpoint);
                }
                else
                {
                    this.globalEndpointManager.MarkEndpointUnavailableForWrite(this.locationEndpoint);
                }
            }

            TimeSpan retryDelay = TimeSpan.Zero;
            if (!isReadRequest)
            {
                DefaultTrace.TraceInformation("Failover happening. retryCount {0}", this.failoverRetryCount);

                if (this.failoverRetryCount > 1)
                {
                    //if retried both endpoints, follow regular retry interval.
                    retryDelay = TimeSpan.FromMilliseconds(ClientRetryPolicy.RetryIntervalInMS);
                }
            }
            else
            {
                retryDelay = TimeSpan.FromMilliseconds(ClientRetryPolicy.RetryIntervalInMS);
            }

            await this.globalEndpointManager.RefreshLocationAsync(null, forceRefresh).ConfigureAwait(false);

            this.retryContext = new RetryContext
            {
                RetryCount = this.failoverRetryCount,
                RetryRequestOnPreferredLocations = false
            };

            return ShouldRetryResult.RetryAfter(retryDelay);
        }

        private ShouldRetryResult ShouldRetryOnSessionNotAvailable()
        {
            this.sessionTokenRetryCount++;

            if (!this.enableEndpointDiscovery)
            {
                // if endpoint discovery is disabled, the request cannot be retried anywhere else
                return ShouldRetryResult.NoRetry();
            }
            else
            {
                if (this.canUseMultipleWriteLocations)
                {
                    ReadOnlyCollection<Uri> endpoints = this.isReadRequest ? this.globalEndpointManager.ReadEndpoints : this.globalEndpointManager.WriteEndpoints;

                    if (this.sessionTokenRetryCount > endpoints.Count)
                    {
                        // When use multiple write locations is true and the request has been tried 
                        // on all locations, then don't retry the request
                        return ShouldRetryResult.NoRetry();
                    }
                    else
                    {
                        this.retryContext = new RetryContext()
                        {
                            RetryCount = this.sessionTokenRetryCount - 1,
                            RetryRequestOnPreferredLocations = this.sessionTokenRetryCount > 1
                        };

                        return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
                    }
                }
                else
                {
                    if (this.sessionTokenRetryCount > 1)
                    {
                        // When cannot use multiple write locations, then don't retry the request if 
                        // we have already tried this request on the write location
                        return ShouldRetryResult.NoRetry();
                    }
                    else
                    {
                        this.retryContext = new RetryContext
                        {
                            RetryCount = this.sessionTokenRetryCount - 1,
                            RetryRequestOnPreferredLocations = false
                        };

                        return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
                    }
                }
            }
        }

        /// <summary>
        /// For a ServiceUnavailable (503.0) we could be having a timeout from Direct/TCP locally or a request to Gateway request with a similar response due to an endpoint not yet available.
        /// We try and retry the request only if there are other regions available.
        /// </summary>
        private ShouldRetryResult ShouldRetryOnServiceUnavailable()
        {
            if (this.serviceUnavailableRetryCount++ >= ClientRetryPolicy.MaxServiceUnavailableRetryCount)
            {
                DefaultTrace.TraceInformation($"ShouldRetryOnServiceUnavailable() Not retrying. Retry count = {this.serviceUnavailableRetryCount}.");
                return ShouldRetryResult.NoRetry();
            }

            if (!this.canUseMultipleWriteLocations
                    && !this.isReadRequest)
            {
                // Write requests on single master cannot be retried, no other regions available
                return ShouldRetryResult.NoRetry();
            }

            int availablePreferredLocations = this.globalEndpointManager.PreferredLocationCount;

            if (availablePreferredLocations <= 1)
            {
                // No other regions to retry on
                DefaultTrace.TraceInformation($"ShouldRetryOnServiceUnavailable() Not retrying. No other regions available for the request. AvailablePreferredLocations = {availablePreferredLocations}.");
                return ShouldRetryResult.NoRetry();
            }

            DefaultTrace.TraceInformation($"ShouldRetryOnServiceUnavailable() Retrying. Received on endpoint {this.locationEndpoint}, IsReadRequest = {this.isReadRequest}.");

            // Retrying on second PreferredLocations
            // RetryCount is used as zero-based index
            this.retryContext = new RetryContext()
            {
                RetryCount = this.serviceUnavailableRetryCount,
                RetryRequestOnPreferredLocations = true
            };

            return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
        }

        private sealed class RetryContext
        {
            public int RetryCount { get; set; }
            public bool RetryRequestOnPreferredLocations { get; set; }
        }
    }
}
