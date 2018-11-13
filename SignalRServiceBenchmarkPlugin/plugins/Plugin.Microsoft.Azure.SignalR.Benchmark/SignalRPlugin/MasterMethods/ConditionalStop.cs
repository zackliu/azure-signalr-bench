﻿using Common;
using Plugin.Base;
using Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods.Statistics;
using Rpc.Service;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugin.Microsoft.Azure.SignalR.Benchmark.MasterMethods
{
    public class ConditionalStop: IMasterMethod
    {
        public async Task Do(IDictionary<string, object> stepParameters, IDictionary<string, object> pluginParameters, IList<IRpcClient> clients)
        {
            Log.Information($"Start to conditional stop...");

            // Get parameters
            stepParameters.TryGetTypedValue(SignalRConstants.Type, out string type, Convert.ToString);
            stepParameters.TryGetTypedValue(SignalRConstants.CriteriaMaxFailConnectionPercentage, out double criteriaMaxFailConnectionPercentage, Convert.ToDouble);
            stepParameters.TryGetTypedValue(SignalRConstants.CriteriaMaxFailConnectionAmount, out int criteriaMaxFailConnectionAmount, Convert.ToInt32);
            stepParameters.TryGetTypedValue(SignalRConstants.CriteriaMaxFailSendingPercentage, out double criteriaMaxFailSendingPercentage, Convert.ToDouble);

            // Get context
            pluginParameters.TryGetTypedValue($"{SignalRConstants.LatencyStep}.{type}", out long latencyStep, Convert.ToInt64);
            pluginParameters.TryGetTypedValue($"{SignalRConstants.LatencyMax}.{type}", out long latencyMax, Convert.ToInt64);

            var results = await Task.WhenAll(from client in clients
                                             select client.QueryAsync(stepParameters));

            // Merge statistics
            var merged = SignalRUtils.MergeStatistics(results, type, latencyMax, latencyStep);

            merged.TryGetTypedValue(SignalRConstants.StatisticsConnectionConnectSuccess, out int connectionSuccess, Convert.ToInt32);
            merged.TryGetTypedValue(SignalRConstants.StatisticsConnectionConnectFail, out int connectionFail, Convert.ToInt32);

            var connectionTotal = connectionSuccess + connectionFail;
            var connectionFailPercentage = (double)connectionFail / connectionTotal;
            var largeLatencyPercentage = GetLargeLatencyPercentage(merged);
            if (connectionFailPercentage > criteriaMaxFailConnectionPercentage) throw new Exception($"Connection fail percentage {connectionFailPercentage * 100}% is greater than criteria {criteriaMaxFailConnectionPercentage * 100}%");
            if (connectionFail > criteriaMaxFailConnectionAmount) throw new Exception($"Connection fail amount {connectionFail} is greater than {criteriaMaxFailConnectionAmount}");
        }

        private double GetLargeLatencyPercentage(IDictionary<string, int> data)
        {
            var largeLatencyMessageCount = data[SignalRUtils.MessageGreaterOrEqaulTo(StatisticsCollector.LatencyMax)];
            var receivedMessageCount = data[SignalRConstants.StatisticsMessageReceived];
            return (double)largeLatencyMessageCount / receivedMessageCount;
        }
    }
}