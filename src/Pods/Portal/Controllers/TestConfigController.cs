﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Azure.SignalRBench.Common;
using Azure.SignalRBench.Coordinator.Entities;
using Azure.SignalRBench.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NCrontab;
using Newtonsoft.Json;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace Portal.Controllers
{
    [Route("TestConfig")]
    [ApiController]
    public class TestConfigController : ControllerBase
    {
        private readonly ClusterState _clusterState;
        private readonly ILogger<TestConfigController> _logger;
        private readonly IPerfStorage _perfStorage;

        public TestConfigController(IPerfStorage perfStorage, ClusterState clusterState,
            ILogger<TestConfigController> logger)
        {
            _perfStorage = perfStorage;
            _clusterState = clusterState;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IEnumerable<TestConfigEntity>> Get()
        {
            var table = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var rows = await table.QueryAsync(table.Rows
            ).ToListAsync();
            rows.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return rows;
        }


        [HttpPut]
        public async Task<ActionResult> CreateTestConfig(TestConfigEntity testConfigEntity)
        {
            var table = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var exist = await table.GetFirstOrDefaultAsync(from row in table.Rows
                where row.PartitionKey == testConfigEntity.PartitionKey
                select row);
            if (exist != null)
            {
                return BadRequest($"Test name :{testConfigEntity.PartitionKey} already exist!");
            }

            testConfigEntity.User = User.Identity.Name;
            testConfigEntity.PartitionKey = testConfigEntity.RowKey;
            try
            {
                testConfigEntity.Init();
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }

            await table.InsertAsync(testConfigEntity);
            _logger.LogInformation($"Create Test config:{JsonConvert.SerializeObject(testConfigEntity)}");
            return Ok();
        }

        [Authorize(Policy = PerfConstants.Policy.RoleLogin,
            Roles = PerfConstants.Roles.Contributor + "," + PerfConstants.Roles.Pipeline)]
        [HttpPost("StartTest/{testConfigEntityKey}")]
        public async Task StartTestAsync(string testConfigEntityKey)
        {
            var configTable = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var latestTestConfig = await configTable.GetFirstOrDefaultAsync(from row in configTable.Rows
                where row.PartitionKey == testConfigEntityKey
                select row);
            latestTestConfig.InstanceIndex += 1;
            await configTable.UpdateAsync(latestTestConfig);
            var queue = await _perfStorage.GetQueueAsync<TestJob>(PerfConstants.QueueNames.PortalJob);
            var statusTable = await _perfStorage.GetTableAsync<TestStatusEntity>(PerfConstants.TableNames.TestStatus);
            var testEntity = new TestStatusEntity
            {
                User = User.Identity.Name,
                PartitionKey = latestTestConfig.PartitionKey,
                RowKey = latestTestConfig.InstanceIndex.ToString(),
                Status = "Init",
                Healthy = true,
                Report = "",
                ErrorInfo = "",
                Dir = latestTestConfig.Dir,
                Config = JsonConvert.SerializeObject(latestTestConfig)
            };
            try
            {
                await statusTable.InsertAsync(testEntity);
                await queue.SendAsync(latestTestConfig.ToTestJob(_clusterState));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Start test error");
                throw;
            }
        }

        [HttpDelete("{key}")]
        public async Task Delete(string key)
        {
            var configTable = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var config =
                await configTable.GetFirstOrDefaultAsync(from row in configTable.Rows
                    where row.PartitionKey == key
                    select row);
            await configTable.DeleteAsync(config);
            var statusTable = await _perfStorage.GetTableAsync<TestStatusEntity>(PerfConstants.TableNames.TestStatus);
            var statuses =
                await statusTable.QueryAsync(from row in statusTable.Rows where row.PartitionKey == key select row)
                    .ToListAsync();
            if (statuses.Count > 0)
                await statusTable.BatchDeleteAsync(statuses);
        }

        [HttpPut("move/{type}/{source}/{target}")]
        public async Task<ActionResult> Move(string type, string source, string target)
        {
            switch (type)
            {
                case "jobConfig":
                {
                    var configTable =
                        await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
                    var config =
                        await configTable.GetFirstOrDefaultAsync(from row in configTable.Rows
                            where row.PartitionKey == source
                            select row);
                    if (config == null) return BadRequest($"testName {source} doesn't exist");
                    config.Dir = target;
                    await configTable.UpdateAsync(config);
                    return Ok();
                }
                case "dir":
                {
                    var configTable =
                        await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
                    var configs =
                        await configTable.QueryAsync(from row in configTable.Rows
                            where row.Dir == source
                            select row).ToListAsync();
                    if (configs.Count == 0) return BadRequest($"dir {source} doesn't exist");
                    var tasks = new List<Task>();
                    foreach (var testConfigEntity in configs)
                    {
                        testConfigEntity.Dir = target;
                        tasks.Add(Task.Run(async () => await configTable.UpdateAsync(testConfigEntity)));
                    }

                    await Task.WhenAll(tasks);
                    return Ok();
                }
                default:
                    return BadRequest("unsupported");
            }
        }

        [HttpPut("cron/{key}")]
        public async Task<ActionResult> Cron(string key, string cron)
        {
            //validate cron
            if (cron != "0")
            {
                cron = cron.Replace("_", " ");
                try
                {
                    CrontabSchedule.Parse(cron);
                }
                catch (Exception)
                {
                    return BadRequest($"invalid cron expression: {cron} ");
                }
            }

            var configTable =
                await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var config =
                await configTable.GetFirstOrDefaultAsync(from row in configTable.Rows
                    where row.PartitionKey == key
                    select row);
            if (config == null) return BadRequest($"testName {key} doesn't exist");
            config.Cron = cron;
            await configTable.UpdateAsync(config);
            return Ok();
        }

        [HttpPut("batch/{testName}")]
        public async Task<ActionResult> Batch(string testName, string dir, string units)
        {
            var table = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var testConfig = await table.GetFirstOrDefaultAsync(from row in table.Rows
                where row.PartitionKey == testName
                select row);
            if (testConfig == null)
            {
                return BadRequest($"Test name :{testName} doesn't exist!");
            }

            try
            {
                var configs = testConfig.GenerateTestConfigs(dir, units);

                var tasks = new List<Task>();
                foreach (var testConfigEntity in configs)
                {
                    testConfigEntity.User = User.Identity.Name;
                    tasks.Add(Task.Run(async () =>
                    {
                        var exist = await table.GetFirstOrDefaultAsync(from row in table.Rows
                            where row.PartitionKey == testConfigEntity.PartitionKey
                            select row);
                        if (exist != null)
                        {
                            await Delete(exist.PartitionKey);
                        }

                        await table.InsertAsync(testConfigEntity);
                    }));
                }

                await Task.WhenAll(tasks);
                return Ok();
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }

        [Authorize(Policy = PerfConstants.Policy.RoleLogin,
            Roles = PerfConstants.Roles.Contributor + "," + PerfConstants.Roles.Pipeline)]
        [HttpPost("batch/StartTest/{dir}")]
        public async Task<ActionResult> StartTestAsync(string dir, string index)
        {
            index = index.ToLower();
            var configTable = await _perfStorage.GetTableAsync<TestConfigEntity>(PerfConstants.TableNames.TestConfig);
            var configs = await configTable.QueryAsync(from row in configTable.Rows
                where row.Dir == dir
                select row).ToListAsync();
            if (configs.Count == 0)
            {
                return BadRequest($"Dir {dir} doesn't exist");
            }

            var queue = await _perfStorage.GetQueueAsync<TestJob>(PerfConstants.QueueNames.PortalJob);
            var statusTable = await _perfStorage.GetTableAsync<TestStatusEntity>(PerfConstants.TableNames.TestStatus);
            var tasks = new List<Task>();
            foreach (var testConfigEntity in configs)
            {
                var task = Task.Run(async () =>
                {
                    var testEntity = new TestStatusEntity
                    {
                        User = User.Identity.Name,
                        PartitionKey = testConfigEntity.PartitionKey,
                        RowKey = index,
                        Status = "Init",
                        Healthy = true,
                        Report = "",
                        ErrorInfo = "",
                        Dir = testConfigEntity.Dir,
                        Config = JsonConvert.SerializeObject(testConfigEntity)
                    };
                    try
                    {
                        var exist = await statusTable.GetFirstOrDefaultAsync(from row in statusTable.Rows
                            where row.PartitionKey == testEntity.PartitionKey && row.RowKey == testEntity.RowKey
                            select row);
                        if (exist != null)
                        {
                            await statusTable.DeleteAsync(exist);
                        }
                        await statusTable.InsertAsync(testEntity); 
                         await queue.SendAsync(testConfigEntity.ToTestJob(_clusterState,index));
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Start test error");
                        throw;
                    }
                });
                tasks.Add(task);
            }

            try
            {
                await Task.WhenAll(tasks);
                return Ok();
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }
    }
}