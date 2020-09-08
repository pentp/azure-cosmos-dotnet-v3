﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Resource.CosmosExceptions;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal sealed class CosmosHttpClientCore : CosmosHttpClient
    {
        private static readonly TimeSpan GatewayRequestTimeout = TimeSpan.FromSeconds(65);
        private readonly HttpClient httpClient;
        private readonly ICommunicationEventSource eventSource;

        private bool disposedValue;

        private CosmosHttpClientCore(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            ICommunicationEventSource eventSource)
        {
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
            this.HttpMessageHandler = httpMessageHandler;
        }

        public override HttpMessageHandler HttpMessageHandler { get; }

        public static CosmosHttpClient CreateWithConnectionPolicy(
            ApiType apiType,
            ICommunicationEventSource eventSource,
            ConnectionPolicy connectionPolicy,
            HttpMessageHandler httpMessageHandler,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            if (connectionPolicy == null)
            {
                throw new ArgumentNullException(nameof(connectionPolicy));
            }

            Func<HttpClient> httpClientFactory = connectionPolicy.HttpClientFactory;
            if (httpClientFactory != null)
            {
                if (sendingRequestEventArgs != null &&
                    receivedResponseEventArgs != null)
                {
                    throw new InvalidOperationException($"{nameof(connectionPolicy.HttpClientFactory)} can not be set at the same time as {nameof(sendingRequestEventArgs)} or {nameof(ReceivedResponseEventArgs)}");
                }

                HttpClient userHttpClient = httpClientFactory.Invoke() ?? throw new ArgumentNullException($"{nameof(httpClientFactory)} returned null. {nameof(httpClientFactory)} must return a HttpClient instance.");
                return CosmosHttpClientCore.CreateHelper(
                    httpClient: userHttpClient,
                    httpMessageHandler: httpMessageHandler,
                    requestTimeout: connectionPolicy.RequestTimeout,
                    userAgentContainer: connectionPolicy.UserAgentContainer,
                    apiType: apiType,
                    eventSource: eventSource);
            }

            if (httpMessageHandler == null)
            {
                httpMessageHandler = CosmosHttpClientCore.CreateHttpClientHandler(
                        gatewayModeMaxConnectionLimit: connectionPolicy.MaxConnectionLimit,
                        webProxy: null);
            }

            if (sendingRequestEventArgs != null ||
                receivedResponseEventArgs != null)
            {
                httpMessageHandler = CosmosHttpClientCore.CreateHttpMessageHandler(
                    httpMessageHandler,
                    sendingRequestEventArgs,
                    receivedResponseEventArgs);
            }

            HttpClient httpClient = new HttpClient(httpMessageHandler);

            return CosmosHttpClientCore.CreateHelper(
                httpClient: httpClient,
                httpMessageHandler: httpMessageHandler,
                requestTimeout: connectionPolicy.RequestTimeout,
                userAgentContainer: connectionPolicy.UserAgentContainer,
                apiType: apiType,
                eventSource: eventSource);
        }

        public static HttpMessageHandler CreateHttpClientHandler(int gatewayModeMaxConnectionLimit, IWebProxy webProxy)
        {
            // https://docs.microsoft.com/en-us/archive/blogs/timomta/controlling-the-number-of-outgoing-connections-from-httpclient-net-core-or-full-framework
            return new HttpClientHandler
            {
                Proxy = webProxy,
                MaxConnectionsPerServer = gatewayModeMaxConnectionLimit
            };
        }

        private static HttpMessageHandler CreateHttpMessageHandler(
            HttpMessageHandler innerHandler,
            EventHandler<SendingRequestEventArgs> sendingRequestEventArgs,
            EventHandler<ReceivedResponseEventArgs> receivedResponseEventArgs)
        {
            return new HttpRequestMessageHandler(
                sendingRequestEventArgs,
                receivedResponseEventArgs,
                innerHandler);
        }

        private static CosmosHttpClient CreateHelper(
            HttpClient httpClient,
            HttpMessageHandler httpMessageHandler,
            TimeSpan requestTimeout,
            UserAgentContainer userAgentContainer,
            ApiType apiType,
            ICommunicationEventSource eventSource)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            httpClient.Timeout = requestTimeout > CosmosHttpClientCore.GatewayRequestTimeout
                ? requestTimeout
                : CosmosHttpClientCore.GatewayRequestTimeout;
            httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            httpClient.AddUserAgentHeader(userAgentContainer);
            httpClient.AddApiTypeHeader(apiType);

            // Set requested API version header that can be used for
            // version enforcement.
            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Version,
                HttpConstants.Versions.CurrentVersion);

            httpClient.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Accept, RuntimeConstants.MediaTypes.Json);

            return new CosmosHttpClientCore(
                httpClient,
                httpMessageHandler,
                eventSource);
        }

        public override Task<HttpResponseMessage> GetAsync(
            Uri uri,
            INameValueCollection additionalHeaders,
            ResourceType resourceType,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            // GetAsync doesn't let clients to pass in additional headers. So, we are
            // internally using SendAsync and add the additional headers to requestMessage. 
            ValueTask<HttpRequestMessage> CreateRequestMessage()
            {
                HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
                if (additionalHeaders != null)
                {
                    foreach (string header in additionalHeaders)
                    {
                        if (GatewayStoreClient.IsAllowedRequestHeader(header))
                        {
                            requestMessage.Headers.TryAddWithoutValidation(header, additionalHeaders[header]);
                        }
                    }
                }

                return new ValueTask<HttpRequestMessage>(requestMessage);
            }

            return this.SendHttpAsync(
                CreateRequestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                resourceType,
                diagnosticsContext,
                cancellationToken);
        }

        public override Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessageAsync,
            ResourceType resourceType,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            return this.SendHttpAsync(
                createRequestMessageAsync,
                HttpCompletionOption.ResponseContentRead,
                resourceType,
                diagnosticsContext,
                cancellationToken);
        }

        private Task<HttpResponseMessage> SendHttpAsync(
            Func<ValueTask<HttpRequestMessage>> createRequestMessageAsync,
            HttpCompletionOption httpCompletionOption,
            ResourceType resourceType,
            CosmosDiagnosticsContext diagnosticsContext,
            CancellationToken cancellationToken)
        {
            diagnosticsContext ??= new CosmosDiagnosticsContextCore();
            HttpRequestMessage requestMessage = null;
            Func<Task<HttpResponseMessage>> funcDelegate = async () =>
            {
                using (diagnosticsContext.CreateScope(nameof(CosmosHttpClientCore.SendHttpAsync)))
                {
                    using (requestMessage = await createRequestMessageAsync().ConfigureAwait(false))
                    {
                        DateTime sendTimeUtc = DateTime.UtcNow;
                        Guid localGuid = Guid.NewGuid(); // For correlating HttpRequest and HttpResponse Traces

                        Guid requestedActivityId = Trace.CorrelationManager.ActivityId;
                        this.eventSource.Request(
                            requestedActivityId,
                            localGuid,
                            requestMessage.RequestUri.ToString(),
                            resourceType.ToResourceTypeString(),
                            requestMessage.Headers);

                        HttpResponseMessage responseMessage = await this.httpClient.SendAsync(
                                requestMessage,
                                httpCompletionOption,
                                cancellationToken).ConfigureAwait(false);

                        DateTime receivedTimeUtc = DateTime.UtcNow;
                        TimeSpan durationTimeSpan = receivedTimeUtc - sendTimeUtc;

                        Guid activityId = Guid.Empty;
                        if (responseMessage.Headers.TryGetValues(
                            HttpConstants.HttpHeaders.ActivityId,
                            out IEnumerable<string> headerValues) && headerValues.Any())
                        {
                            activityId = new Guid(headerValues.First());
                        }

                        this.eventSource.Response(
                            activityId,
                            localGuid,
                            (short)responseMessage.StatusCode,
                            durationTimeSpan.TotalMilliseconds,
                            responseMessage.Headers);

                        return responseMessage;
                    }
                }
            };

            HttpRequestMessage GetHttpRequestMessage() => requestMessage;
            return BackoffRetryUtility<HttpResponseMessage>.ExecuteAsync(
                callbackMethod: funcDelegate,
                retryPolicy: new TransientHttpClientRetryPolicy(
                    getHttpRequestMessage: GetHttpRequestMessage,
                    gatewayRequestTimeout: this.httpClient.Timeout,
                    diagnosticsContext: diagnosticsContext),
                cancellationToken: cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.httpClient.Dispose();
                }

                this.disposedValue = true;
            }
        }

        public override void Dispose()
        {
            this.Dispose(true);
        }

        private class HttpRequestMessageHandler : DelegatingHandler
        {
            private readonly EventHandler<SendingRequestEventArgs> sendingRequest;
            private readonly EventHandler<ReceivedResponseEventArgs> receivedResponse;

            public HttpRequestMessageHandler(
                EventHandler<SendingRequestEventArgs> sendingRequest,
                EventHandler<ReceivedResponseEventArgs> receivedResponse,
                HttpMessageHandler innerHandler)
            {
                this.sendingRequest = sendingRequest;
                this.receivedResponse = receivedResponse;

                this.InnerHandler = innerHandler ?? throw new ArgumentNullException(
                    $"innerHandler is null. This required for .NET core to limit the http connection. See {nameof(CreateHttpClientHandler)} ");
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.sendingRequest?.Invoke(this, new SendingRequestEventArgs(request));
                HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                this.receivedResponse?.Invoke(this, new ReceivedResponseEventArgs(request, response));
                return response;
            }
        }
    }
}
