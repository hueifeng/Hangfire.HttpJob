﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.HttpJob.Agent.Config;
using Hangfire.HttpJob.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hangfire.HttpJob.Agent
{
    class JobAgentRegisterService: IHostedService, IDisposable
    {
        private readonly JobAgentOptions _options;
        private readonly ILogger _logger;
        private static System.Threading.Timer _timer;
        /// <summary>
        /// 重试三次
        /// </summary>
        private int retryTimes = 0;

        public JobAgentRegisterService(IOptions<JobAgentOptions> options, ILoggerFactory loggerFactory)
        {
            _options = options.Value;
            _logger = loggerFactory.CreateLogger<JobAgentRegisterService>();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.EnableAutoRegister)
            {
                return Task.CompletedTask;
            }

            if (string.IsNullOrEmpty(_options.RegisterHangfireUrl) || (!_options.RegisterHangfireUrl.ToLower().StartsWith("http://") && !_options.RegisterHangfireUrl.ToLower().StartsWith("https://")))
            {
                _logger.LogWarning(new EventId(1, "Hangfire.HttpJob.AutoRegister"), $"RegisterHangfireUrl invaild");
                return Task.CompletedTask;
            }

            if (string.IsNullOrEmpty(_options.RegisterAgentHost) || (!_options.RegisterAgentHost.ToLower().StartsWith("http://") && !_options.RegisterAgentHost.ToLower().StartsWith("https://")))
            {
                _logger.LogWarning(new EventId(1, "Hangfire.HttpJob.AutoRegister"), $"RegisterAgentHost invaild");
                return Task.CompletedTask;
            }


            _timer = new System.Threading.Timer(DoRegister, null, 1000 * 5, 1000 * 5);
#if NETCORE
            DoRegister(null);
#endif
            return Task.CompletedTask;
        }
      
        public Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _timer.Change(-1, -1);
            }
            catch (Exception)
            {
                //ignore
            }
          
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private void DoRegister(object state)
        {
            retryTimes++;
            try
            {
                _timer.Change(-1, -1);
                foreach (var jobMetaData in JobAgentServiceConfigurer.JobAgentDic)
                {
                    //如果自己硬要不注册 就不去注册
                    if(jobMetaData.Value.EnableAutoRegister!=null && !jobMetaData.Value.EnableAutoRegister.Value ) continue;

                    //已经注册过了
                    if(jobMetaData.Value.AutoRegisterResult) continue;

                    var jobName = jobMetaData.Value.RegisterName;
                    if (string.IsNullOrEmpty(jobName))
                    {
                        jobName = jobMetaData.Key.Namespace + "." + jobMetaData.Key.Name;
                    }

                    //得设置如果存在就不能加了

                    var url = ((_options.RegisterAgentHost) + "/" + _options.SitemapUrl);
                    var urlArr = url.Split(new string[] {"://"}, StringSplitOptions.None);
                    
                    var result = HangfireJobClient.AddRecurringJob(_options.RegisterHangfireUrl, new RecurringJob
                        {
                            Url = urlArr[0] + "://" + urlArr[1].Replace("//","/"),
                            AgentClass = jobMetaData.Key.Namespace + "." + jobMetaData.Key.Name+"," + jobMetaData.Key.Assembly.GetName().Name,
                            BasicUserName = _options.BasicUserName,
                            BasicPassword = _options.BasicUserPwd,
                            Cron = "",//自注册的必须手动在去设置
                            JobName = jobName,
                        },
                        new HangfireServerPostOption
                        {
                            BasicUserName = _options.RegisterHangfireBasicName,
                            BasicPassword = _options.RegisterHangfireBasicPwd,
                            ThrowException = false,
                        });

                    var isAlreadyRegisterd = result.ErrMessage != null && result.ErrMessage.Contains("registerd");
                    if (!result.IsSuccess && !isAlreadyRegisterd)
                    {
                        //继续下次重试
                        _logger.LogError(new EventId(1, "Hangfire.HttpJob.AutoRegister"),
                            $"Failed to register job:{jobName} to hangfire httpjob server:{_options.RegisterHangfireUrl},err:{result.ErrMessage??string.Empty},retrytimes:{retryTimes}");
                        continue;
                    }

                    _logger.LogInformation(new EventId(1, "Hangfire.HttpJob.AutoRegister"), $"【{jobName}】 {(isAlreadyRegisterd ? "already registerd" : "registered")} to HangfireServer:{_options.RegisterHangfireUrl}");
                    jobMetaData.Value.AutoRegisterResult = true;
                }

            }
            catch (Exception e)
            {
                _logger.LogError(new EventId(1, "Hangfire.HttpJob.Agent"), e,
                    $"Failed to register to hangfire httpjob server:{_options.RegisterHangfireUrl},retrytimes:{retryTimes}");
            }
            finally
            {
                
                if (retryTimes >=3)
                {
                    _timer.Change(-1, -1);
                }
                else
                {
                    _timer.Change(1000 * 1, 1000 * 1);
                }

               
            }
        }


    }
}