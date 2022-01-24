using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using Unity.Networking.Transport.Protocols;
using Unity.Networking.Transport.Utilities;
using UnityEngine.TestTools;
using Random = UnityEngine.Random;

namespace Unity.Networking.Transport.Tests
{
    public class TransportErrorValidation
    {
        // -- NetworkDriver ----------------------------------------------------

        // - NullReferenceException : If the NetworkInterface is invalid for some reason
        [Test]
        public void Given_InvalidNetworkInterface_SystemThrows_NullReferenceException()
        {
            Assert.Throws<NullReferenceException>(() => { var driver = new NetworkDriver(default(INetworkInterface)); });
        }

        // - ArgumentException : If the NetworkParameters are outside their given range.
        [Test]
        public void Given_ParametersOutsideSpecifiedRange_Throws_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var settings = new NetworkSettings();
                settings.WithDataStreamParameters(size: -1);
                var driver = new NetworkDriver(new BaselibNetworkInterface(), settings);
            });

            LogAssert.Expect(LogType.Error, "size value (-1) must be greater or equal to 0");
        }

        // -- NetworkPipeline --------------------------------------------------

        // - ArgumentException : If the NetworkParameters are outside their given range.
        [Test]
        public void Given_PiplineParametersOutsideSpecifiedRange_Throws_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var settings = new NetworkSettings();
                settings.WithPipelineParameters(initialCapacity: -1);
                var driver = new NetworkDriver(new BaselibNetworkInterface(), settings);
            });

            LogAssert.Expect(LogType.Error, "initialCapacity value (-1) must be greater or equal to 0");
        }

        // -- BaselibNetworkInterface ------------------------------------------

        [Test]
        public void Given_BaselibReceiveParametersOutsideSpecifiedRange_Throws_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var settings = new NetworkSettings();
                settings.WithBaselibNetworkInterfaceParameters(receiveQueueCapacity: -1, sendQueueCapacity: 1);
                var driver = new NetworkDriver(new BaselibNetworkInterface(), settings);
            });

            LogAssert.Expect(LogType.Error, "receiveQueueCapacity value (-1) must be greater than 0");
        }

        [Test]
        public void Given_BaselibSendParametersOutsideSpecifiedRange_Throws_ArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                var settings = new NetworkSettings();
                settings.WithBaselibNetworkInterfaceParameters(receiveQueueCapacity: 1, sendQueueCapacity: -1);
                var driver = new NetworkDriver(new BaselibNetworkInterface(), settings);
            });

            LogAssert.Expect(LogType.Error, "sendQueueCapacity value (-1) must be greater than 0");
        }
    }
}
