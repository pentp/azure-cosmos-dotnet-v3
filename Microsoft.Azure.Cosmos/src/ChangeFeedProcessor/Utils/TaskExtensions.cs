//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.ChangeFeed.Utils
{
    using System.Threading.Tasks;

    internal static class TaskExtensions
    {
        public static void LogException(this Task task)
        {
#pragma warning disable VSTHRD110 // Observe result of async calls
            task.ContinueWith(t => Extensions.TraceException(t.Exception), default, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
#pragma warning restore VSTHRD110 // Observe result of async calls
        }
    }
}