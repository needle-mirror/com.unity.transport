using Unity.Collections;
using Unity.Networking.Transport.Logging;

namespace Unity.Networking.Transport.Logging
{
    /// <summary>Parameters related to how UTP logs messages.</summary>
    public struct LoggingParameter : INetworkParameter
    {
        /// <summary>Label to use for this driver in the logs.</summary>
        public FixedString32Bytes DriverName;

        /// <summary>Validate the settings.</summary>
        /// <returns>True if the settings are valid, false otherwise.</returns>
        public bool Validate()
        {
            if (DriverName.IsEmpty)
            {
                DebugLog.LogError("The driver name must not be empty.");
                return false;
            }

            return true;
        }
    }

    /// <summary>Extension methods related to <see cref="LoggingParameter"/>.</summary>
    public static class LoggingParameterExtensions
    {
        /// <summary>
        /// Sets the <see cref="LoggingParameter"/> values for the given <see cref="NetworkSettings"/>.
        /// </summary>
        /// <param name="settings"><see cref="NetworkSettings"/> to modify.</param>
        /// <param name="driverName">Label to use for this driver in the logs.</param>
        /// <returns>Modified <see cref="NetworkSettings"/>.</returns>
        public static ref NetworkSettings WithLoggingParameters(ref this NetworkSettings settings, FixedString32Bytes driverName)
        {
            var parameter = new LoggingParameter { DriverName = driverName };
            settings.AddRawParameterStruct(ref parameter);
            return ref settings;
        }
    }
}