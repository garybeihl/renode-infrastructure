// Copyright (c) 2026 Microsoft
// Licensed under the MIT license.
//
// NCSI (NC-SI) Responder - simulates a host NIC NCSI controller.
// Implements IMACInterface to connect to an AST2600 FTGMAC100 MAC via a Switch.
// Processes NCSI command frames (EtherType 0x88F8, DSP0222) and sends responses.
// NOT a register-mapped peripheral - NCSI is a protocol over Ethernet, not
// a separate hardware block on the AST2600.

using System;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Network;
using Antmicro.Renode.Peripherals.Network;

namespace Antmicro.Renode.Peripherals.Network
{
    public class NcsiResponder : IMACInterface, IExternal
    {
        public NcsiResponder()
        {
            MAC = MACAddress.Parse("DE:AD:BE:EF:00:01");
            Reset();
        }

        public void Reset()
        {
            initialStateCleared = false;
            packageSelected = false;
            selectedPackageId = 0;
            channelEnabled = new bool[MaxChannels];
            linkUp = true;
            lastResponseCode = 0xFFFF;
            lastReasonCode = 0xFFFF;
            lastInstanceId = 0;
            lastCommandType = 0;
            commandCount = 0;
            responseCount = 0;
        }

        // IMACInterface
        public MACAddress MAC { get; set; }
        public event Action<EthernetFrame> FrameReady;

        public void ReceiveFrame(EthernetFrame frame)
        {
            var bytes = frame.Bytes;

            // Minimum NCSI frame: 14 (eth header) + 16 (NCSI header) = 30 bytes
            if(bytes.Length < 30)
            {
                return;
            }

            // Check EtherType: 0x88F8 (NC-SI)
            ushort etherType = (ushort)((bytes[12] << 8) | bytes[13]);
            if(etherType != NcsiEtherType)
            {
                return;
            }

            // Parse NCSI header (starts at offset 14)
            byte mcId = bytes[14];
            byte headerRevision = bytes[15];
            byte instanceId = bytes[17];
            byte controlPacketType = bytes[18];
            byte channelId = bytes[19];
            ushort payloadLength = (ushort)((bytes[20] << 8) | bytes[21]);

            int payloadOffset = 30; // 14 + 16
            byte[] payload = new byte[0];
            if(payloadLength > 0 && bytes.Length >= payloadOffset + payloadLength)
            {
                payload = new byte[payloadLength];
                Array.Copy(bytes, payloadOffset, payload, 0, payloadLength);
            }

            commandCount++;
            lastInstanceId = instanceId;
            lastCommandType = controlPacketType;

            byte packageId = (byte)(channelId >> 5);
            byte channelIndex = (byte)(channelId & 0x1F);

            byte[] responsePayload;
            ushort responseCode;
            ushort reasonCode;

            ProcessCommand(controlPacketType, packageId, channelIndex, payload,
                out responseCode, out reasonCode, out responsePayload);

            lastResponseCode = responseCode;
            lastReasonCode = reasonCode;

            SendResponse(frame, instanceId, controlPacketType, channelId,
                responseCode, reasonCode, responsePayload);
        }

        // --- Test API (callable from Renode monitor) ---

        public void InjectNcsiCommand(int cmdType, int channelId, int instanceId)
        {
            InjectNcsiCommandWithPayload(cmdType, channelId, instanceId, new byte[0]);
        }

        public void InjectNcsiCommandWithPayload(int cmdType, int channelId,
            int instanceId, byte[] payload)
        {
            int frameLen = 30 + payload.Length;
            if(frameLen < 60)
            {
                frameLen = 60;
            }
            var bytes = new byte[frameLen];

            // Ethernet header: dst=broadcast, src=test MAC, EtherType=0x88F8
            bytes[0] = 0xFF; bytes[1] = 0xFF; bytes[2] = 0xFF;
            bytes[3] = 0xFF; bytes[4] = 0xFF; bytes[5] = 0xFF;
            bytes[6] = 0x00; bytes[7] = 0x01; bytes[8] = 0x02;
            bytes[9] = 0x03; bytes[10] = 0x04; bytes[11] = 0x05;
            bytes[12] = 0x88; bytes[13] = 0xF8;

            // NCSI header (16 bytes at offset 14)
            bytes[14] = 0x00;       // MC ID
            bytes[15] = 0x01;       // Header revision
            bytes[17] = (byte)instanceId;
            bytes[18] = (byte)cmdType;
            bytes[19] = (byte)channelId;
            bytes[20] = (byte)((payload.Length >> 8) & 0xFF);
            bytes[21] = (byte)(payload.Length & 0xFF);

            if(payload.Length > 0)
            {
                Array.Copy(payload, 0, bytes, 30, payload.Length);
            }

            EthernetFrame injectedFrame;
            if(EthernetFrame.TryCreateEthernetFrame(bytes, false, out injectedFrame))
            {
                ReceiveFrame(injectedFrame);
            }
        }

        public int GetLastResponseCode()
        {
            return lastResponseCode;
        }

        public int GetLastReasonCode()
        {
            return lastReasonCode;
        }

        public bool IsPackageSelected()
        {
            return packageSelected;
        }

        public bool IsChannelEnabled(int channel)
        {
            if(channel < 0 || channel >= MaxChannels)
            {
                return false;
            }
            return channelEnabled[channel];
        }

        public bool IsInitialStateCleared()
        {
            return initialStateCleared;
        }

        public bool IsLinkUp()
        {
            return linkUp;
        }

        public void SetLinkUp(bool up)
        {
            linkUp = up;
        }

        public int GetCommandCount()
        {
            return commandCount;
        }

        public int GetResponseCount()
        {
            return responseCount;
        }

        public int GetLastInstanceId()
        {
            return lastInstanceId;
        }

        // --- Protocol Engine ---

        private void ProcessCommand(byte cmdType, byte packageId, byte channelIndex,
            byte[] payload, out ushort responseCode, out ushort reasonCode,
            out byte[] responsePayload)
        {
            responsePayload = new byte[0];

            // Most commands require initial state cleared first
            if(cmdType != CmdClearInitialState && cmdType != CmdDeselectPackage
                && !initialStateCleared)
            {
                responseCode = RspCommandFailed;
                reasonCode = ReasonInitRequired;
                return;
            }

            switch(cmdType)
            {
                case CmdClearInitialState:
                    initialStateCleared = true;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdSelectPackage:
                    if(packageId > 0)
                    {
                        responseCode = RspCommandFailed;
                        reasonCode = ReasonInvalidParam;
                        return;
                    }
                    packageSelected = true;
                    selectedPackageId = packageId;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdDeselectPackage:
                    packageSelected = false;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdEnableChannel:
                    if(!packageSelected)
                    {
                        responseCode = RspCommandFailed;
                        reasonCode = ReasonPkgNotSelected;
                        return;
                    }
                    if(channelIndex >= MaxChannels)
                    {
                        responseCode = RspCommandFailed;
                        reasonCode = ReasonInvalidParam;
                        return;
                    }
                    channelEnabled[channelIndex] = true;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdDisableChannel:
                    if(channelIndex >= MaxChannels)
                    {
                        responseCode = RspCommandFailed;
                        reasonCode = ReasonInvalidParam;
                        return;
                    }
                    channelEnabled[channelIndex] = false;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdResetChannel:
                    if(channelIndex >= MaxChannels)
                    {
                        responseCode = RspCommandFailed;
                        reasonCode = ReasonInvalidParam;
                        return;
                    }
                    channelEnabled[channelIndex] = false;
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdGetVersionId:
                    responsePayload = BuildGetVersionIdResponse();
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdGetCapabilities:
                    responsePayload = BuildGetCapabilitiesResponse();
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdGetParameters:
                    responsePayload = BuildGetParametersResponse();
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdGetLinkStatus:
                    responsePayload = BuildGetLinkStatusResponse();
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                case CmdGetMacAddress:
                    responsePayload = BuildGetMacAddressResponse();
                    responseCode = RspCommandCompleted;
                    reasonCode = ReasonNoError;
                    break;

                default:
                    responseCode = RspCommandUnsupported;
                    reasonCode = ReasonUnknownCmd;
                    break;
            }
        }

        private void SendResponse(EthernetFrame requestFrame, byte instanceId,
            byte cmdType, byte channelId, ushort responseCode, ushort reasonCode,
            byte[] payload)
        {
            // Response includes 4-byte response/reason header + command payload
            int rspPayloadLen = 4 + payload.Length;
            int frameLen = 30 + rspPayloadLen;
            if(frameLen < 60)
            {
                frameLen = 60;
            }
            var bytes = new byte[frameLen];

            var requestBytes = requestFrame.Bytes;

            // Ethernet header: swap src/dst
            Array.Copy(requestBytes, 6, bytes, 0, 6);
            bytes[6] = MAC.A; bytes[7] = MAC.B; bytes[8] = MAC.C;
            bytes[9] = MAC.D; bytes[10] = MAC.E; bytes[11] = MAC.F;
            bytes[12] = 0x88; bytes[13] = 0xF8;

            // NCSI header
            bytes[14] = 0x00;       // MC ID
            bytes[15] = 0x01;       // Header revision
            bytes[17] = instanceId;
            bytes[18] = (byte)(cmdType | 0x80); // Response bit
            bytes[19] = channelId;
            bytes[20] = (byte)((rspPayloadLen >> 8) & 0xFF);
            bytes[21] = (byte)(rspPayloadLen & 0xFF);

            // Response code and reason code
            int payloadStart = 30;
            bytes[payloadStart] = (byte)((responseCode >> 8) & 0xFF);
            bytes[payloadStart + 1] = (byte)(responseCode & 0xFF);
            bytes[payloadStart + 2] = (byte)((reasonCode >> 8) & 0xFF);
            bytes[payloadStart + 3] = (byte)(reasonCode & 0xFF);

            if(payload.Length > 0)
            {
                Array.Copy(payload, 0, bytes, payloadStart + 4, payload.Length);
            }

            responseCount++;
            EthernetFrame responseFrame;
            if(EthernetFrame.TryCreateEthernetFrame(bytes, false, out responseFrame))
            {
                FrameReady?.Invoke(responseFrame);
            }
        }

        // --- Response builders ---

        private byte[] BuildGetVersionIdResponse()
        {
            // DSP0222 Table 8-4: 40 bytes
            var data = new byte[40];
            // NCSI version: 1.0.0 (BCD)
            data[0] = 0xF1; data[1] = 0xF0; data[2] = 0xF0; data[3] = 0x00;
            // Firmware name (8 bytes): RENODE
            data[4] = 0x52; data[5] = 0x45; data[6] = 0x4E; data[7] = 0x4F;
            data[8] = 0x44; data[9] = 0x45; data[10] = 0x20; data[11] = 0x20;
            // Firmware version: 1.0.0.0
            data[12] = 0x01;
            // PCI VID (Intel 0x8086 big-endian)
            data[16] = 0x80; data[17] = 0x86;
            // PCI DID (I210 0x1533)
            data[18] = 0x15; data[19] = 0x33;
            // PCI SSVID
            data[20] = 0x80; data[21] = 0x86;
            // Manufacturer ID
            data[24] = 0x00; data[25] = 0x00; data[26] = 0x80; data[27] = 0x86;
            return data;
        }

        private byte[] BuildGetCapabilitiesResponse()
        {
            // DSP0222 Table 8-5: 32 bytes
            var data = new byte[32];
            // Capabilities flags
            data[3] = 0x0F;
            // Broadcast packet filter
            data[7] = 0x0F;
            // Multicast packet filter
            data[11] = 0x07;
            // AEN control
            data[19] = 0x07;
            // VLAN filter count
            data[20] = 0x08;
            // Mixed filter count
            data[21] = 0x04;
            // Multicast filter count
            data[22] = 0x04;
            // Unicast filter count
            data[23] = 0x04;
            // Channel count
            data[27] = (byte)MaxChannels;
            return data;
        }

        private byte[] BuildGetParametersResponse()
        {
            // 28 bytes
            var data = new byte[28];
            data[0] = 0x01; // 1 MAC address
            data[3] = 0x01; // Unicast
            // MAC address at offset 14
            data[14] = MAC.A; data[15] = MAC.B; data[16] = MAC.C;
            data[17] = MAC.D; data[18] = MAC.E; data[19] = MAC.F;
            return data;
        }

        private byte[] BuildGetLinkStatusResponse()
        {
            // 4 bytes
            var data = new byte[4];
            if(linkUp)
            {
                data[3] = 0x29; // link up | 1G | full duplex
            }
            return data;
        }

        private byte[] BuildGetMacAddressResponse()
        {
            // 8 bytes: 2 reserved + 6 MAC
            var data = new byte[8];
            data[2] = MAC.A; data[3] = MAC.B; data[4] = MAC.C;
            data[5] = MAC.D; data[6] = MAC.E; data[7] = MAC.F;
            return data;
        }

        // State
        private bool initialStateCleared;
        private bool packageSelected;
        private byte selectedPackageId;
        private bool[] channelEnabled;
        private bool linkUp;
        private int lastResponseCode;
        private int lastReasonCode;
        private int lastInstanceId;
        private int lastCommandType;
        private int commandCount;
        private int responseCount;

        // Constants
        private const ushort NcsiEtherType = 0x88F8;
        private const int MaxChannels = 4;

        // NCSI command types (DSP0222)
        private const byte CmdClearInitialState = 0x00;
        private const byte CmdSelectPackage = 0x01;
        private const byte CmdDeselectPackage = 0x02;
        private const byte CmdEnableChannel = 0x03;
        private const byte CmdDisableChannel = 0x04;
        private const byte CmdResetChannel = 0x05;
        private const byte CmdGetVersionId = 0x08;
        private const byte CmdGetCapabilities = 0x09;
        private const byte CmdGetParameters = 0x0A;
        private const byte CmdGetLinkStatus = 0x0E;
        private const byte CmdGetMacAddress = 0x17;

        // Response codes (DSP0222)
        private const ushort RspCommandCompleted = 0x0000;
        private const ushort RspCommandFailed = 0x0001;
        private const ushort RspCommandUnsupported = 0x0002;

        // Reason codes
        private const ushort ReasonNoError = 0x0000;
        private const ushort ReasonInitRequired = 0x0003;
        private const ushort ReasonInvalidParam = 0x0004;
        private const ushort ReasonPkgNotSelected = 0x0005;
        private const ushort ReasonUnknownCmd = 0x7FFF;
    }
}
