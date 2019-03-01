﻿using Common;
using Plugin.Base;
using Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods.Statistics;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods
{
    class RestBroadcast : BaseContinuousSendMethod, ISlaveMethod
    {
        public async Task<IDictionary<string, object>> Do(IDictionary<string, object> stepParameters, IDictionary<string, object> pluginParameters)
        {
            try
            {
                Log.Information($"{GetType().Name}...");

                // Get parameters
                stepParameters.TryGetTypedValue(SignalRConstants.Type, out string type, Convert.ToString);
                stepParameters.TryGetTypedValue(SignalRConstants.RemainderBegin, out int remainderBegin, Convert.ToInt32);
                stepParameters.TryGetTypedValue(SignalRConstants.RemainderEnd, out int remainderEnd, Convert.ToInt32);
                stepParameters.TryGetTypedValue(SignalRConstants.Modulo, out int modulo, Convert.ToInt32);
                stepParameters.TryGetTypedValue(SignalRConstants.Duration, out long duration, Convert.ToInt64);
                stepParameters.TryGetTypedValue(SignalRConstants.Interval, out long interval, Convert.ToInt64);
                stepParameters.TryGetTypedValue(SignalRConstants.MessageSize, out int messageSize, Convert.ToInt32);

                pluginParameters.TryGetTypedValue($"{SignalRConstants.StatisticsStore}.{type}",
                    out StatisticsCollector statisticsCollector, obj => (StatisticsCollector)obj);
                pluginParameters.TryGetTypedValue($"{SignalRConstants.ConnectionIndex}.{type}",
                    out List<int> connectionIndex, (obj) => (List<int>)obj);
                // The connection string is saved in context after finishing creating connection
                pluginParameters.TryGetTypedValue($"{SignalRConstants.ConnectionString}.{type}",
                    out string connectionString, Convert.ToString);
                // Generate necessary data
                var data = new Dictionary<string, object>
                {
                    { SignalRConstants.MessageBlob, SignalRUtils.GenerateRandomData(messageSize) } // message payload
                };

                // Reset counters
                UpdateStatistics(statisticsCollector, remainderEnd);

                // Send messages
                await Task.WhenAll(from i in Enumerable.Range(0, connectionIndex.Count)
                                   where connectionIndex[i] % modulo >= remainderBegin && connectionIndex[i] % modulo < remainderEnd
                                   let restApiClient = new RestApiProvider(connectionString, SignalRConstants.DefaultRestHubName)
                                   let userId = SignalRUtils.GenClientUserIdFromConnectionIndex(connectionIndex[i])
                                   select ContinuousSend((RestApiClient: restApiClient,
                                                          StatisticsCollector: statisticsCollector),
                                                          data,
                                                          BroadcastMsg,
                                                          TimeSpan.FromMilliseconds(duration),
                                                          TimeSpan.FromMilliseconds(interval),
                                                          TimeSpan.FromMilliseconds(1),
                                                          TimeSpan.FromMilliseconds(interval)));
                return null;
            }
            catch (Exception ex)
            {
                var message = $"Fail to {GetType().Name}: {ex}";
                Log.Error(message);
                throw;
            }
        }

        private async Task BroadcastMsg(
            (RestApiProvider RestApiProvider,
             StatisticsCollector StatisticsCollector) package,
             IDictionary<string, object> data)
        {
            var payload = GenPayload(data);
            await package.RestApiProvider.SendToAll(
                SignalRConstants.RecordLatencyCallbackName,
                new[] { payload });
            SignalRUtils.RecordSend(payload, package.StatisticsCollector);
        }

        protected override IDictionary<string, object> GenPayload(IDictionary<string, object> data)
        {
            return GenCommonPayload(data);
        }
    }
}