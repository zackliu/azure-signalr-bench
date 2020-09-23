﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Azure.SignalRBench.Common
{
    /// <summary>
    /// Get HttpTransportType by <code>(HttpTransportType)((int)protocol & 0xF)</code>
    /// Get TransferFormat by <code>(TransferFormat)((int)protocol >> 4)</code>
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SignalRProtocol
    {
        /// <summary>
        /// HttpTransportType.WebSockets = 1, TransferFormat.Binary = 1
        /// </summary>
        WebSocketsWithMessagePack = 1 | 0x10,
        /// <summary>
        /// HttpTransportType.WebSockets = 1, TransferFormat.Text = 2
        /// </summary>
        WebSocketsWithJson = 1 | 0x20,
        /// <summary>
        /// HttpTransportType.ServerSentEvents = 2, TransferFormat.Text = 2
        /// </summary>
        ServerSideEventsWithJson = 2 | 0x20,
        /// <summary>
        /// HttpTransportType.LongPolling = 4, TransferFormat.Binary = 1
        /// </summary>
        LongPollingWithMessagePack = 4 | 0x10,
        /// <summary>
        /// HttpTransportType.LongPolling = 4, TransferFormat.Text = 2
        /// </summary>
        LongPollingWithJson = 4 | 0x20,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ClientBehavior
    {
        Listen,
        Echo,
        Broadcast,
        GroupBroadcast,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum LatencyClass
    {
        LessThan50ms,
        LessThan100ms,
        LessThan200ms,
        LessThan500ms,
        LessThan1s,
        LessThan2s,
        LessThan5s,
        MoreThan5s,
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TestCategory
    {
        AspnetCore,
        AspnetCoreServerless,
        Aspnet,
    }
}