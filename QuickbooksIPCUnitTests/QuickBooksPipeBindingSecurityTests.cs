using System.Net.Security;
using System.ServiceModel;
using NUnit.Framework;
using QuickBooksConnectorCore;

namespace QuickbooksIPCUnitTests
{
    [TestFixture]
    public class QuickBooksPipeBindingSecurityTests
    {
        [TestCase("CreateServiceBinding")]
        [TestCase("CreateClientBinding")]
        public void Binding_AllowsSameUserClientsAcrossUacIntegrityLevels(string factoryMethodName)
        {
            NetNamedPipeBinding binding = factoryMethodName == "CreateServiceBinding"
                ? QuickBooksPipeBindingFactory.CreateServiceBinding()
                : QuickBooksPipeBindingFactory.CreateClientBinding();

            Assert.That(binding.Security.Mode, Is.EqualTo(NetNamedPipeSecurityMode.Transport));
            Assert.That(binding.Security.Transport.ProtectionLevel, Is.EqualTo(ProtectionLevel.None));
        }
    }
}
