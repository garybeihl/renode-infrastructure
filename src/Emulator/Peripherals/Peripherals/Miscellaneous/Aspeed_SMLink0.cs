//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT License.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    /// <summary>
    /// Birchstream SMLink0 — I2C host management bridge on bus 5.
    /// Simulates the Intel ME (Management Engine) sideband interface.
    ///
    /// Simics reference: birchstream_smlink0.py
    ///
    /// Provides a register-based interface for:
    /// - Host management commands (via SMLink0 protocol)
    /// - ME firmware version query
    /// - Host power state notification
    /// - Temperature/sensor polling
    ///
    /// Register layout (at configured sysbus address):
    ///   0x00: CTRL      — Control register
    ///   0x04: STATUS     — Status register (W1C)
    ///   0x08: CMD        — Command register
    ///   0x0C: DATA_IN    — Data input (host → BMC)
    ///   0x10: DATA_OUT   — Data output (BMC → host)
    ///   0x14: ME_VERSION — ME firmware version (read-only)
    ///   0x18: HOST_STATE — Host power state mirror
    ///   0x1C: TEMP       — Temperature sensor reading
    ///   0x20: CMD_TABLE  — Command response table base
    /// </summary>
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_SMLink0 : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_SMLink0()
        {
            commandResponses = new Dictionary<uint, uint>();
            Reset();
        }

        public long Size => 0x100;

        public GPIO IRQ { get; } = new GPIO();

        public void Reset()
        {
            ctrl = 0;
            status = 0;
            command = 0;
            dataIn = 0;
            dataOut = 0;
            meVersion = DefaultMeVersion;
            hostState = 0;
            temperature = DefaultTemperature;
            commandResponses.Clear();
            SetupDefaultResponses();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((uint)offset)
            {
                case R_CTRL:      return ctrl;
                case R_STATUS:    return status;
                case R_CMD:       return command;
                case R_DATA_IN:   return dataIn;
                case R_DATA_OUT:  return dataOut;
                case R_ME_VER:    return meVersion;
                case R_HOST_ST:   return hostState;
                case R_TEMP:      return temperature;
                default:          return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((uint)offset)
            {
                case R_CTRL:
                    ctrl = value;
                    if((ctrl & CtrlExecute) != 0)
                    {
                        ExecuteCommand();
                        ctrl &= ~CtrlExecute; // auto-clear
                    }
                    break;
                case R_STATUS:
                    status &= ~value; // W1C
                    UpdateIRQ();
                    break;
                case R_CMD:
                    command = value;
                    break;
                case R_DATA_IN:
                    dataIn = value;
                    break;
                case R_HOST_ST:
                    hostState = value;
                    this.Log(LogLevel.Debug, "SMLink0: Host state updated to 0x{0:X}", value);
                    break;
                case R_TEMP:
                    temperature = value;
                    break;
            }
        }

        /// <summary>
        /// Set ME firmware version (for test configuration).
        /// </summary>
        public void SetMeVersion(uint version)
        {
            meVersion = version;
        }

        /// <summary>
        /// Set temperature sensor reading (in 0.1°C units).
        /// </summary>
        public void SetTemperature(uint tempValue)
        {
            temperature = tempValue;
        }

        /// <summary>
        /// Add a command response mapping.
        /// When command 'cmd' is executed, DATA_OUT is set to 'response'.
        /// </summary>
        public void SetCommandResponse(uint cmd, uint response)
        {
            commandResponses[cmd] = response;
            this.Log(LogLevel.Debug, "SMLink0: Set response for cmd 0x{0:X} = 0x{1:X}", cmd, response);
        }

        /// <summary>
        /// Send a host management command (from test scripts).
        /// Sets CMD register, triggers execution, returns response.
        /// </summary>
        public uint SendCommand(uint cmd, uint data)
        {
            command = cmd;
            dataIn = data;
            ExecuteCommand();
            return dataOut;
        }

        private void ExecuteCommand()
        {
            this.Log(LogLevel.Debug, "SMLink0: Execute cmd=0x{0:X} data=0x{1:X}", command, dataIn);

            if(commandResponses.TryGetValue(command, out var response))
            {
                dataOut = response;
                status |= StatusCmdComplete;
            }
            else
            {
                // Unknown command — return error
                dataOut = 0xFFFFFFFF;
                status |= StatusCmdComplete | StatusError;
                this.Log(LogLevel.Warning, "SMLink0: Unknown command 0x{0:X}", command);
            }

            UpdateIRQ();
        }

        private void UpdateIRQ()
        {
            bool pending = (status & StatusCmdComplete) != 0 && (ctrl & CtrlIrqEn) != 0;
            IRQ.Set(pending);
        }

        private void SetupDefaultResponses()
        {
            // ME firmware version query
            commandResponses[CmdGetMeVersion] = meVersion;
            // Get host power state
            commandResponses[CmdGetHostState] = 0x00; // S0
            // Get temperature
            commandResponses[CmdGetTemperature] = temperature;
            // Heartbeat/ping
            commandResponses[CmdHeartbeat] = 0x01; // alive
        }

        private readonly Dictionary<uint, uint> commandResponses;
        private uint ctrl;
        private uint status;
        private uint command;
        private uint dataIn;
        private uint dataOut;
        private uint meVersion;
        private uint hostState;
        private uint temperature;

        // Default values (Birchstream Simics oracle)
        private const uint DefaultMeVersion = 0x0B050100; // ME 11.5.1.0
        private const uint DefaultTemperature = 350; // 35.0°C

        // Register offsets
        private const uint R_CTRL     = 0x00;
        private const uint R_STATUS   = 0x04;
        private const uint R_CMD      = 0x08;
        private const uint R_DATA_IN  = 0x0C;
        private const uint R_DATA_OUT = 0x10;
        private const uint R_ME_VER   = 0x14;
        private const uint R_HOST_ST  = 0x18;
        private const uint R_TEMP     = 0x1C;

        // Control bits
        private const uint CtrlExecute = 0x01;
        private const uint CtrlIrqEn   = 0x02;

        // Status bits (W1C)
        private const uint StatusCmdComplete = 0x01;
        private const uint StatusError       = 0x02;

        // Known commands
        private const uint CmdGetMeVersion   = 0x01;
        private const uint CmdGetHostState   = 0x02;
        private const uint CmdGetTemperature = 0x03;
        private const uint CmdHeartbeat      = 0x04;
    }
}