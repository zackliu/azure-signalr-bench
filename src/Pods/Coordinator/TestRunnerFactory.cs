﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Azure.SignalRBench.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Azure.SignalRBench.Coordinator
{
    public class TestRunnerFactory
    {
        private readonly string _podName;
        private readonly string _redisConnectionString;
        private readonly ILogger<TestRunner> _logger;

        public TestRunnerFactory(
            IConfiguration configuration,
            IAksProvider aksProvider,
            IK8sProvider k8sProvider,
            ISignalRProvider signalRProvider,
            ILogger<TestRunner> logger)
        {
            _podName = configuration[Constants.ConfigurationKeys.PodNameStringKey];
            _redisConnectionString = configuration[Constants.ConfigurationKeys.RedisConnectionStringKey];
            AksProvider = aksProvider;
            K8sProvider = k8sProvider;
            SignalRProvider = signalRProvider;
            _logger = logger;
        }

        public IAksProvider AksProvider { get; }

        public IK8sProvider K8sProvider { get; }

        public ISignalRProvider SignalRProvider { get; }

        public TestRunner Create(
            TestJob job,
            int nodePoolIndex,
            string defaultLocation) =>
            new TestRunner(
                job,
                _podName,
                _redisConnectionString,
                nodePoolIndex,
                AksProvider,
                K8sProvider,
                SignalRProvider,
                defaultLocation,
                _logger);
    }
}