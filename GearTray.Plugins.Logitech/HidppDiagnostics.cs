using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using System;
using System.Collections.Generic;

namespace LGSTrayHID
{
    public sealed partial class HidppDevices
    {
        internal bool MatchesEndpointPathHash(string pathHash) =>
            _shortEndpoint.PathHash.Equals(pathHash, StringComparison.OrdinalIgnoreCase)
            || (_longEndpoint?.PathHash.Equals(pathHash, StringComparison.OrdinalIgnoreCase) ?? false);

        private void RecordTransportFailure(string reason, byte[] request, int attempt)
        {
            AddFailure(reason);
            NativeDiagnosticsStore.AddEvent(
                $"{NativeDiagnosticsStore.FormatHex(_shortEndpoint.ProductId, 4)}: {reason} attempt={attempt} request={NativeDiagnosticsStore.FormatBytes(request)}"
            );
        }

        internal void RecordDeviceDiscovery(
            string deviceIndex,
            string deviceName,
            DeviceType deviceType,
            string identifier,
            IReadOnlyDictionary<ushort, byte> featureMap,
            string? selectedBatteryFeature,
            string? lastBatteryResponse,
            HidppDeviceIdentity? identity = null
        )
        {
            DeviceDiscoveryDiagnostic deviceDiagnostic = new()
            {
                DeviceIndex = deviceIndex,
                DeviceName = deviceName,
                DeviceType = deviceType.ToString(),
                Identifier = identifier,
                Identity = identity?.ToDiagnostic(),
                FeatureMap = NativeDiagnosticsStore.FormatFeatureMap(featureMap),
                SelectedBatteryFeature = selectedBatteryFeature,
                LastBatteryResponse = lastBatteryResponse,
            };

            NativeDiagnosticsStore.UpdateSession(_diagnostics, x =>
            {
                x.Devices.RemoveAll(y => y.Identifier == identifier);
                x.Devices.Add(deviceDiagnostic);
            });
            RegisterKnownDevice(identifier);
        }

        private void RegisterKnownDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            lock (_knownDeviceIds)
            {
                _knownDeviceIds.Add(deviceId);
            }
        }

        private void RecordPing(byte deviceId, bool success)
        {
            NativeDiagnosticsStore.UpdateSession(_diagnostics, x =>
                x.PingResults[NativeDiagnosticsStore.FormatHex(deviceId, 2)] = success);
        }

        private void AddFailure(string reason)
        {
            NativeDiagnosticsStore.UpdateSession(_diagnostics, x =>
            {
                if (!x.FailureReasons.Contains(reason))
                {
                    x.FailureReasons.Add(reason);
                }
            });
            NativeDiagnosticsStore.AddEvent($"{NativeDiagnosticsStore.FormatHex(_shortEndpoint.ProductId, 4)}: {reason}");
        }
    }
}
