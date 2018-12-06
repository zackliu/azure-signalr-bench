﻿using Common;
using Microsoft.AspNetCore.SignalR.Client;
using Plugin.Base;
using Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods.Statistics;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Plugin.Microsoft.Azure.SignalR.Benchmark.SignalREnums;

namespace Plugin.Microsoft.Azure.SignalR.Benchmark.SlaveMethods
{
    public class CreateConnection : ISlaveMethod
    {
        public Task<IDictionary<string, object>> Do(IDictionary<string, object> stepParameters, IDictionary<string, object> pluginParameters)
        {
            try
            {
                Log.Information($"Create connections...");

                return SignalRUtils.SlaveCreateConnection(stepParameters, pluginParameters, ClientType.AspNetCore);
            }
            catch (Exception ex)
            {
                var message = $"Fail to create connections: {ex}";
                Log.Error(message);
                throw;
            }
        }
    }
}
