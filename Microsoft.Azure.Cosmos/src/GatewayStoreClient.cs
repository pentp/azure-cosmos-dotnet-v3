﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;
    using Newtonsoft.Json;

    internal class GatewayStoreClient : TransportClient
    {
        private readonly ICommunicationEventSource eventSource;
        private readonly CosmosHttpClient httpClient;
        private JsonSerializerSettings SerializerSettings;
        private static readonly HttpMethod httpPatchMethod = new HttpMethod(HttpConstants.HttpMethods.Patch);

        public GatewayStoreClient(
            CosmosHttpClient httpClient,
            ICommunicationEventSource eventSource,
            JsonSerializerSettings serializerSettings = null)
        {
            this.httpClient = httpClient;
            this.SerializerSettings = serializerSettings;
            this.eventSource = eventSource;
        }

        public async Task<DocumentServiceResponse> InvokeAsync(
           DocumentServiceRequest request,
           ResourceType resourceType,
           Uri physicalAddress,
           CancellationToken cancellationToken)
        {
            using (HttpResponseMessage responseMessage = await this.InvokeClientAsync(request, resourceType, physicalAddress, cancellationToken).ConfigureAwait(false))
            {
                return await ParseResponseAsync(responseMessage, request.SerializerSettings ?? this.SerializerSettings, request).ConfigureAwait(false);
            }
        }

        public static bool IsFeedRequest(OperationType requestOperationType)
        {
            return requestOperationType == OperationType.Create ||
                requestOperationType == OperationType.Upsert ||
                requestOperationType == OperationType.ReadFeed ||
                requestOperationType == OperationType.Query ||
                requestOperationType == OperationType.SqlQuery ||
                requestOperationType == OperationType.QueryPlan ||
                requestOperationType == OperationType.Batch;
        }

        internal override async Task<StoreResponse> InvokeStoreAsync(Uri baseAddress, ResourceOperation resourceOperation, DocumentServiceRequest request)
        {
            Uri physicalAddress = IsFeedRequest(request.OperationType) ?
                HttpTransportClient.GetResourceFeedUri(resourceOperation.resourceType, baseAddress, request) :
                HttpTransportClient.GetResourceEntryUri(resourceOperation.resourceType, baseAddress, request);

            using (HttpResponseMessage responseMessage = await this.InvokeClientAsync(request, resourceOperation.resourceType, physicalAddress, default(CancellationToken)).ConfigureAwait(false))
            {
                return await HttpTransportClient.ProcessHttpResponse(request.ResourceAddress, string.Empty, responseMessage, physicalAddress, request).ConfigureAwait(false);
            }
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposable object returned by method")]
        internal Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> requestMessage,
            ResourceType resourceType,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return this.httpClient.SendHttpAsync(
                createRequestMessageAsync: requestMessage,
                resourceType: resourceType,
                diagnosticsContext: null,
                cancellationToken: cancellationToken);
        }

        internal static async Task<DocumentServiceResponse> ParseResponseAsync(HttpResponseMessage responseMessage, JsonSerializerSettings serializerSettings = null, DocumentServiceRequest request = null)
        {
            using (responseMessage)
            {
                IClientSideRequestStatistics requestStatistics = request?.RequestContext?.ClientRequestStatistics;
                if ((int)responseMessage.StatusCode < 400
                    || (request != null && request.IsValidStatusCodeForExceptionlessRetry((int)responseMessage.StatusCode)))
                {
                    INameValueCollection headers = ExtractResponseHeaders(responseMessage);
                    Stream contentStream = await BufferContentIfAvailableAsync(responseMessage).ConfigureAwait(false);
                    return new DocumentServiceResponse(
                        body: contentStream,
                        headers: headers,
                        statusCode: responseMessage.StatusCode,
                        clientSideRequestStatistics: requestStatistics,
                        serializerSettings: serializerSettings);
                }
                else
                {
                    throw await CreateDocumentClientExceptionAsync(responseMessage, requestStatistics).ConfigureAwait(false);
                }
            }
        }

        internal static INameValueCollection ExtractResponseHeaders(HttpResponseMessage responseMessage)
        {
            INameValueCollection headers = new DictionaryNameValueCollection();

            foreach (KeyValuePair<string, IEnumerable<string>> headerPair in responseMessage.Headers)
            {
                if (string.Compare(headerPair.Key, HttpConstants.HttpHeaders.OwnerFullName, StringComparison.Ordinal) == 0)
                {
                    foreach (string val in headerPair.Value)
                    {
                        headers.Add(headerPair.Key, Uri.UnescapeDataString(val));
                    }
                }
                else
                {
                    foreach (string val in headerPair.Value)
                    {
                        headers.Add(headerPair.Key, val);
                    }
                }
            }

            if (responseMessage.Content != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> headerPair in responseMessage.Content.Headers)
                {
                    if (string.Compare(headerPair.Key, HttpConstants.HttpHeaders.OwnerFullName, StringComparison.Ordinal) == 0)
                    {
                        foreach (string val in headerPair.Value)
                        {
                            headers.Add(headerPair.Key, Uri.UnescapeDataString(val));
                        }
                    }
                    else
                    {
                        foreach (string val in headerPair.Value)
                        {
                            headers.Add(headerPair.Key, val);
                        }
                    }
                }
            }

            return headers;
        }

        internal static async Task<DocumentClientException> CreateDocumentClientExceptionAsync(
            HttpResponseMessage responseMessage,
            IClientSideRequestStatistics requestStatistics)
        {
            bool isNameBased = false;
            bool isFeed = false;
            string resourceTypeString;
            string resourceIdOrFullName;

            string resourceLink = responseMessage.RequestMessage.RequestUri.LocalPath;
            if (!PathsHelper.TryParsePathSegments(resourceLink, out isFeed, out resourceTypeString, out resourceIdOrFullName, out isNameBased))
            {
                // if resourceLink is invalid - we will not set resourceAddress in exception.
            }

            // If service rejects the initial payload like header is to large it will return an HTML error instead of JSON.
            if (string.Equals(responseMessage.Content?.Headers?.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                Stream readStream = await responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
                Error error = Documents.Resource.LoadFrom<Error>(readStream);
                return new DocumentClientException(
                    error,
                    responseMessage.Headers,
                    responseMessage.StatusCode)
                {
                    StatusDescription = responseMessage.ReasonPhrase,
                    ResourceAddress = resourceIdOrFullName,
                    RequestStatistics = requestStatistics
                };
            }
            else
            {
                StringBuilder context = new StringBuilder();
                context.AppendLine(await responseMessage.Content.ReadAsStringAsync().ConfigureAwait(false));

                HttpRequestMessage requestMessage = responseMessage.RequestMessage;
                if (requestMessage != null)
                {
                    context.AppendLine($"RequestUri: {requestMessage.RequestUri.ToString()};");
                    context.AppendLine($"RequestMethod: {requestMessage.Method.Method};");

                    if (requestMessage.Headers != null)
                    {
                        foreach (KeyValuePair<string, IEnumerable<string>> header in requestMessage.Headers)
                        {
                            context.AppendLine($"Header: {header.Key} Length: {string.Join(",", header.Value).Length};");
                        }
                    }
                }

                return new DocumentClientException(
                    message: context.ToString(),
                    innerException: null,
                    responseHeaders: responseMessage.Headers,
                    statusCode: responseMessage.StatusCode,
                    requestUri: responseMessage.RequestMessage.RequestUri)
                {
                    StatusDescription = responseMessage.ReasonPhrase,
                    ResourceAddress = resourceIdOrFullName,
                    RequestStatistics = requestStatistics
                };
            }
        }

        internal static bool IsAllowedRequestHeader(string headerName)
        {
            if (!headerName.StartsWith("x-ms", StringComparison.OrdinalIgnoreCase))
            {
                switch (headerName)
                {
                    //Just flow the header which are settable at RequestMessage level and the one we care.
                    case HttpConstants.HttpHeaders.Authorization:
                    case HttpConstants.HttpHeaders.Accept:
                    case HttpConstants.HttpHeaders.ContentType:
                    case HttpConstants.HttpHeaders.Host:
                    case HttpConstants.HttpHeaders.IfMatch:
                    case HttpConstants.HttpHeaders.IfModifiedSince:
                    case HttpConstants.HttpHeaders.IfNoneMatch:
                    case HttpConstants.HttpHeaders.IfRange:
                    case HttpConstants.HttpHeaders.IfUnmodifiedSince:
                    case HttpConstants.HttpHeaders.UserAgent:
                    case HttpConstants.HttpHeaders.Prefer:
                    case HttpConstants.HttpHeaders.Query:
                    case HttpConstants.HttpHeaders.A_IM:
                        return true;

                    default:
                        return false;
                }
            }
            return true;
        }

        private static async Task<Stream> BufferContentIfAvailableAsync(HttpResponseMessage responseMessage)
        {
            if (responseMessage.Content == null)
            {
                return null;
            }

            MemoryStream bufferedStream = new MemoryStream();
            await responseMessage.Content.CopyToAsync(bufferedStream).ConfigureAwait(false);
            bufferedStream.Position = 0;
            return bufferedStream;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposable object returned by method")]
        private async ValueTask<HttpRequestMessage> PrepareRequestMessageAsync(
            DocumentServiceRequest request,
            Uri physicalAddress)
        {
            HttpMethod httpMethod = HttpMethod.Head;
            if (request.OperationType == OperationType.Create ||
                request.OperationType == OperationType.Upsert ||
                request.OperationType == OperationType.Query ||
                request.OperationType == OperationType.SqlQuery ||
                request.OperationType == OperationType.Batch ||
                request.OperationType == OperationType.ExecuteJavaScript ||
                request.OperationType == OperationType.QueryPlan)
            {
                httpMethod = HttpMethod.Post;
            }
            else if (request.OperationType == OperationType.Read
                || request.OperationType == OperationType.ReadFeed)
            {
                httpMethod = HttpMethod.Get;
            }
            else if (request.OperationType == OperationType.Replace)
            {
                httpMethod = HttpMethod.Put;
            }
            else if (request.OperationType == OperationType.Delete)
            {
                httpMethod = HttpMethod.Delete;
            }
            else if (request.OperationType == OperationType.Patch)
            {
                // There isn't support for PATCH method in .NetStandard 2.0
                httpMethod = httpPatchMethod;
            }
            else
            {
                throw new NotImplementedException();
            }

            HttpRequestMessage requestMessage = new HttpRequestMessage(httpMethod, physicalAddress);

            // The StreamContent created below will own and dispose its underlying stream, but we may need to reuse the stream on the 
            // DocumentServiceRequest for future requests. Hence we need to clone without incurring copy cost, so that when
            // HttpRequestMessage -> StreamContent -> MemoryStream all get disposed, the original stream will be left open.
            if (request.Body != null)
            {
                await request.EnsureBufferedBodyAsync().ConfigureAwait(false);
                MemoryStream clonedStream = new MemoryStream();
                // WriteTo doesn't use and update Position of source stream. No point in setting/restoring it.
                request.CloneableBody.WriteTo(clonedStream);
                clonedStream.Position = 0;

                requestMessage.Content = new StreamContent(clonedStream);
            }

            if (request.Headers != null)
            {
                foreach (string key in request.Headers)
                {
                    if (IsAllowedRequestHeader(key))
                    {
                        if (key.Equals(HttpConstants.HttpHeaders.ContentType, StringComparison.OrdinalIgnoreCase))
                        {
                            requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue(request.Headers[key]);
                        }
                        else
                        {
                            requestMessage.Headers.TryAddWithoutValidation(key, request.Headers[key]);
                        }
                    }
                }
            }

            // add activityId
            Guid activityId = Trace.CorrelationManager.ActivityId;
            Debug.Assert(activityId != Guid.Empty);
            requestMessage.Headers.Add(HttpConstants.HttpHeaders.ActivityId, activityId.ToString());

            return requestMessage;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposable object returned by method")]
        private Task<HttpResponseMessage> InvokeClientAsync(
           DocumentServiceRequest request,
           ResourceType resourceType,
           Uri physicalAddress,
           CancellationToken cancellationToken)
        {
            CosmosDiagnosticsContext diagnosticsContext = null;
            if (request?.RequestContext?.ClientRequestStatistics is CosmosClientSideRequestStatistics cosmosClientSideRequestStatistics)
            {
                diagnosticsContext = cosmosClientSideRequestStatistics.DiagnosticsContext;
            }

            return this.httpClient.SendHttpAsync(
                () => this.PrepareRequestMessageAsync(request, physicalAddress),
                resourceType,
                diagnosticsContext,
                cancellationToken);
        }
    }
}
