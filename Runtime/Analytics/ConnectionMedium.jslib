mergeInto(LibraryManager.library, {
    utp_GetConnectionMedium: function ()
    {
        if (navigator && navigator.connection)
        {
            switch (navigator.connection.type)
            {
                case "ethernet":
                    return 1 << 2; // Wired
                case "wifi":
                    return 1 << 3; // Wifi
                case "cellular":
                case "wimax":
                case "bluetooth": // Probably means tethering to a cellular network.
                    return 1 << 4; // Cellular
                default:
                    return 1 << 0; // Unknown
            }
        }
        else
        {
            return 1 << 0; // Unknown
        }
    }
});