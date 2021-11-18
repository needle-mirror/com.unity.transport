using System;
using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine.TestTools;

namespace Unity.Networking.Transport.Tests
{
    public class NetworkSettingsTests
    {
#pragma warning disable UTP0002
        private unsafe struct TestParameter32 : INetworkParameter
        {
            public fixed byte Data[32];
        }

        private unsafe struct TestParameter64 : INetworkParameter
        {
            public fixed byte Data[64];
        }

        public struct TestParameterValidatable : INetworkParameter, IValidatableNetworkParameter
        {
            public int Valid;

            public bool Validate()
            {
                return Valid > 0;
            }
        }
#pragma warning restore UTP0002

        [Test]
        public unsafe void NetworkSettings_WithParameter_AddsParameters()
        {
            var parameter32 = new TestParameter32();
            var parameter64 = new TestParameter64();

            CommonUtilites.FillBuffer(parameter32.Data, 32);
            CommonUtilites.FillBuffer(parameter64.Data, 64);

            using var settings = new NetworkSettings();
            settings.AddRawParameterStruct(ref parameter32);
            settings.AddRawParameterStruct(ref parameter64);

            Assert.IsTrue(settings.TryGet<TestParameter32>(out var returnedParameter32));
            Assert.IsTrue(settings.TryGet<TestParameter64>(out var returnedParameter64));

            Assert.IsTrue(CommonUtilites.CheckBuffer(returnedParameter32.Data, 32));
            Assert.IsTrue(CommonUtilites.CheckBuffer(returnedParameter64.Data, 64));
        }

        [Test]
        public unsafe void NetworkSettings_WithParameter_WarnsOnDuplicated()
        {
            using var settings = new NetworkSettings();

            var paramA = new TestParameter32();
            var paramB = new TestParameter32();

            paramA.Data[0] = 1;
            paramB.Data[0] = 2;

            settings.AddRawParameterStruct(ref paramA);
            settings.AddRawParameterStruct(ref paramB);

            Assert.IsTrue(settings.TryGet<TestParameter32>(out var returnedParameter));
            Assert.AreEqual(2, returnedParameter.Data[0]);

            LogAssert.Expect(UnityEngine.LogType.Warning, $"The parameters list already contains a parameter of type {nameof(TestParameter32)}. The previous value will be overwritten.");
        }

        [Test]
        public void NetworkSettings_WithParameter_ValidatesParameter()
        {
            using var settingsValid = new NetworkSettings();
            var parameter = new TestParameterValidatable { Valid = 1 };
            settingsValid.AddRawParameterStruct(ref parameter);

            TestDelegate funct = () =>
            {
                using var settingsInvalid = new NetworkSettings();
                parameter = new TestParameterValidatable { Valid = 0 };
                settingsInvalid.AddRawParameterStruct(ref parameter);
            };

            var expectedMessage = $"The provided network parameter ({nameof(TestParameterValidatable)}) is not valid";

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var exception = Assert.Throws<System.ArgumentException>(funct);
            Assert.AreEqual(expectedMessage, exception.Message);
#else
            funct();
            LogAssert.Expect(UnityEngine.LogType.Error, expectedMessage);
#endif
        }

        [Test]
        public void NetworkSettings_TryGet_ReturnsCorrectly()
        {
            using var settings = new NetworkSettings();
            var parameter = new TestParameter32();
            settings.AddRawParameterStruct(ref parameter);

            Assert.IsTrue(settings.TryGet<TestParameter32>(out _));
            Assert.IsFalse(settings.TryGet<TestParameter64>(out _));
        }

        [Test]
        public unsafe void NetworkSettings_TryGet_SetsParameter()
        {
            var parameter32 = new TestParameter32();
            CommonUtilites.FillBuffer(parameter32.Data, 32);

            using var settings = new NetworkSettings();
            settings.AddRawParameterStruct(ref parameter32);

            Assert.IsTrue(settings.TryGet<TestParameter32>(out var returnedParameter32));
            Assert.IsTrue(CommonUtilites.CheckBuffer(returnedParameter32.Data, 32));

            Assert.IsFalse(settings.TryGet<TestParameter64>(out _));
        }

        [Test]
        public void NetworkSettings_WithParameter_ThrowsIfDisposed()
        {
            using var settings = new NetworkSettings();
            settings.Dispose();

            TestDelegate funct = () =>
            {
                var parameter = new TestParameter32();
                settings.AddRawParameterStruct(ref parameter);
            };

            var expectedMessage = $"The {nameof(NetworkSettings)} has been deallocated, it is not allowed to access it.";

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var exception = Assert.Throws<ObjectDisposedException>(funct);
            Assert.AreEqual(expectedMessage, exception.Message);
#else
            funct();
            LogAssert.Expect(UnityEngine.LogType.Error, expectedMessage);
#endif
        }

        [Test]
        public void NetworkSettings_TryGet_ThrowsIfDisposed()
        {
            using var settings = new NetworkSettings();
            settings.Dispose();

            TestDelegate funct = () =>
            {
                settings.TryGet<TestParameter32>(out _);
            };

            var expectedMessage = $"The {nameof(NetworkSettings)} has been deallocated, it is not allowed to access it.";

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var exception = Assert.Throws<ObjectDisposedException>(funct);
            Assert.AreEqual(expectedMessage, exception.Message);
#else
            funct();
            LogAssert.Expect(UnityEngine.LogType.Error, expectedMessage);
#endif
        }

        [UnityTest]
        public IEnumerator NetworkSettings_TempAllocator_ThrowsIfAccessedAfterDisposed()
        {
            var settings = new NetworkSettings();
            var parameter = new TestParameter32();
            settings.AddRawParameterStruct(ref parameter);

            // wait one frame
            yield return null;

            TestDelegate funct = () =>
            {
                settings.TryGet<TestParameter32>(out _);
            };

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var exception = Assert.Throws<ObjectDisposedException>(funct);
#else
            // Collections deallocated message is disabled if checks are not active
            // funct();
            // LogAssert.Expect(UnityEngine.LogType.Error, $"The {nameof(NetworkSettings)} has been deallocated, it is not allowed to access it.");
#endif
        }

        [Test]
        public void NetworkSettings_ExtensionPlusNoAllocator_StoresValue()
        {
            // This will not initialize the internal native container
            var settings = new NetworkSettings();

            // This will initialize the internal native container, and so the new returned setting
            // could return a copy of the setting. Then the returned settings and the var settings
            // will reference two different instances. To fix it all extension methods must be
            // defined with _ref this_, for that we use Roslyn analysers to make sure users follow
            // the rules, and keep this test as a reminder and quick validator.
            settings.WithNetworkConfigParameters(heartbeatTimeoutMS: 123454321);

            Assert.AreEqual(123454321, settings.GetNetworkConfigParameters().heartbeatTimeoutMS);
        }

        private static IEnumerable<MethodInfo> GetNetworkSettingsExtensions()
        {
            var methods = new List<MethodInfo>();
            // Uncomment the following line to test also user assemblies
            // var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var assemblies = new[] { typeof(INetworkParameter).Assembly };

            foreach (var assembly in assemblies)
            {
                var query = from type in assembly.GetTypes()
                    where type.IsSealed && !type.IsGenericType && !type.IsNested
                    from method in type.GetMethods(BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic)
                    where method.IsDefined(typeof(ExtensionAttribute), false)
                    where (method.GetParameters()[0].ParameterType == typeof(NetworkSettings) || method.GetParameters()[0].ParameterType == typeof(NetworkSettings).MakeByRefType())
                    select method;

                methods.AddRange(query);
            }

            return methods;
        }

        [Test]
        public void NetworkSettings_ExtensionMethods_RefThis()
        {
            var extensionMethods = GetNetworkSettingsExtensions();

            foreach (var method in extensionMethods)
            {
                var thisParameter = method.GetParameters()[0];
                Assert.IsTrue(thisParameter.ParameterType.IsByRef, $"NetworkSettings extension method {method.Name} must define a _ref this_ parameter");

                if (method.ReturnType == typeof(NetworkSettings))
                {
                    var returnParameter = method.ReturnParameter;
                    Assert.IsTrue(returnParameter.ParameterType.IsByRef, $"NetworkSettings extension method {method.Name} must return a NetworkSettings by reference");
                }
            }
        }

        [Test]
        public void NetworkSettings_ExtensionMethods_Naming()
        {
            var extensionMethods = GetNetworkSettingsExtensions();

            foreach (var method in extensionMethods)
            {
                var setRegex = new Regex(@"^With.*Parameters$");
                var getRegex = new Regex(@"^Get.*Parameters$");
                Assert.IsTrue(setRegex.IsMatch(method.Name) || getRegex.IsMatch(method.Name), $"The method '{method.Name}' must follow the rule 'With[...]Parameters' or 'Get[...]Parameters'");
            }
        }

        [Test]
        public void NetworkSettings_ExtensionMethods_RefParameters()
        {
            var extensionMethods = GetNetworkSettingsExtensions();

            foreach (var method in extensionMethods)
            {
                var parameters = method.GetParameters();
                foreach (var parameter in parameters)
                {
                    var parameterType = parameter.ParameterType;

                    if (!parameterType.IsPrimitive && !parameterType.IsEnum)
                        Assert.IsTrue(parameterType.IsByRef, $"The parameter '{parameter.Name}' of the method '{method.Name}' should be passed by reference");
                }
            }
        }

        // TODO: Remove when deprecation is complete
#if !UNITY_DOTSRUNTIME
        [Test]
        public unsafe void LEGACY_NetworkSettings_FromArray_ConvertsToSettings()
        {
            var parameter32 = new TestParameter32();
            var parameter64 = new TestParameter64();

            CommonUtilites.FillBuffer(parameter32.Data, 32);
            CommonUtilites.FillBuffer(parameter64.Data, 64);

            using var settings = NetworkSettings.FromArray(parameter32, parameter64);

            Assert.IsTrue(settings.TryGet<TestParameter32>(out var returnedParameter32));
            Assert.IsTrue(settings.TryGet<TestParameter64>(out var returnedParameter64));

            Assert.IsTrue(CommonUtilites.CheckBuffer(returnedParameter32.Data, 32));
            Assert.IsTrue(CommonUtilites.CheckBuffer(returnedParameter64.Data, 64));
        }

        [Test]
        public unsafe void LEGACY_NetworkSettings_FromArray_ValidatesParameter()
        {
            TestDelegate funct = () =>
            {
                using var settings = NetworkSettings.FromArray(new TestParameterValidatable { Valid = 0 });
            };

            var expectedMessage = $"The provided network parameter ({typeof(TestParameterValidatable).Name}) is not valid";

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var exception = Assert.Throws<System.ArgumentException>(funct);
            Assert.AreEqual(expectedMessage, exception.Message);
#else
            funct();
            LogAssert.Expect(UnityEngine.LogType.Error, expectedMessage);
#endif
        }

#endif // !UNITY_DOTSRUNTIME
    }
}
