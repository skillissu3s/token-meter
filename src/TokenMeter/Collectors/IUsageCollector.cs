using TokenMeter.Core;

namespace TokenMeter.Collectors;

public interface IUsageCollector
{
    string Id { get; }
    string Name { get; }
    string Accent { get; }
    ProviderSnapshot Collect(Settings settings);
}
