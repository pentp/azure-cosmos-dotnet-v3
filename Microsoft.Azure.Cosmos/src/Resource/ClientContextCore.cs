﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Handlers;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    internal class ClientContextCore : CosmosClientContext
    {
        private readonly BatchAsyncContainerExecutorCache batchExecutorCache;
        private readonly CosmosClient client;
        private readonly DocumentClient documentClient;
        private readonly CosmosSerializerCore serializerCore;
        private readonly CosmosResponseFactoryInternal responseFactory;
        private readonly RequestInvokerHandler requestHandler;
        private readonly CosmosClientOptions clientOptions;

        private readonly string userAgent;
        private bool isDisposed = false;

        private ClientContextCore(
            CosmosClient client,
            CosmosClientOptions clientOptions,
            CosmosSerializerCore serializerCore,
            CosmosResponseFactoryInternal cosmosResponseFactory,
            RequestInvokerHandler requestHandler,
            DocumentClient documentClient,
            string userAgent,
            BatchAsyncContainerExecutorCache batchExecutorCache)
        {
            this.client = client;
            this.clientOptions = clientOptions;
            this.serializerCore = serializerCore;
            this.responseFactory = cosmosResponseFactory;
            this.requestHandler = requestHandler;
            this.documentClient = documentClient;
            this.userAgent = userAgent;
            this.batchExecutorCache = batchExecutorCache;
        }

        internal static CosmosClientContext Create(
            CosmosClient cosmosClient,
            CosmosClientOptions clientOptions)
        {
            if (cosmosClient == null)
            {
                throw new ArgumentNullException(nameof(cosmosClient));
            }

            clientOptions = ClientContextCore.CreateOrCloneClientOptions(clientOptions);
            HttpMessageHandler httpMessageHandler = CosmosHttpClientCore.CreateHttpClientHandler(
                clientOptions.GatewayModeMaxConnectionLimit,
                clientOptions.WebProxy);

            DocumentClient documentClient = new DocumentClient(
               cosmosClient.Endpoint,
               cosmosClient.AccountKey,
               apitype: clientOptions.ApiType,
               sendingRequestEventArgs: clientOptions.SendingRequestEventArgs,
               transportClientHandlerFactory: clientOptions.TransportClientHandlerFactory,
               connectionPolicy: clientOptions.GetConnectionPolicy(),
               enableCpuMonitor: clientOptions.EnableCpuMonitor,
               storeClientFactory: clientOptions.StoreClientFactory,
               desiredConsistencyLevel: clientOptions.GetDocumentsConsistencyLevel(),
               handler: httpMessageHandler,
               sessionContainer: clientOptions.SessionContainer);

            return ClientContextCore.Create(
                cosmosClient,
                documentClient,
                clientOptions);
        }

        internal static CosmosClientContext Create(
            CosmosClient cosmosClient,
            DocumentClient documentClient,
            CosmosClientOptions clientOptions,
            RequestInvokerHandler requestInvokerHandler = null)
        {
            if (cosmosClient == null)
            {
                throw new ArgumentNullException(nameof(cosmosClient));
            }

            if (documentClient == null)
            {
                throw new ArgumentNullException(nameof(documentClient));
            }

            clientOptions = ClientContextCore.CreateOrCloneClientOptions(clientOptions);

            if (requestInvokerHandler == null)
            {
                //Request pipeline 
                ClientPipelineBuilder clientPipelineBuilder = new ClientPipelineBuilder(
                    cosmosClient,
                    clientOptions.ConsistencyLevel,
                    clientOptions.CustomHandlers);

                requestInvokerHandler = clientPipelineBuilder.Build();
            }

            CosmosSerializerCore serializerCore = CosmosSerializerCore.Create(
                clientOptions.Serializer,
                clientOptions.SerializerOptions);

            // This sets the serializer on client options which gives users access to it if a custom one is not configured.
            clientOptions.SetSerializerIfNotConfigured(serializerCore.GetCustomOrDefaultSerializer());

            CosmosResponseFactoryInternal responseFactory = new CosmosResponseFactoryCore(serializerCore);

            return new ClientContextCore(
                client: cosmosClient,
                clientOptions: clientOptions,
                serializerCore: serializerCore,
                cosmosResponseFactory: responseFactory,
                requestHandler: requestInvokerHandler,
                documentClient: documentClient,
                userAgent: documentClient.ConnectionPolicy.UserAgentContainer.UserAgent,
                batchExecutorCache: new BatchAsyncContainerExecutorCache());
        }

        /// <summary>
        /// The Cosmos client that is used for the request
        /// </summary>
        internal override CosmosClient Client => this.ThrowIfDisposed(this.client);

        internal override DocumentClient DocumentClient => this.ThrowIfDisposed(this.documentClient);

        internal override CosmosSerializerCore SerializerCore => this.ThrowIfDisposed(this.serializerCore);

        internal override CosmosResponseFactoryInternal ResponseFactory => this.ThrowIfDisposed(this.responseFactory);

        internal override RequestInvokerHandler RequestHandler => this.ThrowIfDisposed(this.requestHandler);

        internal override CosmosClientOptions ClientOptions => this.ThrowIfDisposed(this.clientOptions);

        internal override string UserAgent => this.ThrowIfDisposed(this.userAgent);

        /// <summary>
        /// Generates the URI link for the resource
        /// </summary>
        /// <param name="parentLink">The parent link URI (/dbs/mydbId) </param>
        /// <param name="uriPathSegment">The URI path segment</param>
        /// <param name="id">The id of the resource</param>
        /// <returns>A resource link in the format of {parentLink}/this.UriPathSegment/this.Name with this.Name being a Uri escaped version</returns>
        internal override string CreateLink(
            string parentLink,
            string uriPathSegment,
            string id)
        {
            this.ThrowIfDisposed();
            int parentLinkLength = parentLink?.Length ?? 0;
            string idUriEscaped = Uri.EscapeUriString(id);

            Debug.Assert(parentLinkLength == 0 || !parentLink.EndsWith("/"));

            StringBuilder stringBuilder = new StringBuilder(parentLinkLength + 2 + uriPathSegment.Length + idUriEscaped.Length);
            if (parentLinkLength > 0)
            {
                stringBuilder.Append(parentLink);
                stringBuilder.Append("/");
            }

            stringBuilder.Append(uriPathSegment);
            stringBuilder.Append("/");
            stringBuilder.Append(idUriEscaped);
            return stringBuilder.ToString();
        }

        internal override void ValidateResource(string resourceId)
        {
            this.ThrowIfDisposed();
            this.DocumentClient.ValidateResource(resourceId);
        }

        internal override Task<TResult> OperationHelperAsync<TResult>(
            string operationName,
            RequestOptions requestOptions,
            Func<CosmosDiagnosticsContext, Task<TResult>> task)
        {
            CosmosDiagnosticsContext diagnosticsContext = this.CreateDiagnosticContext(
               operationName,
               requestOptions);

            if (SynchronizationContext.Current == null)
            {
                return this.RunWithDiagnosticsHelperAsync(
                    diagnosticsContext,
                    task);
            }

            return this.RunWithSynchronizationContextAndDiagnosticsHelperAsync(
                    diagnosticsContext,
                    task);
        }

        internal override CosmosDiagnosticsContext CreateDiagnosticContext(
            string operationName,
            RequestOptions requestOptions)
        {
            return CosmosDiagnosticsContextCore.Create(
                operationName,
                requestOptions,
                this.UserAgent);
        }

        internal override Task<ResponseMessage> ProcessResourceOperationStreamAsync(
            string resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            RequestOptions requestOptions,
            ContainerInternal cosmosContainerCore,
            PartitionKey? partitionKey,
            string itemId,
            Stream streamPayload,
            Action<RequestMessage> requestEnricher,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            if (this.IsBulkOperationSupported(resourceType, operationType))
            {
                if (!partitionKey.HasValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(partitionKey));
                }

                if (requestEnricher != null)
                {
                    throw new ArgumentException($"Bulk does not support {nameof(requestEnricher)}");
                }

                return this.ProcessResourceOperationAsBulkStreamAsync(
                    operationType: operationType,
                    requestOptions: requestOptions,
                    cosmosContainerCore: cosmosContainerCore,
                    partitionKey: partitionKey.Value,
                    itemId: itemId,
                    streamPayload: streamPayload,
                    diagnosticsContext: diagnosticsContext,
                    cancellationToken: cancellationToken);
            }

            return this.ProcessResourceOperationStreamAsync(
                resourceUri: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: requestOptions,
                cosmosContainerCore: cosmosContainerCore,
                partitionKey: partitionKey,
                streamPayload: streamPayload,
                requestEnricher: requestEnricher,
                diagnosticsContext: diagnosticsContext,
                cancellationToken: cancellationToken);
        }

        internal override Task<ResponseMessage> ProcessResourceOperationStreamAsync(
            string resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            RequestOptions requestOptions,
            ContainerInternal cosmosContainerCore,
            PartitionKey? partitionKey,
            Stream streamPayload,
            Action<RequestMessage> requestEnricher,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            return this.RequestHandler.SendAsync(
                resourceUriString: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: requestOptions,
                cosmosContainerCore: cosmosContainerCore,
                partitionKey: partitionKey,
                streamPayload: streamPayload,
                requestEnricher: requestEnricher,
                diagnosticsContext: diagnosticsContext,
                cancellationToken: cancellationToken);
        }

        internal override Task<T> ProcessResourceOperationAsync<T>(
            string resourceUri,
            ResourceType resourceType,
            OperationType operationType,
            RequestOptions requestOptions,
            ContainerInternal cosmosContainerCore,
            PartitionKey? partitionKey,
            Stream streamPayload,
            Action<RequestMessage> requestEnricher,
            Func<ResponseMessage, T> responseCreator,
            CosmosDiagnosticsContext diagnosticsScope,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();

            return this.RequestHandler.SendAsync<T>(
                resourceUri: resourceUri,
                resourceType: resourceType,
                operationType: operationType,
                requestOptions: requestOptions,
                cosmosContainerCore: cosmosContainerCore,
                partitionKey: partitionKey,
                streamPayload: streamPayload,
                requestEnricher: requestEnricher,
                responseCreator: responseCreator,
                diagnosticsScope: diagnosticsScope,
                cancellationToken: cancellationToken);
        }

        internal override async Task<ContainerProperties> GetCachedContainerPropertiesAsync(
            string containerUri,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            CosmosDiagnosticsContext diagnosticsContext = CosmosDiagnosticsContextCore.Create(requestOptions: null);
            using (diagnosticsContext.GetOverallScope())
            {
                ClientCollectionCache collectionCache = await this.DocumentClient.GetCollectionCacheAsync().ConfigureAwait(false);
                try
                {
                    using (diagnosticsContext.CreateScope("ContainerCache.ResolveByNameAsync"))
                    {
                        return await collectionCache.ResolveByNameAsync(
                            HttpConstants.Versions.CurrentVersion,
                            containerUri,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (DocumentClientException ex)
                {
                    throw CosmosExceptionFactory.Create(ex, diagnosticsContext);
                }
            }

        }

        internal override BatchAsyncContainerExecutor GetExecutorForContainer(ContainerInternal container)
        {
            this.ThrowIfDisposed();

            if (!this.ClientOptions.AllowBulkExecution)
            {
                return null;
            }

            return this.batchExecutorCache.GetExecutorForContainer(container, this);
        }

        public override void Dispose()
        {
            this.Dispose(true);
        }

        /// <summary>
        /// Dispose of cosmos client
        /// </summary>
        /// <param name="disposing">True if disposing</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.isDisposed)
            {
                if (disposing)
                {
                    this.batchExecutorCache.Dispose();
                    this.DocumentClient.Dispose();
                }

                this.isDisposed = true;
            }
        }

        private Task<TResult> RunWithSynchronizationContextAndDiagnosticsHelperAsync<TResult>(
            CosmosDiagnosticsContext diagnosticsContext,
            Func<CosmosDiagnosticsContext, Task<TResult>> task)
        {
            Debug.Assert(SynchronizationContext.Current != null, "This should only be used when a SynchronizationContext is specified");

            // Used on NETFX applications with SynchronizationContext when doing locking calls
            IDisposable synchronizationContextScope = diagnosticsContext.CreateScope("SynchronizationContext");
            return Task.Run(() =>
            {
                using (new ActivityScope(Guid.NewGuid()))
                {
                    // The goal of synchronizationContextScope is to log how much latency the Task.Run added to the latency.
                    // Dispose of it here so it only measures the latency added by the Task.Run.
                    synchronizationContextScope.Dispose();
                    return this.RunWithDiagnosticsHelperAsync<TResult>(
                        diagnosticsContext,
                        task);
                }
            });
        }

        private async Task<TResult> RunWithDiagnosticsHelperAsync<TResult>(
            CosmosDiagnosticsContext diagnosticsContext,
            Func<CosmosDiagnosticsContext, Task<TResult>> task)
        {
            using (new ActivityScope(Guid.NewGuid()))
            {
                try
                {
                    using (diagnosticsContext.GetOverallScope())
                    {
                        return await task(diagnosticsContext).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException oe) when (!(oe is CosmosOperationCanceledException))
                {
                    throw new CosmosOperationCanceledException(oe, diagnosticsContext);
                }
            }
        }

        private async Task<ResponseMessage> ProcessResourceOperationAsBulkStreamAsync(
            OperationType operationType,
            RequestOptions requestOptions,
            ContainerInternal cosmosContainerCore,
            PartitionKey partitionKey,
            string itemId,
            Stream streamPayload,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            this.ThrowIfDisposed();
            ItemRequestOptions itemRequestOptions = requestOptions as ItemRequestOptions;
            TransactionalBatchItemRequestOptions batchItemRequestOptions = TransactionalBatchItemRequestOptions.FromItemRequestOptions(itemRequestOptions);
            ItemBatchOperation itemBatchOperation = new ItemBatchOperation(
                operationType: operationType,
                operationIndex: 0,
                partitionKey: partitionKey,
                id: itemId,
                resourceStream: streamPayload,
                requestOptions: batchItemRequestOptions,
                diagnosticsContext: diagnosticsContext);

            TransactionalBatchOperationResult batchOperationResult = await cosmosContainerCore.BatchExecutor.AddAsync(
                itemBatchOperation,
                itemRequestOptions,
                cancellationToken).ConfigureAwait(false);

            return batchOperationResult.ToResponseMessage();
        }

        private bool IsBulkOperationSupported(
            ResourceType resourceType,
            OperationType operationType)
        {
            this.ThrowIfDisposed();
            if (!this.ClientOptions.AllowBulkExecution)
            {
                return false;
            }

            return resourceType == ResourceType.Document
                && (operationType == OperationType.Create
                || operationType == OperationType.Upsert
                || operationType == OperationType.Read
                || operationType == OperationType.Delete
                || operationType == OperationType.Replace
                || operationType == OperationType.Patch);
        }

        private static CosmosClientOptions CreateOrCloneClientOptions(CosmosClientOptions clientOptions)
        {
            if (clientOptions == null)
            {
                return new CosmosClientOptions();
            }

            return clientOptions.Clone();
        }

        internal T ThrowIfDisposed<T>(T input)
        {
            this.ThrowIfDisposed();

            return input;
        }

        private void ThrowIfDisposed()
        {
            if (this.isDisposed)
            {
                throw new ObjectDisposedException($"Accessing {nameof(CosmosClient)} after it is disposed is invalid.");
            }
        }
    }
}
