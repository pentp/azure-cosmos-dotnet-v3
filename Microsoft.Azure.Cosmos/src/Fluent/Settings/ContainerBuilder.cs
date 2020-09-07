﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Fluent
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// <see cref="Container"/> fluent definition for creation flows.
    /// </summary>
    public class ContainerBuilder : ContainerDefinition<ContainerBuilder>
    {
        private readonly Database database;
        private readonly CosmosClientContext clientContext;
        private readonly Uri containerUri;
        private UniqueKeyPolicy uniqueKeyPolicy;
        private ConflictResolutionPolicy conflictResolutionPolicy;

        /// <summary>
        /// Creates an instance for unit-testing
        /// </summary>
        protected ContainerBuilder()
        {
        }

        internal ContainerBuilder(
            Database cosmosContainers,
            CosmosClientContext clientContext,
            string name,
            string partitionKeyPath = null)
            : base(name, partitionKeyPath)
        {
            this.database = cosmosContainers;
            this.clientContext = clientContext;
            this.containerUri = UriFactory.CreateDocumentCollectionUri(this.database.Id, name);
        }

        /// <summary>
        /// Defines a Unique Key policy for this Azure Cosmos container.
        /// </summary>
        /// <returns>An instance of <see cref="UniqueKeyDefinition"/>.</returns>
        public UniqueKeyDefinition WithUniqueKey()
        {
            return new UniqueKeyDefinition(
                this,
                (uniqueKey) => this.AddUniqueKey(uniqueKey));
        }

        /// <summary>
        /// Defined the conflict resolution for Azure Cosmos container
        /// </summary>
        /// <returns>An instance of <see cref="ConflictResolutionDefinition"/>.</returns>
        public ConflictResolutionDefinition WithConflictResolution()
        {
            return new ConflictResolutionDefinition(
                this,
                (conflictPolicy) => this.AddConflictResolution(conflictPolicy));
        }

        /// <summary>
        /// Creates a container with the current fluent definition.
        /// </summary>
        /// <param name="throughputProperties">Desired throughput for the container expressed in Request Units per second.</param>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>An asynchronous Task representing the creation of a <see cref="Container"/> based on the Fluent definition.</returns>
        /// <seealso href="https://docs.microsoft.com/azure/cosmos-db/request-units">Request Units</seealso>
        public Task<ContainerResponse> CreateAsync(
            ThroughputProperties throughputProperties,
            CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = this.Build();

            return this.database.CreateContainerAsync(
                containerProperties: containerProperties,
                throughputProperties: throughputProperties,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates a container if it does not exist with the current fluent definition.
        /// </summary>
        /// <param name="throughputProperties">Desired throughput for the container expressed in Request Units per second.</param>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>An asynchronous Task representing the creation of a <see cref="Container"/> based on the Fluent definition.</returns>
        /// <seealso href="https://docs.microsoft.com/azure/cosmos-db/request-units">Request Units</seealso>
        public Task<ContainerResponse> CreateIfNotExistsAsync(
            ThroughputProperties throughputProperties,
            CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = this.Build();

            return this.database.CreateContainerIfNotExistsAsync(
                containerProperties: containerProperties,
                throughputProperties: throughputProperties,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates a container with the current fluent definition.
        /// </summary>
        /// <param name="throughput">Desired throughput for the container expressed in Request Units per second.</param>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>An asynchronous Task representing the creation of a <see cref="Container"/> based on the Fluent definition.</returns>
        /// <seealso href="https://docs.microsoft.com/azure/cosmos-db/request-units">Request Units</seealso>
        public Task<ContainerResponse> CreateAsync(
            int? throughput = null,
            CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = this.Build();

            return this.database.CreateContainerAsync(
                containerProperties: containerProperties,
                throughput: throughput,
                requestOptions: null,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates a container if it does not exist with the current fluent definition.
        /// </summary>
        /// <param name="throughput">Desired throughput for the container expressed in Request Units per second.</param>
        /// <param name="cancellationToken">(Optional) <see cref="CancellationToken"/> representing request cancellation.</param>
        /// <returns>An asynchronous Task representing the creation of a <see cref="Container"/> based on the Fluent definition.</returns>
        /// <seealso href="https://docs.microsoft.com/azure/cosmos-db/request-units">Request Units</seealso>
        public Task<ContainerResponse> CreateIfNotExistsAsync(
            int? throughput = null,
            CancellationToken cancellationToken = default)
        {
            ContainerProperties containerProperties = this.Build();

            return this.database.CreateContainerIfNotExistsAsync(
                containerProperties: containerProperties,
                throughput: throughput,
                requestOptions: null,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Applies the current Fluent definition and creates a container configuration.
        /// </summary>
        /// <returns>Builds the current Fluent configuration into an instance of <see cref="ContainerProperties"/>.</returns>
        public new ContainerProperties Build()
        {
            ContainerProperties containerProperties = base.Build();

            if (this.uniqueKeyPolicy != null)
            {
                containerProperties.UniqueKeyPolicy = this.uniqueKeyPolicy;
            }

            if (this.conflictResolutionPolicy != null)
            {
                containerProperties.ConflictResolutionPolicy = this.conflictResolutionPolicy;
            }

            return containerProperties;
        }

        private void AddUniqueKey(UniqueKey uniqueKey)
        {
            if (this.uniqueKeyPolicy == null)
            {
                this.uniqueKeyPolicy = new UniqueKeyPolicy();
            }

            this.uniqueKeyPolicy.UniqueKeys.Add(uniqueKey);
        }

        private void AddConflictResolution(ConflictResolutionPolicy conflictResolutionPolicy)
        {
            if (conflictResolutionPolicy.Mode == ConflictResolutionMode.Custom
                && !string.IsNullOrEmpty(conflictResolutionPolicy.ResolutionProcedure))
            {
                this.clientContext.ValidateResource(conflictResolutionPolicy.ResolutionProcedure);
                conflictResolutionPolicy.ResolutionProcedure = UriFactory.CreateStoredProcedureUri(this.containerUri.ToString(), conflictResolutionPolicy.ResolutionProcedure).ToString();
            }

            this.conflictResolutionPolicy = conflictResolutionPolicy;
        }
    }
}
