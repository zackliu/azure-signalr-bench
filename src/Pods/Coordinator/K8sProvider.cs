﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Azure.SignalRBench.Common;
using k8s;
using k8s.Models;
using Microsoft.Azure.Management.Storage.Fluent;
using Microsoft.Extensions.Configuration;

namespace Azure.SignalRBench.Coordinator
{
    public class K8sProvider : IK8sProvider
    {
        private const string _default = "default";
        private const string _appserver = "appserver";
        private const string _client = "client";
        private Kubernetes? _k8s;
        private PerfStorageProvider _perfStorageProvider;
        private string _redisConnectionString;
        public Kubernetes K8s => _k8s ?? throw new InvalidOperationException();

        public K8sProvider(PerfStorageProvider perfStorageProvider, IConfiguration configuration)
        {
            _perfStorageProvider = perfStorageProvider;
            _redisConnectionString = configuration[PerfConstants.ConfigurationKeys.RedisConnectionStringKey];
        }
        public void Initialize(string config)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(config));
            _k8s = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
        }

        public async Task<string> CreateServerPodsAsync(string testId, int nodePoolIndex, string[] asrsConnectionStrings,int serverPodCount, CancellationToken cancellationToken)
        {
            var name = _appserver + "-" + testId;
            var service = new V1Service()
            {
                ApiVersion = "v1",
                Kind = "Service",
                Metadata = new V1ObjectMeta()
                {
                    Name = name
                },
                Spec = new V1ServiceSpec()
                {
                    Ports =
                    {
                        new V1ServicePort(port: 80, targetPort: 8080)
                    },
                    Selector =
                    {
                        ["app"] = name
                    }
                }
            };
            await _k8s.CreateNamespacedServiceAsync(service, _default, cancellationToken: cancellationToken);

            V1Deployment deployment = new V1Deployment()
            {
                ApiVersion = "apps/v1",
                Kind = "Deployment",
                Metadata = new V1ObjectMeta()
                {
                    Name = name,
                    Labels =
                    {
                        [PerfConstants.ConfigurationKeys.TestIdKey] = testId
                    }
                },
                Spec = new V1DeploymentSpec
                {
                    Replicas = serverPodCount,
                    Selector = new V1LabelSelector()
                    {
                        MatchLabels =
                        {
                            { "app", name }
                        }
                    },
                    Template = new V1PodTemplateSpec()
                    {
                        Metadata =
                        {
                            CreationTimestamp = null,
                            Labels =
                            {
                                ["app"] = name,
                            }
                        },
                        Spec = new V1PodSpec
                        {
                            NodeSelector =
                            {
                                ["agentpool"] = AksProvider.ToPoolName(nodePoolIndex)
                            },
                            Containers =
                            {
                            new V1Container()
                            {
                                Name = name,
                                Image = "signalrbenchmark/perf:1.3",
                                Resources=
                                {
                                    Requests=
                                    {
                                        ["cpu"]=new ResourceQuantity("3000m"),
                                        ["memory"]=new ResourceQuantity("12000Mi")
                                    },
                                    Limits=
                                    {
                                        ["cpu"]=new ResourceQuantity("3000m"),
                                        ["memory"]=new ResourceQuantity("12000Mi")
                                    }
                                },
                                VolumeMounts=
                                {
                                    new V1VolumeMount("/mnt/perf","volume")
                                },
                                Command=
                                {
                                    "/bin/sh", "-c"
                                },
                                Args=
                                {
                                    "cp /mnt/perf/manifest/AppServer/AppServer.zip /home ; cd /home ; unzip AppServer.zip ;exec ./AppServer;"
                                },
                                Env=
                                {
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.PodNameStringKey,valueFrom:new V1EnvVarSource(fieldRef:new V1ObjectFieldSelector("metadata.name") ) ),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.TestIdKey,testId),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.ConnectionString,string.Join(",",asrsConnectionStrings)),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.StorageConnectionStringKey,_perfStorageProvider.ConnectionString),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.RedisConnectionStringKey,_redisConnectionString),

                                }
                            },
                            },
                            Volumes =
                            {
                                new V1Volume("volume")
                                {
                                    AzureFile=new V1AzureFileVolumeSource("azure-secret","perf",false)
                                }
                            }
                        },

                    }
                }
            };
            await _k8s.CreateNamespacedDeploymentAsync(deployment, _default, cancellationToken: cancellationToken);
            return name;
        }

        public async Task CreateClientPodsAsync(string testId, int nodePoolIndex, int clientPodCount, CancellationToken cancellationToken)
        {
            var name = _client + '-' + testId;
            V1Deployment deployment = new V1Deployment()
            {
                ApiVersion = "apps/v1",
                Kind = "Deployment",
                Metadata =
                {
                    Name = name,
                    Labels =
                    {
                        [PerfConstants.ConfigurationKeys.TestIdKey] = testId
                    }
                },
                Spec =
                {
                    Replicas = clientPodCount,
                    Selector = 
                    {
                        MatchLabels =
                        {
                            { "app", name }
                        }
                    },
                    Template =
                    {
                        Metadata =
                        {
                            CreationTimestamp = null,
                            Labels =
                            {
                                ["app"] = name,
                            }
                        },
                        Spec =
                        {
                            NodeSelector =
                            {
                                ["agentpool"] = AksProvider.ToPoolName(nodePoolIndex)
                            },
                            Containers =
                            {
                            new V1Container()
                            {
                                Name = name,
                                Image = "signalrbenchmark/perf:1.3",
                                Resources=
                                {
                                    Requests=
                                    {
                                        ["cpu"]=new ResourceQuantity("3000m"),
                                        ["memory"]=new ResourceQuantity("12000Mi")
                                    },
                                    Limits=
                                    {
                                        ["cpu"]=new ResourceQuantity("3000m"),
                                        ["memory"]=new ResourceQuantity("12000Mi")
                                    }
                                },
                                VolumeMounts=
                                {
                                    new V1VolumeMount("/mnt/perf","volume")
                                },
                                Command=
                                {
                                    "/bin/sh", "-c"
                                },
                                Args=
                                {
                                    "cp /mnt/perf/manifest/Client/Client.zip /home ; cd /home ; unzip Client.zip ; exec ./Client"
                                },
                                Env=
                                {
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.PodNameStringKey,valueFrom:new V1EnvVarSource(fieldRef:new V1ObjectFieldSelector("metadata.name") ) ),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.TestIdKey,testId),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.StorageConnectionStringKey,_perfStorageProvider.ConnectionString),
                                    new V1EnvVar(PerfConstants.ConfigurationKeys.RedisConnectionStringKey,_redisConnectionString),
                                }
                            },
                            },
                            Volumes =
                            {
                                new V1Volume("volume")
                                {
                                    AzureFile = new V1AzureFileVolumeSource("azure-secret", "perf", false)
                                }
                            }
                        },
                    }
                }
            };
            await _k8s.CreateNamespacedDeploymentAsync(deployment, _default, cancellationToken: cancellationToken);
        }

        public async Task DeleteClientPodsAsync(string testId, int nodePoolIndex)
        {
            string name = _client + '-' + testId;
            await _k8s.DeleteNamespacedDeploymentAsync(name, _default);
        }

        public async Task DeleteServerPodsAsync(string testId, int nodePoolIndex)
        {
            string name = _appserver + '-' + testId;
            await _k8s.DeleteNamespacedServiceAsync(name, _default);
            await _k8s.DeleteNamespacedDeploymentAsync(name, _default);
        }
    }
}
