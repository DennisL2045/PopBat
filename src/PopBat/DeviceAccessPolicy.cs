using System;
using System.Collections.Generic;
using System.Linq;

namespace PopBat;

internal static class DeviceAccessPolicy
{
    internal const string Scope = "SupportedBatteryProtocols";
    // Explicit battery providers, not arbitrary HID command discovery.
    private static readonly HashSet<string> Providers = new(StringComparer.Ordinal) {
        "bluetooth-fast-pair", "bluetooth-battery-service", "hid-battery-feature",
        "rog-delta-ii", "logitech-hidpp", "legacy-keyboard-battery"
    };
    internal static bool AllowsProvider(string id) => Providers.Contains(id);
}