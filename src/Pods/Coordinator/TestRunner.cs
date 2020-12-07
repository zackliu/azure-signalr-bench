﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.SignalRBench.Common;
using Azure.SignalRBench.Coordinator.Entities;
using Azure.SignalRBench.Messages;
using Azure.SignalRBench.Storage;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Azure.SignalRBench.Coordinator
{
    public class TestRunner
    {
        private const double MaxClientCountInPod = 5000;

        private readonly Dictionary<string, SetClientRangeParameters> _clients =
            new Dictionary<string, SetClientRangeParameters>();

        private readonly ConcurrentDictionary<string, ReportClientStatusParameters> _clientStatus =
            new ConcurrentDictionary<string, ReportClientStatusParameters>();

        private readonly List<ReportClientStatusParameters>
            _clientStatusList = new List<ReportClientStatusParameters>();

        private readonly List<string> _serverPods = new List<string>();
        private readonly ILogger<TestRunner> _logger;
        private ITableAccessor<TestStatusEntity> _testStatusAccessor;

        private string _url = "http://localhost:8080/";
        private TestStatusEntity _testStatusEntity;

        public TestRunner(
            TestJob job,
            string podName,
            string redisConnectionString,
            int nodePoolIndex,
            IAksProvider aksProvider,
            IK8sProvider k8sProvider,
            ISignalRProvider signalRProvider,
            IPerfStorage perfStorage,
            string defaultLocation,
            ILogger<TestRunner> logger)
        {
            Job = job;
            PodName = podName;
            RedisConnectionString = redisConnectionString;
            NodePoolIndex = nodePoolIndex;
            AksProvider = aksProvider;
            K8sProvider = k8sProvider;
            SignalRProvider = signalRProvider;
            PerfStorage = perfStorage;
            DefaultLocation = defaultLocation;
            _logger = logger;
        }

        public TestJob Job { get; }

        public string PodName { get; set; }

        public string RedisConnectionString { get; set; }

        public int NodePoolIndex { get; set; }

        public IAksProvider AksProvider { get; }

        public IK8sProvider K8sProvider { get; }

        public ISignalRProvider SignalRProvider { get; }

        public IPerfStorage PerfStorage { get; }

        public string DefaultLocation { get; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Job.ServiceSetting.Length == 0)
            {
                _logger.LogWarning("Test job {testId}: No service configuration.", Job.TestId);
                return;
            }

            _testStatusAccessor = await PerfStorage.GetTableAsync<TestStatusEntity>(PerfConstants.TableNames.TestStatus);
            int idx = Job.TestId.LastIndexOf('-');
            _testStatusEntity = await _testStatusAccessor.GetAsync(Job.TestId.Substring(0,idx), Job.TestId.Substring(idx+1));
            var clientAgentCount = Job.ScenarioSetting.TotalConnectionCount;
            var clientPodCount = (int) Math.Ceiling(clientAgentCount / MaxClientCountInPod);
            _logger.LogInformation("Test job {testId}: Client pods count: {count}.", Job.TestId, clientPodCount);
            var serverPodCount = Job.ServerSetting.ServerCount;
            _logger.LogInformation("Test job {testId}: Server pods count: {count}.", Job.TestId, serverPodCount);
            var nodeCount = clientPodCount + serverPodCount;
            _logger.LogInformation("Test job {testId}: Node count: {count}.", Job.TestId, nodeCount);
            var asrsConnectionStringsTask = PrepairAsrsInstancesAsync(cancellationToken);
            await UpdateTestStatus("Creating vms");
            await AksProvider.EnsureNodeCountAsync(NodePoolIndex, nodeCount, cancellationToken);
            using var messageClient = await MessageClient.ConnectAsync(RedisConnectionString, Job.TestId, PodName);
            await messageClient.WithHandlers(MessageHandler.CreateCommandHandler(Roles.Coordinator,
                Commands.Coordinator.ReportClientStatus, CollectClientStatus));
            try
            {
                await UpdateTestStatus("Creating pods");
                await CreatePodsAsync(await asrsConnectionStringsTask, clientAgentCount, clientPodCount, serverPodCount,
                    messageClient, cancellationToken);
                await UpdateTestStatus("Starting client connections");
                await StartClientConnectionsAsync(messageClient, cancellationToken);
                int i = 0;
                foreach (var round in Job.ScenarioSetting.Rounds)
                {
                    i++;
                    await UpdateTestStatus($"Testing Round {i}");
                    _clientStatus.Clear();
                    await SetScenarioAsync(messageClient, round, cancellationToken);
                    await StartScenarioAsync(messageClient, cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(round.DurationInSeconds), cancellationToken);
                    await StopScenarioAsync(messageClient, cancellationToken);
                    await UpdateTestReports();
                }

                await UpdateTestStatus($"Stopping client connections");
                await StopClientConnectionsAsync(messageClient, cancellationToken);
                await UpdateTestStatus($"Test Finishes");
            }
            catch (Exception e)
            {
                await UpdateTestStatus("Testing Round failed ", false,e);
            }
            finally
            {
                try
                {
                    _logger.LogInformation("Test job {testId}: Removing client pods.", Job.TestId);
                    await K8sProvider.DeleteClientPodsAsync(Job.TestId, NodePoolIndex);
                    _logger.LogInformation("Test job {testId}: Removing server pods.", Job.TestId);
                    await K8sProvider.DeleteServerPodsAsync(Job.TestId, NodePoolIndex);
                    _logger.LogInformation("Test job {testId}: Removing service instances.", Job.TestId);
                    await Task.WhenAll(
                        from ss in Job.ServiceSetting
                        where ss.AsrsConnectionString == null
                        group ss by ss.Location ?? DefaultLocation
                        into g
                        select SignalRProvider.DeleteResourceGroupAsync(Job.TestId, g.Key));
                }
                catch (Exception ignore)
                {
                  await  UpdateTestStatus("Clean up failed", false, ignore);
                }
                
            }
        }

        private async Task UpdateTestStatus(string currentStatus, bool healthy = true,Exception? e=default)
        {
            _testStatusEntity.Status = currentStatus;
            _testStatusEntity.Healthy = healthy;
            if (healthy)
            {
                _logger.LogInformation(currentStatus);
            }
            else
            {
                _logger.LogError(e,currentStatus);
                if(e!=null)
                  _testStatusEntity.ErrorInfo+="   \n  \n     "+ e;
            }
            await _testStatusAccessor.UpdateAsync(_testStatusEntity);
        }

        private async Task UpdateTestReports()
        {
            var combindClientStatus = new ReportClientStatusParameters()
            {
                ConnectedCount = 0,
                Latency = new Dictionary<LatencyClass, int>(),
                MessageRecieved = 0,
                MessageSent = 0,
                ReconnectingCount = 0,
                TotalReconnectCount = 0
            };
            foreach (var v in _clientStatus.Values)
            {
                combindClientStatus.ConnectedCount += v.ConnectedCount;
                combindClientStatus.MessageRecieved += v.MessageRecieved;
                combindClientStatus.MessageSent += v.MessageSent;
                combindClientStatus.ReconnectingCount += v.ReconnectingCount;
                combindClientStatus.TotalReconnectCount += v.TotalReconnectCount;
                foreach (var kv in v.Latency)
                {
                    if (!combindClientStatus.Latency.ContainsKey(kv.Key))
                        combindClientStatus.Latency[kv.Key] = 0;
                    combindClientStatus.Latency[kv.Key] = combindClientStatus.Latency[kv.Key] + kv.Value;
                }
            }

            _clientStatusList.Add(combindClientStatus);
            _testStatusEntity.Report = JsonConvert.SerializeObject(_clientStatusList);
            await _testStatusAccessor.UpdateAsync(_testStatusEntity);
        }

        private async Task<string[]> PrepairAsrsInstancesAsync(CancellationToken cancellationToken)
        {
            var asrsConnectionStrings = new string[Job.ServiceSetting.Length];
            for (int i = 0; i < Job.ServiceSetting.Length; i++)
            {
                var ss = Job.ServiceSetting[i];
                if (ss.AsrsConnectionString == null)
                {
                    asrsConnectionStrings[i] =
                        await CreateAsrsAsync(ss, Job.TestId + '-' + i.ToString(), cancellationToken);
                }
                else
                {
                    asrsConnectionStrings[i] = ss.AsrsConnectionString;
                }
            }

            return asrsConnectionStrings;
        }

        private async Task<string> CreateAsrsAsync(ServiceSetting ss, string name, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Test job {testId}: Creating SignalR service instance.", Job.TestId);
            await SignalRProvider.CreateResourceGroupAsync(Job.TestId, ss.Location ?? DefaultLocation);
            await SignalRProvider.CreateInstanceAsync(
                Job.TestId,
                name,
                ss.Location ?? DefaultLocation,
                ss.Tier ?? "Standard",
                ss.Size ?? 1,
                Job.TestMethod.GetServiceMode(),
                cancellationToken);
            _logger.LogInformation("Test job {testId}: SignalR service instance created.", Job.TestId);
            _logger.LogInformation("Test job {testId}: Retrieving SignalR service connection string.", Job.TestId);
            var result =
                await SignalRProvider.GetKeyAsync(Job.TestId, name, ss.Location ?? DefaultLocation, cancellationToken);
            _logger.LogInformation("Test job {testId}: SignalR service connection string retrieved.", Job.TestId);
            return result;
        }

        private Task CollectClientStatus(CommandMessage msg)
        {
            var p = msg.Parameters.ToObject<ReportClientStatusParameters>();
            _clientStatus[msg.Sender] = p;
            return Task.CompletedTask;
        }


        private Func<CommandMessage, Task> GetReportReady(
            int clientAgentCount,
            int clientPodCount,
            int serverPodCount,
            out Task clientPodsReady,
            out Task serverPodsReady)
        {
            int clientReadyCount = 0;
            int serverReadyCount = 0;
            var countPerPod = clientAgentCount / clientPodCount;
            var clientPodsReadyTcs = new TaskCompletionSource<object?>();
            var serverPodsReadyTcs = new TaskCompletionSource<object?>();
            clientPodsReady = clientPodsReadyTcs.Task;
            serverPodsReady = serverPodsReadyTcs.Task;
            return m =>
            {
                var p = m.Parameters?.ToObject<ReportReadyParameters>();
                if (p == null)
                {
                    // todo: log.
                    return Task.CompletedTask;
                }

                if (p.Role == Roles.Clients)
                {
                    clientReadyCount++;
                    if (clientReadyCount < clientPodCount)
                    {
                        _clients[m.Sender] =
                            new SetClientRangeParameters
                            {
                                StartId = (clientReadyCount - 1) * countPerPod,
                                Count = countPerPod,
                            };
                    }
                    else if (clientReadyCount == clientPodCount)
                    {
                        _clients[m.Sender] =
                            new SetClientRangeParameters
                            {
                                StartId = (clientReadyCount - 1) * countPerPod,
                                Count = clientAgentCount - (clientReadyCount - 1) * countPerPod,
                            };
                        clientPodsReadyTcs.TrySetResult(null);
                    }
                    else
                    {
                        // todo: log
                        _clients[m.Sender] =
                            new SetClientRangeParameters
                            {
                                StartId = 0,
                                Count = 0,
                            };
                    }
                }
                else if (p.Role == Roles.AppServers)
                {
                    serverReadyCount++;
                    if (serverReadyCount == serverPodCount)
                    {
                        serverPodsReadyTcs.TrySetResult(null);
                    }

                    _serverPods.Add(m.Sender);
                }
                else
                {
                    // todo: log.
                }

                return Task.CompletedTask;
            };
        }

        private async Task CreatePodsAsync(
            string[] asrsConnectionStrings,
            int clientAgentCount,
            int clientPodCount,
            int serverPodCount,
            MessageClient messageClient,
            CancellationToken cancellationToken)
        {
            await messageClient.WithHandlers(
                MessageHandler.CreateCommandHandler(
                    Roles.Coordinator,
                    Commands.Coordinator.ReportReady,
                    GetReportReady(clientAgentCount, clientPodCount, serverPodCount, out var clientPodReady,
                        out var serverPodReady)));

            _logger.LogInformation("Test job {testId}: Creating server pods.", Job.TestId);
            _url =  await K8sProvider.CreateServerPodsAsync(Job.TestId, NodePoolIndex, asrsConnectionStrings,
                serverPodCount, cancellationToken) ;
            _logger.LogInformation("Test job {testId}: Creating client pods.", Job.TestId);
            await K8sProvider.CreateClientPodsAsync(Job.TestId, NodePoolIndex, clientPodCount, cancellationToken);

            await Task.WhenAll(
                Task.Run(async () =>
                {
                    await clientPodReady;
                    _logger.LogInformation("Test job {testId}: Client pods ready.", Job.TestId);
                }),
                Task.Run(async () =>
                {
                    await serverPodReady;
                    _logger.LogInformation("Test job {testId}: Server pods ready.", Job.TestId);
                }));

            var clientCompleteTask = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.SetClientRange,
                cancellationToken);
            await Task.WhenAll(_clients.Select(pair => messageClient.SetClientRangeAsync(pair.Key, pair.Value)));
            await clientCompleteTask;
        }

        private async Task StartClientConnectionsAsync(
            MessageClient messageClient,
            CancellationToken cancellationToken)
        {
            var task = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.StartClientConnections,
                cancellationToken);
            await messageClient.StartClientConnectionsAsync(
                new StartClientConnectionsParameters
                {
                    ClientLifetime = Job.ScenarioSetting.ClientLifetime,
                    TotalCount = Job.ScenarioSetting.TotalConnectionCount,
                    GroupDefinitions = Job.ScenarioSetting.GroupDefinitions,
                    IsAnonymous = Job.ScenarioSetting.IsAnonymous,
                    Protocol = Job.ScenarioSetting.Protocol,
                    Rate = Job.ScenarioSetting.Rate / _clients.Count,
                    Url = _url,
                });
            await task;
            _logger.LogInformation(" start  client connections acked.");
        }

        private async Task SetScenarioAsync(
            MessageClient messageClient,
            RoundSetting round,
            CancellationToken cancellationToken)
        {
            var task = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.SetScenario,
                cancellationToken);
            await messageClient.SetScenarioAsync(
                new SetScenarioParameters
                {
                    Scenarios = Array.ConvertAll(
                        round.ClientSettings,
                        cs =>
                        {
                            var sd = new ScenarioDefinition {ClientBehavior = cs.Behavior};
                            if (cs.Behavior == ClientBehavior.GroupBroadcast)
                            {
                                sd.SetDetail(
                                    new GroupClientBehaviorDetailDefinition
                                    {
                                        Count = cs.Count,
                                        Interval = TimeSpan.FromMilliseconds(cs.IntervalInMilliseconds),
                                        MessageSize = cs.MessageSize,
                                        GroupFamily = cs.GroupFamily ??
                                                      throw new InvalidDataException("Group family is required."),
                                    });
                            }
                            else
                            {
                                sd.SetDetail(
                                    new ClientBehaviorDetailDefinition
                                    {
                                        Count = cs.Count,
                                        Interval = TimeSpan.FromMilliseconds(cs.IntervalInMilliseconds),
                                        MessageSize = cs.MessageSize,
                                    });
                            }

                            return sd;
                        }),
                });
            await task;
            _logger.LogInformation(" set scenario acked.");
        }

        private async Task StartScenarioAsync(
            MessageClient messageClient,
            CancellationToken cancellationToken)
        {
            var task = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.StartScenario,
                cancellationToken);
            await messageClient.StartScenarioAsync(
                new StartScenarioParameters());
            await task;
            _logger.LogInformation(" Start scenario acked.");
        }

        private async Task StopScenarioAsync(
            MessageClient messageClient,
            CancellationToken cancellationToken)
        {
            var task = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.StopScenario,
                cancellationToken);
            await messageClient.StopScenarioAsync(
                new StopScenarioParameters());
            await task;
            _logger.LogInformation(" Stop scenario acked.");
        }

        private async Task StopClientConnectionsAsync(
            MessageClient messageClient,
            CancellationToken cancellationToken)
        {
            var task = await messageClient.GetWhenAllAckAsync(
                _clients.Keys,
                Commands.Clients.StopClientConnections,
                cancellationToken);
            await messageClient.StopClientConnectionsAsync(
                new StopClientConnectionsParameters
                {
                    Rate = Job.ScenarioSetting.Rate / _clients.Count,
                });
            await task;
            _logger.LogInformation(" Stop client connections acked.");
        }
    }
}