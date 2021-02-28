﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.SignalRBench.Common;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;

namespace Azure.SignalRBench.Coordinator
{
    public class K8sProvider : IK8sProvider
    {
        private const string _default = "default";
        private const string _appserver = "appserver";
        private const string _client = "client";
        private const string _upstream = "upstream";
        private readonly string _domain;
        private readonly PerfStorageProvider _perfStorageProvider;
        private readonly string _redisConnectionString;
        private Kubernetes? _k8s;

        public K8sProvider(PerfStorageProvider perfStorageProvider, IConfiguration configuration)
        {
            _perfStorageProvider = perfStorageProvider;
            _redisConnectionString = configuration[PerfConstants.ConfigurationKeys.RedisConnectionStringKey];
            _domain = configuration[PerfConstants.ConfigurationKeys.DomainKey];
        }

        public void Initialize(string config)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(config));
            _k8s = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
        }

        public async Task<string> CreateServerPodsAsync(string testId, string[] asrsConnectionStrings,
            int serverPodCount, TestCategory testCategory, CancellationToken cancellationToken)
        {
            var name = _appserver + "-" + testId;

            var service = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name
                },
                Spec = new V1ServiceSpec
                {
                    Ports = new List<V1ServicePort>
                    {
                        new V1ServicePort(80, targetPort: 8080)
                    },
                    Selector = new Dictionary<string, string>
                    {
                        ["app"] = name
                    }
                }
            };
            await _k8s.CreateNamespacedServiceAsync(service, _default, cancellationToken: cancellationToken);
            if (testCategory == TestCategory.AspnetCoreSignalRServerless)
            {
                var ingress = new Networkingv1beta1Ingress
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = _upstream + "-" + testId
                    },
                    Spec = new Networkingv1beta1IngressSpec
                    {
                        Rules = new List<Networkingv1beta1IngressRule>
                        {
                            new Networkingv1beta1IngressRule
                            {
                                Host = _domain,
                                Http = new Networkingv1beta1HTTPIngressRuleValue
                                {
                                    Paths = new List<Networkingv1beta1HTTPIngressPath>
                                    {
                                        new Networkingv1beta1HTTPIngressPath
                                        {
                                            Path = $"/upstream/{TestId2HubNameConverter.GenerateHubName(testId)}",
                                            Backend = new Networkingv1beta1IngressBackend
                                            {
                                                ServiceName = name,
                                                ServicePort = 80
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                };
                await _k8s.CreateNamespacedIngress1Async(ingress, _default, cancellationToken: cancellationToken);
            }

            var server = testCategory switch
            {
                TestCategory.AspnetCoreSignalRServerless => "SignalRUpstream",
                TestCategory.AspnetSignalR => "AspNetAppServer",
                _ => "AppServer"
            };

            V1Deployment deployment = new V1Deployment
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    Labels = new Dictionary<string, string>
                    {
                        [PerfConstants.ConfigurationKeys.TestIdKey] = testId
                    },
                    Annotations = new Dictionary<string, string>
                    {
                        ["cluster-autoscaler.kubernetes.io/safe-to-evict"] = "false"
                    }
                },
                Spec = new V1DeploymentSpec
                {
                    Replicas = serverPodCount,
                    Selector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            {"app", name}
                        }
                    },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            CreationTimestamp = null,
                            Labels = new Dictionary<string, string>
                            {
                                ["app"] = name
                            }
                        },
                        Spec = new V1PodSpec
                        {
                            NodeSelector = new Dictionary<string, string>
                            {
                                [PerfConstants.Name.OsLabel] = testCategory == TestCategory.AspnetSignalR
                                    ? PerfConstants.Name.Windows
                                    : PerfConstants.Name.Linux
                            },
                            Containers = new List<V1Container>
                            {
                                new V1Container
                                {
                                    Name = name,
                                    Image = testCategory == TestCategory.AspnetSignalR
                                        ? "mcr.microsoft.com/dotnet/framework/runtime:4.8"
                                        : "signalrbenchmark/perf:1.3",
                                    Resources = new V1ResourceRequirements
                                    {
                                        Requests = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("3000m"),
                                            ["memory"] = new ResourceQuantity("10000Mi")
                                        },
                                        Limits = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("3000m"),
                                            ["memory"] = new ResourceQuantity("10000Mi")
                                        }
                                    },
                                    VolumeMounts = new List<V1VolumeMount>
                                    {
                                        new V1VolumeMount("/mnt/perf", "volume")
                                    },
                                    Command = testCategory == TestCategory.AspnetSignalR
                                        ? new List<string>
                                        {
                                            "powershell"
                                        }
                                        : new List<string>
                                        {
                                            "/bin/sh", "-c"
                                        },
                                    Args = testCategory == TestCategory.AspnetSignalR
                                        ? new List<string>
                                        {
                                            "cd  /mnt/perf/manifest; xcopy .\\AspNetAppServer\\AspNetAppServer.zip C:\\home\\ ; cd C:/home/ ; tar -xf AspNetAppServer.zip ; ./AspNetAppServer.exe"
                                        }
                                        : new List<string>
                                        {
                                            $"cp /mnt/perf/manifest/{server}/{server}.zip /home ; cd /home ; unzip {server}.zip ;exec ./{server};"
                                        },
                                    Env = new List<V1EnvVar>
                                    {
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.PodNameStringKey,
                                            valueFrom: new V1EnvVarSource(
                                                fieldRef: new V1ObjectFieldSelector("metadata.name"))),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.TestIdKey, testId),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.ConnectionString,
                                            string.Join(",", asrsConnectionStrings)),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.StorageConnectionStringKey,
                                            _perfStorageProvider.ConnectionString),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.RedisConnectionStringKey,
                                            _redisConnectionString)
                                    }
                                }
                            },
                            Volumes = new List<V1Volume>
                            {
                                new V1Volume("volume")
                                {
                                    AzureFile = new V1AzureFileVolumeSource("azure-secret", "perf", false)
                                }
                            }
                        }
                    }
                }
            };
            await _k8s.CreateNamespacedDeploymentAsync(deployment, _default, cancellationToken: cancellationToken);
            return serverPodCount==0 ? asrsConnectionStrings[0] : name;
        }

        public async Task CreateClientPodsAsync(string testId, TestCategory testCategory, int clientPodCount,
            CancellationToken cancellationToken)
        {
            var name = _client + '-' + testId;

            V1Deployment deployment = new V1Deployment
            {
                Metadata = new V1ObjectMeta
                {
                    Name = name,
                    Labels = new Dictionary<string, string>
                    {
                        [PerfConstants.ConfigurationKeys.TestIdKey] = testId
                    },
                    Annotations = new Dictionary<string, string>
                    {
                        ["cluster-autoscaler.kubernetes.io/safe-to-evict"] = "false"
                    }
                },
                Spec = new V1DeploymentSpec
                {
                    Replicas = clientPodCount,
                    Selector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string>
                        {
                            {"app", name}
                        }
                    },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            CreationTimestamp = null,
                            Labels = new Dictionary<string, string>
                            {
                                ["app"] = name
                            }
                        },
                        Spec = new V1PodSpec
                        {
                            DnsConfig = new V1PodDNSConfig
                            {
                                Options = new List<V1PodDNSConfigOption>
                                {
                                    new V1PodDNSConfigOption
                                    {
                                        Name = "ndots",
                                        Value = "2"
                                    }
                                }
                            },
                            NodeSelector = new Dictionary<string, string>
                            {
                                [PerfConstants.Name.OsLabel] = PerfConstants.Name.Linux
                            },
                            Containers = new List<V1Container>
                            {
                                new V1Container
                                {
                                    Name = name,
                                    Image = "signalrbenchmark/perf:1.3",
                                    Resources = new V1ResourceRequirements
                                    {
                                        Requests = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("3000m"),
                                            ["memory"] = new ResourceQuantity("10000Mi")
                                        },
                                        Limits = new Dictionary<string, ResourceQuantity>
                                        {
                                            ["cpu"] = new ResourceQuantity("3000m"),
                                            ["memory"] = new ResourceQuantity("10000Mi")
                                        }
                                    },
                                    VolumeMounts = new List<V1VolumeMount>
                                    {
                                        new V1VolumeMount("/mnt/perf", "volume")
                                    },
                                    Command = new List<string>
                                    {
                                        "/bin/sh", "-c"
                                    },
                                    Args = new List<string>
                                    {
                                        "cp /mnt/perf/manifest/Client/Client.zip /home ; cd /home ; unzip Client.zip ; exec ./Client"
                                    },
                                    Env = new List<V1EnvVar>
                                    {
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.PodNameStringKey,
                                            valueFrom: new V1EnvVarSource(
                                                fieldRef: new V1ObjectFieldSelector("metadata.name"))),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.TestIdKey, testId),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.StorageConnectionStringKey,
                                            _perfStorageProvider.ConnectionString),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.RedisConnectionStringKey,
                                            _redisConnectionString),
                                        new V1EnvVar(PerfConstants.ConfigurationKeys.TestCategory,
                                            testCategory.ToString())
                                    }
                                }
                            },
                            Volumes = new List<V1Volume>
                            {
                                new V1Volume("volume")
                                {
                                    AzureFile = new V1AzureFileVolumeSource("azure-secret", "perf", false)
                                }
                            }
                        }
                    }
                }
            };
            await _k8s.CreateNamespacedDeploymentAsync(deployment, _default, cancellationToken: cancellationToken);
        }

        public async Task DeleteClientPodsAsync(string testId)
        {
            string name = _client + '-' + testId;
            await _k8s.DeleteNamespacedDeploymentAsync(name, _default);
        }

        public async Task DeleteServerPodsAsync(string testId, bool upstream)
        {
            string name = _appserver + '-' + testId;
            await _k8s.DeleteNamespacedServiceAsync(name, _default);
            await _k8s.DeleteNamespacedDeploymentAsync(name, _default);
            if (upstream)
                await _k8s.DeleteNamespacedIngressAsync(_upstream + "-" + testId, _default);
        }
    }
}