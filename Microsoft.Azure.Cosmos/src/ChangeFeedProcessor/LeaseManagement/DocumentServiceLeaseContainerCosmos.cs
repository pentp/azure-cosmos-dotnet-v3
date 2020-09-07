﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed.LeaseManagement
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.ChangeFeed.Utils;

    internal sealed class DocumentServiceLeaseContainerCosmos : DocumentServiceLeaseContainer
    {
        private readonly Container container;
        private readonly DocumentServiceLeaseStoreManagerOptions options;
        private static readonly QueryRequestOptions queryRequestOptions = new QueryRequestOptions() { MaxConcurrency = 0 };

        public DocumentServiceLeaseContainerCosmos(
            Container container,
            DocumentServiceLeaseStoreManagerOptions options)
        {
            this.container = container;
            this.options = options;
        }

        public override async Task<IEnumerable<DocumentServiceLease>> GetOwnedLeasesAsync()
        {
            List<DocumentServiceLease> ownedLeases = new List<DocumentServiceLease>();
            foreach (DocumentServiceLease lease in await this.GetAllLeasesAsync().ConfigureAwait(false))
            {
                if (string.Equals(lease.Owner, this.options.HostName, StringComparison.OrdinalIgnoreCase))
                {
                    ownedLeases.Add(lease);
                }
            }

            return ownedLeases;
        }

        public override async Task<IReadOnlyList<DocumentServiceLease>> GetAllLeasesAsync()
        {
            string prefix = this.options.GetPartitionLeasePrefix();
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException("Prefix must be non-empty string", nameof(prefix));

            FeedIterator iterator = this.container.GetItemQueryStreamIterator(
                "SELECT * FROM c WHERE STARTSWITH(c.id, '" + prefix + "')",
                continuationToken: null,
                requestOptions: queryRequestOptions);

            List<DocumentServiceLeaseCore> leases = new List<DocumentServiceLeaseCore>();
            while (iterator.HasMoreResults)
            {
                using (ResponseMessage responseMessage = await iterator.ReadNextAsync().ConfigureAwait(false))
                {
                    responseMessage.EnsureSuccessStatusCode();
                    leases.AddRange(CosmosFeedResponseSerializer.FromFeedResponseStream<DocumentServiceLeaseCore>(
                        CosmosContainerExtensions.DefaultJsonSerializer,
                        responseMessage.Content));
                }   
            }

            return leases;
        }
    }
}
