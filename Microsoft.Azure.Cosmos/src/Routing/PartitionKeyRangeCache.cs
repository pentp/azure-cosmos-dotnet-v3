﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;
    using Microsoft.Azure.Documents.Routing;

    internal class PartitionKeyRangeCache : IRoutingMapProvider, ICollectionRoutingMapCache
    {
        private const string PageSizeString = "-1";

        private readonly AsyncCache<string, CollectionRoutingMap> routingMapCache;

        private readonly IAuthorizationTokenProvider authorizationTokenProvider;
        private readonly IStoreModel storeModel;
        private readonly CollectionCache collectionCache;

        public PartitionKeyRangeCache(IAuthorizationTokenProvider authorizationTokenProvider, IStoreModel storeModel, CollectionCache collectionCache)
        {
            this.routingMapCache = new AsyncCache<string, CollectionRoutingMap>(
                    EqualityComparer<CollectionRoutingMap>.Default,
                    StringComparer.Ordinal);
            this.authorizationTokenProvider = authorizationTokenProvider;
            this.storeModel = storeModel;
            this.collectionCache = collectionCache;
        }

        public virtual async Task<IReadOnlyList<PartitionKeyRange>> TryGetOverlappingRangesAsync(
            string collectionRid,
            Range<string> range,
            bool forceRefresh = false)
        {
            ResourceId collectionRidParsed;
            Debug.Assert(ResourceId.TryParse(collectionRid, out collectionRidParsed), "Could not parse CollectionRid from ResourceId.");

            CollectionRoutingMap routingMap =
                await this.TryLookupAsync(collectionRid, null, null, CancellationToken.None);

            if (forceRefresh && routingMap != null)
            {
                routingMap = await this.TryLookupAsync(collectionRid, routingMap, null, CancellationToken.None);
            }

            if (routingMap == null)
            {
                DefaultTrace.TraceWarning(string.Format("Routing Map Null for collection: {0} for range: {1}, forceRefresh:{2}", collectionRid, range.ToString(), forceRefresh));
                return null;
            }

            return routingMap.GetOverlappingRanges(range);
        }

        public async Task<PartitionKeyRange> TryGetPartitionKeyRangeByIdAsync(
            string collectionResourceId,
            string partitionKeyRangeId,
            bool forceRefresh = false)
        {
            ResourceId collectionRidParsed;
            Debug.Assert(ResourceId.TryParse(collectionResourceId, out collectionRidParsed), "Could not parse CollectionRid from ResourceId.");

            CollectionRoutingMap routingMap =
                await this.TryLookupAsync(collectionResourceId, null, null, CancellationToken.None);

            if (forceRefresh && routingMap != null)
            {
                routingMap = await this.TryLookupAsync(collectionResourceId, routingMap, null, CancellationToken.None);
            }

            if (routingMap == null)
            {
                DefaultTrace.TraceInformation(string.Format("Routing Map Null for collection: {0}, PartitionKeyRangeId: {1}, forceRefresh:{2}", collectionResourceId, partitionKeyRangeId, forceRefresh));
                return null;
            }

            return routingMap.TryGetRangeByPartitionKeyRangeId(partitionKeyRangeId);
        }

        public virtual async Task<CollectionRoutingMap> TryLookupAsync(
            string collectionRid,
            CollectionRoutingMap previousValue,
            DocumentServiceRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await this.routingMapCache.GetAsync(
                    collectionRid,
                    previousValue,
                    () => this.GetRoutingMapForCollectionAsync(collectionRid, previousValue, cancellationToken),
                    CancellationToken.None);
            }
            catch (DocumentClientException ex)
            {
                if (previousValue != null)
                {
                    StringBuilder rangesString = new StringBuilder();
                    foreach (PartitionKeyRange range in previousValue.OrderedPartitionKeyRanges)
                    {
                        rangesString.Append(range.ToRange().ToString());
                        rangesString.Append(", ");
                    }
                    DefaultTrace.TraceInformation(string.Format("DocumentClientException in TryLookupAsync Collection: {0}, previousValue: {1} Exception: {2}", collectionRid, rangesString.ToString(), ex.ToString()));
                }

                if (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                throw;
            }
        }

        public async Task<PartitionKeyRange> TryGetRangeByPartitionKeyRangeIdAsync(string collectionRid, string partitionKeyRangeId)
        {
            try
            {
                CollectionRoutingMap routingMap = await this.routingMapCache.GetAsync(
                    collectionRid,
                    null,
                    () => this.GetRoutingMapForCollectionAsync(collectionRid, null, CancellationToken.None),
                    CancellationToken.None).ConfigureAwait(false);

                return routingMap.TryGetRangeByPartitionKeyRangeId(partitionKeyRangeId);
            }
            catch (DocumentClientException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task<CollectionRoutingMap> GetRoutingMapForCollectionAsync(
            string collectionRid,
            CollectionRoutingMap previousRoutingMap,
            CancellationToken cancellationToken)
        {
            List<PartitionKeyRange> ranges = new List<PartitionKeyRange>();
            string changeFeedNextIfNoneMatch = previousRoutingMap == null ? null : previousRoutingMap.ChangeFeedNextIfNoneMatch;

            HttpStatusCode lastStatusCode = HttpStatusCode.OK;
            do
            {
                INameValueCollection headers = new DictionaryNameValueCollection();

                headers.Set(HttpConstants.HttpHeaders.PageSize, PageSizeString);
                headers.Set(HttpConstants.HttpHeaders.A_IM, HttpConstants.A_IMHeaderValues.IncrementalFeed);
                if (changeFeedNextIfNoneMatch != null)
                {
                    headers.Set(HttpConstants.HttpHeaders.IfNoneMatch, changeFeedNextIfNoneMatch);
                }

                RetryOptions retryOptions = new RetryOptions();
                using (DocumentServiceResponse response = await BackoffRetryUtility<DocumentServiceResponse>.ExecuteAsync(
                    () => this.ExecutePartitionKeyRangeReadChangeFeedAsync(collectionRid, headers),
                    new ResourceThrottleRetryPolicy(retryOptions.MaxRetryAttemptsOnThrottledRequests, retryOptions.MaxRetryWaitTimeInSeconds),
                    cancellationToken))
                {
                    lastStatusCode = response.StatusCode;
                    changeFeedNextIfNoneMatch = response.Headers[HttpConstants.HttpHeaders.ETag];

                    FeedResource<PartitionKeyRange> feedResource = response.GetResource<FeedResource<PartitionKeyRange>>();
                    if (feedResource != null)
                    {
                        ranges.AddRange(feedResource);
                    }
                }
            }
            while (lastStatusCode != HttpStatusCode.NotModified);

            IEnumerable<Tuple<PartitionKeyRange, ServiceIdentity>> tuples = ranges.Select(range => Tuple.Create(range, (ServiceIdentity)null));

            CollectionRoutingMap routingMap;
            if (previousRoutingMap == null)
            {
                // Splits could have happened during change feed query and we might have a mix of gone and new ranges.
                HashSet<string> goneRanges = new HashSet<string>(ranges.SelectMany(range => range.Parents ?? Enumerable.Empty<string>()));
                routingMap = CollectionRoutingMap.TryCreateCompleteRoutingMap(
                    tuples.Where(tuple => !goneRanges.Contains(tuple.Item1.Id)),
                    string.Empty,
                    changeFeedNextIfNoneMatch);
            }
            else
            {
                routingMap = previousRoutingMap.TryCombine(tuples, changeFeedNextIfNoneMatch);
            }

            if (routingMap == null)
            {
                // Range information either doesn't exist or is not complete.
                throw new NotFoundException($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}: GetRoutingMapForCollectionAsync(collectionRid: {collectionRid}), Range information either doesn't exist or is not complete.");
            }

            return routingMap;
        }

        private async Task<DocumentServiceResponse> ExecutePartitionKeyRangeReadChangeFeedAsync(string collectionRid, INameValueCollection headers)
        {
            using (DocumentServiceRequest request = DocumentServiceRequest.Create(
                OperationType.ReadFeed,
                collectionRid,
                ResourceType.PartitionKeyRange,
                AuthorizationTokenType.PrimaryMasterKey,
                headers))
            {
                string authorizationToken = null;
                try
                {
                    authorizationToken = (await this.authorizationTokenProvider.GetUserAuthorizationAsync(
                        request.ResourceAddress,
                        PathsHelper.GetResourcePath(request.ResourceType),
                        HttpConstants.HttpMethods.Get,
                        request.Headers,
                        AuthorizationTokenType.PrimaryMasterKey)).token;
                }
                catch (UnauthorizedException)
                {
                }

                if (authorizationToken == null)
                {
                    // User doesn't have rid based resource token. Maybe he has name based.
                    throw new NotSupportedException("Resource tokens are not supported");

                    ////CosmosContainerSettings collection = await this.collectionCache.ResolveCollectionAsync(request, CancellationToken.None);
                    ////authorizationToken =
                    ////    this.authorizationTokenProvider.GetUserAuthorizationToken(
                    ////        collection.AltLink,
                    ////        PathsHelper.GetResourcePath(request.ResourceType),
                    ////        HttpConstants.HttpMethods.Get,
                    ////        request.Headers,
                    ////        AuthorizationTokenType.PrimaryMasterKey);
                }

                request.Headers[HttpConstants.HttpHeaders.Authorization] = authorizationToken;

                using (new ActivityScope(Guid.NewGuid()))
                {
                    return await this.storeModel.ProcessMessageAsync(request);
                }
            }
        }
    }
}
