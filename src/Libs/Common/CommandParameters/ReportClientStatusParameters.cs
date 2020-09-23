﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Azure.SignalRBench.Common
{
    public class ReportClientStatusParameters
    {
        public int TotalReconnectCount { get; set; }
        public int ConnectedCount { get; set; }
        public int ReconnectingCount { get; set; }
        public int MessageSent { get; set; }
        public int MessageRecieved { get; set; }
        public Dictionary<LatencyClass, int> Latency { get; set; } = new Dictionary<LatencyClass, int>();
    }
}