using Azure.SignalRBench.Common;

namespace Azure.SignalRBench.Client
{
    public class SignalRDefaultClientAgentFactory : IClientAgentFactory
    {
        public IClientAgent Create(string url, Protocol protocol, string[] groups, int globalIndex,
            ClientAgentContext context)
        {
            return new SignalRDefaultClientAgent(url + PerfConstants.Name.HubName, protocol, $"user{globalIndex}", groups,
                globalIndex,
                context);
        }
    }
}