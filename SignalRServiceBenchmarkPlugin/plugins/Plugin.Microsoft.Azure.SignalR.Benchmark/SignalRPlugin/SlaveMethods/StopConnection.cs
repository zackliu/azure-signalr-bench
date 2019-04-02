﻿using Common;
using Plugin.Base;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods
{
    public class StopConnection: ISlaveMethod
    {
        public async Task<IDictionary<string, object>> Do(IDictionary<string, object> stepParameters, IDictionary<string, object> pluginParameters)
        {
            try
            {
                Log.Information($"Stop connections...");

                // Get parameters
                stepParameters.TryGetTypedValue(SignalRConstants.Type, out string type, Convert.ToString);
                pluginParameters.TryGetTypedValue($"{SignalRConstants.ConnectionStore}.{type}",
                    out IList<IHubConnectionAdapter> connections, (obj) => (IList<IHubConnectionAdapter>) obj);

                // Stop connections
                await Task.WhenAll(from connection in connections
                                    select connection.StopAsync());

                return null;
            }
            catch (Exception ex)
            {
                var message = $"Fail to stop connections: {ex}";
                Log.Error(message);
                throw;
            }
        }
    }
}
