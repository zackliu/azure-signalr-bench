﻿using Common;
using Microsoft.AspNetCore.SignalR.Client;
using Plugin.Base;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods
{
    public class StartConnection : ISlaveMethod
    {
        public async Task<IDictionary<string, object>> Do(IDictionary<string, object> stepParameters, IDictionary<string, object> pluginParameters)
        {
            try
            {
                Log.Information($"Start connections...");

                // Get parameters
                stepParameters.TryGetTypedValue(SignalRConstants.ConcurrentConnection, out int concurrentConnection, Convert.ToInt32);
                stepParameters.TryGetTypedValue(SignalRConstants.Type, out string type, Convert.ToString);
                pluginParameters.TryGetTypedValue($"{SignalRConstants.ConnectionStore}.{type}", out IList<HubConnection> connections, (obj) => (IList<HubConnection>)obj);

                await Task.WhenAll(Util.BatchProcess(connections, StartConnect, concurrentConnection));

                return null;
            }
            catch (Exception ex)
            {
                var message = $"Fail to start connections: {ex}";
                Log.Error(message);
                throw;
            }
        }

        private async Task StartConnect(HubConnection connection)
        {
            if (connection == null) return;
            try
            {
                await connection.StartAsync();
            }
            catch (Exception ex)
            {
                var message = $"Fail to start connection: {ex}";
                Log.Error(message);
                throw;
            }
        }
    }
}
