using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin: INethermindPlugin, INethermindServicesPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
        private INodeHealthService _nodeHealthService;

        public void Dispose()
        {
        }

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            _nodeHealthService = new NodeHealthService(_api.RpcModuleProvider, _api.BlockchainProcessor, _api.BlockProducer, _healthChecksConfig, _api.ChainSpec, initConfig.IsMining);

            return Task.CompletedTask;
        }

        public void AddServices(IServiceCollection service)
        {
            service.AddHealthChecks()
                .AddTypeActivatedCheck<NodeHealthCheck>(
                    "node-health", 
                    args: new object[] { _nodeHealthService });
            if (_healthChecksConfig.UIEnabled)
            {
                service.AddHealthChecksUI(setup =>
                {
                    setup.AddHealthCheckEndpoint("health", _healthChecksConfig.Slug);
                    setup.SetEvaluationTimeInSeconds(_healthChecksConfig.PollingInterval);
                    setup.SetHeaderText("Nethermind Node Health");
                    if (_healthChecksConfig.WebhooksEnabled) 
                    {
                        setup.AddWebhookNotification("webhook", uri: _healthChecksConfig.WebhooksUri, payload: _healthChecksConfig.WebhooksPayload, restorePayload: _healthChecksConfig.WebhooksRestorePayload);
                    }
                })
                .AddInMemoryStorage();
            }
        }
        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
