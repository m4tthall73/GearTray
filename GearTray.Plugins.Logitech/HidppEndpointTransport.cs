using LGSTrayHID.HidApi;
using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

using static LGSTrayHID.HidApi.HidApi;

namespace LGSTrayHID
{
    public sealed partial class HidppDevices
    {
        private static HidDevicePtr OpenEndpoint(HidEndpointInfo endpoint)
        {
            nint dev = HidOpenPath(endpoint.Path);
#if DEBUG
            if (dev == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to open {endpoint.Path}");
            }
            else
            {
                Console.WriteLine($"Opened {endpoint.MessageType} {endpoint.ProductId:X4} {endpoint.UsagePage:X4}:{endpoint.Usage:X4} {endpoint.Path}");
            }
#endif
            return dev;
        }

        private void StartReadThread(HidDevicePtr dev, CancellationToken cancellationToken)
        {
            Thread thread = new(() => ReadLoop(dev, cancellationToken))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        private void ReadLoop(HidDevicePtr dev, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[64];
            bool readFailed = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Array.Clear(buffer);
                    int read = dev.Read(buffer, buffer.Length, READ_TIMEOUT);
                    if (read < 0)
                    {
                        readFailed = true;
                        break;
                    }

                    if (read == 0)
                    {
                        continue;
                    }

                    ProcessMessage(buffer[..Math.Min(read, buffer.Length)]);
                }
            }
            finally
            {
                if (readFailed && !cancellationToken.IsCancellationRequested && !ConsumeExpectedClose(dev))
                {
                    SignalKnownDevicesOffline("readFailed");
                }

                CloseEndpoint(dev);
            }
        }

        private void ProcessMessage(byte[] buffer)
        {
            if (buffer.Length < 4)
            {
                return;
            }

            if (buffer[0] == 0x10 && buffer.Length >= 7 && buffer[2] == DEVICE_CONNECTION_NOTIFICATION)
            {
                if ((buffer[4] & DEVICE_DISCONNECTED_FLAG) == 0)
                {
                    SignalOnline(buffer[1]);
                }
                else
                {
                    SignalOffline(buffer[1]);
                }

                return;
            }

            _channel.Writer.TryWrite(buffer);
        }

        public async Task<byte[]> WriteRead10(HidDevicePtr hidDevicePtr, byte[] buffer, int timeout = DEFAULT_COMMAND_TIMEOUT)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);
            if (hidDevicePtr == IntPtr.Zero)
            {
                return [];
            }

            bool locked = await _commandSemaphore.WaitAsync(timeout);
            if (!locked)
            {
                return [];
            }

            try
            {
                await hidDevicePtr.WriteAsync(buffer);

                using CancellationTokenSource cts = new(timeout);
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        byte[] ret = await _channel.Reader.ReadAsync(cts.Token);
                        if (ret.Length >= 4 && ret[0] == 0x10 && ret[1] == buffer[1] && ret[2] == buffer[2])
                        {
                            return ret;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }
                }

                return [];
            }
            finally
            {
                _commandSemaphore.Release();
            }
        }

        public async Task<Hidpp20> WriteRead20(HidDevicePtr hidDevicePtr, Hidpp20 buffer, int timeout = DEFAULT_COMMAND_TIMEOUT, bool ignoreHID10 = true)
        {
            ObjectDisposedException.ThrowIf(_disposeCount > 0, this);
            if (hidDevicePtr == IntPtr.Zero)
            {
                return (Hidpp20)Array.Empty<byte>();
            }

            byte[] request = (byte[])buffer;
            bool targetsShortEndpoint = (nint)hidDevicePtr == (nint)_devShort;
            bool c54dShortRequest = ShouldUseC54dRecovery(targetsShortEndpoint, request, buffer);
            int commandTimeout = c54dShortRequest ? Math.Max(timeout, C54D_COMMAND_TIMEOUT) : timeout;
            int attempts = c54dShortRequest ? C54D_COMMAND_ATTEMPTS : DEFAULT_COMMAND_ATTEMPTS;
            bool locked = await _commandSemaphore.WaitAsync(commandTimeout);
            if (!locked)
            {
                return (Hidpp20)Array.Empty<byte>();
            }

            try
            {
                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    HidDevicePtr writeDevice = targetsShortEndpoint ? _devShort : hidDevicePtr;
                    int written = await writeDevice.WriteAsync(request);
                    if (written <= 0)
                    {
                        RecordTransportFailure("hidWriteFailed", request, attempt);
                        if (c54dShortRequest)
                        {
                            Hidpp20 fallbackRet = await TryC54dLongReportFallbackAsync(buffer, request, commandTimeout, ignoreHID10, attempt);
                            if (fallbackRet.Length > 0)
                            {
                                return fallbackRet;
                            }
                        }
                        else if (targetsShortEndpoint)
                        {
                            ReopenShortEndpoint("hidWriteFailed");
                        }

                        await DelayBeforeRetry(attempt);
                        continue;
                    }

                    Hidpp20 ret = await ReadMatchingHidpp20Async(buffer, commandTimeout, ignoreHID10, c54dShortRequest);
                    if (ret.Length > 0)
                    {
                        if (c54dShortRequest && ret.GetFeatureIndex() == 0x8F)
                        {
                            RecordTransportFailure("hidProtocolError", request, attempt);
                        }

                        return ret;
                    }

                    RecordTransportFailure("hidReadTimeout", request, attempt);

                    if (c54dShortRequest)
                    {
                        ret = await TryC54dLongReportFallbackAsync(buffer, request, commandTimeout, ignoreHID10, attempt);
                        if (ret.Length > 0)
                        {
                            return ret;
                        }
                    }

                    await DelayBeforeRetry(attempt);
                }

                return (Hidpp20)Array.Empty<byte>();
            }
            finally
            {
                _commandSemaphore.Release();
            }
        }

        private async Task<Hidpp20> ReadMatchingHidpp20Async(Hidpp20 buffer, int timeout, bool ignoreHID10, bool preferNonErrorResponse = false)
        {
            using CancellationTokenSource cts = new(timeout);
            Hidpp20 firstErrorResponse = (Hidpp20)Array.Empty<byte>();
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    Hidpp20 ret = await _channel.Reader.ReadAsync(cts.Token);
                    if (ret.Length < 4 || ret.GetDeviceIdx() != buffer.GetDeviceIdx())
                    {
                        continue;
                    }

                    if (!ignoreHID10 && ret.GetFeatureIndex() == 0x8F)
                    {
                        if (!preferNonErrorResponse)
                        {
                            return ret;
                        }

                        if (firstErrorResponse.Length == 0)
                        {
                            firstErrorResponse = ret;
                        }

                        continue;
                    }

                    if (ret.GetFeatureIndex() == buffer.GetFeatureIndex() && ret.GetSoftwareId() == SW_ID)
                    {
                        return ret;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }
            }

            return firstErrorResponse;
        }

        private bool ShouldUseC54dRecovery(bool targetsShortEndpoint, byte[] request, Hidpp20 buffer)
        {
            return targetsShortEndpoint
                && _shortEndpoint.ProductId == LIGHTSPEED_C54D_RECEIVER
                && _devLong != IntPtr.Zero
                && request.Length == 7
                && request[0] == 0x10
                && buffer.GetDeviceIdx() != 0xFF;
        }

        private async Task<Hidpp20> TryC54dLongReportFallbackAsync(Hidpp20 buffer, byte[] shortRequest, int timeout, bool ignoreHID10, int attempt)
        {
            HidDevicePtr longDevice = _devLong;
            if (longDevice == IntPtr.Zero)
            {
                return (Hidpp20)Array.Empty<byte>();
            }

            byte[] longRequest = CreateLongReport(shortRequest);
            int written = await longDevice.WriteAsync(longRequest);
            if (written <= 0)
            {
                RecordTransportFailure("hidLongWriteFailed", longRequest, attempt);
                ReopenLongEndpoint("hidLongWriteFailed");
                return (Hidpp20)Array.Empty<byte>();
            }

            Hidpp20 ret = await ReadMatchingHidpp20Async(buffer, timeout, ignoreHID10, true);
            if (ret.Length == 0)
            {
                RecordTransportFailure("hidLongReadTimeout", longRequest, attempt);
                return ret;
            }

            if (ret.GetFeatureIndex() == 0x8F)
            {
                RecordTransportFailure("hidLongProtocolError", longRequest, attempt);
                return (Hidpp20)Array.Empty<byte>();
            }

            return ret;
        }

        private static byte[] CreateLongReport(byte[] shortRequest)
        {
            byte[] longRequest = new byte[20];
            longRequest[0] = 0x11;
            Array.Copy(shortRequest, 1, longRequest, 1, shortRequest.Length - 1);
            return longRequest;
        }

        private static async Task DelayBeforeRetry(int attempt)
        {
            await Task.Delay(COMMAND_RETRY_DELAY_MS * attempt);
        }

        private void ReopenShortEndpoint(string reason)
        {
            HidDevicePtr reopened = OpenEndpoint(_shortEndpoint);
            if (reopened == IntPtr.Zero)
            {
                AddFailure("reopenShortFailed");
                return;
            }

            HidDevicePtr previous;
            lock (_handleSync)
            {
                previous = _devShort;
                _devShort = reopened;
            }

            MarkExpectedClose(previous);
            CloseEndpoint(previous);

            if (_readCts != null)
            {
                StartReadThread(reopened, _readCts.Token);
            }

            NativeDiagnosticsStore.AddEvent($"{NativeDiagnosticsStore.FormatHex(_shortEndpoint.ProductId, 4)}: reopened short endpoint after {reason}");
        }

        private void ReopenLongEndpoint(string reason)
        {
            if (_longEndpoint == null)
            {
                return;
            }

            HidDevicePtr reopened = OpenEndpoint(_longEndpoint);
            if (reopened == IntPtr.Zero)
            {
                AddFailure("reopenLongFailed");
                return;
            }

            HidDevicePtr previous;
            lock (_handleSync)
            {
                previous = _devLong;
                _devLong = reopened;
            }

            MarkExpectedClose(previous);
            CloseEndpoint(previous);

            if (_readCts != null)
            {
                StartReadThread(reopened, _readCts.Token);
            }

            NativeDiagnosticsStore.AddEvent($"{NativeDiagnosticsStore.FormatHex(_shortEndpoint.ProductId, 4)}: reopened long endpoint after {reason}");
        }

        private void CloseEndpoint(HidDevicePtr dev)
        {
            nint raw = dev;
            if (raw == IntPtr.Zero)
            {
                return;
            }

            lock (_handleSync)
            {
                if (!_closedHandles.Add(raw))
                {
                    return;
                }
            }

            HidClose(raw);
        }

        private void MarkExpectedClose(HidDevicePtr dev)
        {
            nint raw = dev;
            if (raw == IntPtr.Zero)
            {
                return;
            }

            lock (_handleSync)
            {
                _expectedClosedHandles.Add(raw);
            }
        }

        private bool ConsumeExpectedClose(HidDevicePtr dev)
        {
            nint raw = dev;
            if (raw == IntPtr.Zero)
            {
                return false;
            }

            lock (_handleSync)
            {
                return _expectedClosedHandles.Remove(raw);
            }
        }
    }
}
