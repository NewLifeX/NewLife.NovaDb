using Xunit;

namespace XUnitTest;

/// <summary>集成测试集合定义，确保所有使用 NovaServer 的测试串行执行</summary>
[CollectionDefinition("IntegrationTests")]
public class IntegrationTestCollection : ICollectionFixture<IntegrationServerFixture>
{
}
