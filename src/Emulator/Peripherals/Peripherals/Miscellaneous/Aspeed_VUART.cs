//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT License.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    /// <summary>
    /// AST2600 Virtual UART (VUART) — Serial Over LAN bridge.
    /// Routes host COM1 (I/O 0x3F8) to BMC UART9.
    ///
    /// Simics reference: birchstream_vuart.py
    ///
    /// Register layout matches AST2600 VUART at 0x1E787000:
    ///   0x00: VUART_RBRTHR — Receive Buffer / Transmit Holding Register
    ///   0x04: VUART_IER    — Interrupt Enable Register
    ///   0x08: VUART_IIR    — Interrupt Identification (read) / FIFO Control (write)
    ///   0x0C: VUART_LCR    — Line Control Register
    ///   0x10: VUART_MCR    — Modem Control Register
    ///   0x14: VUART_LSR    — Line Status Register
    ///   0x18: VUART_MSR    — Modem Status Register
    ///   0x1C: VUART_SPR    — Scratch Pad Register
    ///   0x20: VUART_ADDL   — Additional Control Register Low
    ///   0x24: VUART_ADDH   — Additional Control Register High
    ///   0x28: VUART_TX_CNT — TX FIFO Count
    ///   0x2C: VUART_RX_CNT — RX FIFO Count
    ///
    /// VUART_ADDL bits:
    ///   [7:0] SIRQ number (default 4 for COM1/IRQ4)
    ///   [15:8] Host I/O port low byte
    /// VUART_ADDH bits:
    ///   [7:0] Host I/O port high byte (0x03F8 = COM1)
    ///   [8] TX disable
    ///   [9] RX disable
    /// </summary>
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_VUART : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_VUART()
        {
            rxFifo = new Queue<byte>();
            txFifo = new Queue<byte>();
            Reset();
        }

        public long Size => 0x100;

        public GPIO IRQ { get; } = new GPIO();

        public void Reset()
        {
            rxFifo.Clear();
            txFifo.Clear();
            ier = 0;
            lcr = 0x03; // 8-N-1 default
            mcr = 0;
            scratchPad = 0;
            addlLow = 0x04;  // SIRQ 4 (COM1)
            addlHigh = 0x03; // I/O port 0x03F8 high byte
            hostIoPortLow = 0xF8; // I/O port low byte
            txDisable = false;
            rxDisable = false;
            UpdateIRQ();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((uint)offset)
            {
                case R_RBRTHR:
                    if((lcr & 0x80) != 0) // DLAB
                        return divisorLow;
                    if(rxFifo.Count > 0)
                        return rxFifo.Dequeue();
                    return 0;
                case R_IER:
                    if((lcr & 0x80) != 0) // DLAB
                        return divisorHigh;
                    return ier;
                case R_IIR:
                    return GetIIR();
                case R_LCR:
                    return lcr;
                case R_MCR:
                    return mcr;
                case R_LSR:
                    return GetLSR();
                case R_MSR:
                    return 0xB0; // CTS + DSR + DCD asserted
                case R_SPR:
                    return scratchPad;
                case R_ADDL:
                    return (uint)(hostIoPortLow << 8) | addlLow;
                case R_ADDH:
                    uint val = addlHigh;
                    if(txDisable) val |= 0x100;
                    if(rxDisable) val |= 0x200;
                    return val;
                case R_TX_CNT:
                    return (uint)txFifo.Count;
                case R_RX_CNT:
                    return (uint)rxFifo.Count;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((uint)offset)
            {
                case R_RBRTHR:
                    if((lcr & 0x80) != 0) // DLAB
                    {
                        divisorLow = value & 0xFF;
                        return;
                    }
                    if(!txDisable)
                    {
                        txFifo.Enqueue((byte)(value & 0xFF));
                        this.Log(LogLevel.Debug, "VUART: TX 0x{0:X2} '{1}'", value & 0xFF,
                            (char)(value & 0xFF) >= ' ' ? (char)(value & 0xFF) : '.');
                    }
                    UpdateIRQ();
                    break;
                case R_IER:
                    if((lcr & 0x80) != 0) // DLAB
                    {
                        divisorHigh = value & 0xFF;
                        return;
                    }
                    ier = value & 0x0F;
                    UpdateIRQ();
                    break;
                case R_IIR: // FCR (write)
                    if((value & 0x02) != 0) rxFifo.Clear();
                    if((value & 0x04) != 0) txFifo.Clear();
                    break;
                case R_LCR:
                    lcr = value;
                    break;
                case R_MCR:
                    mcr = value;
                    break;
                case R_SPR:
                    scratchPad = value;
                    break;
                case R_ADDL:
                    addlLow = value & 0xFF;
                    hostIoPortLow = (value >> 8) & 0xFF;
                    break;
                case R_ADDH:
                    addlHigh = value & 0xFF;
                    txDisable = (value & 0x100) != 0;
                    rxDisable = (value & 0x200) != 0;
                    break;
            }
        }

        /// <summary>
        /// Inject a byte from the host side (COM1 TX → BMC RX).
        /// Used by host stub scripts to simulate host serial output.
        /// </summary>
        public void InjectHostByte(byte data)
        {
            if(!rxDisable)
            {
                rxFifo.Enqueue(data);
                this.Log(LogLevel.Debug, "VUART: Host inject 0x{0:X2} '{1}'", data,
                    (char)data >= ' ' ? (char)data : '.');
                UpdateIRQ();
            }
        }

        /// <summary>
        /// Inject a string from the host side.
        /// </summary>
        public void InjectHostString(string text)
        {
            foreach(char c in text)
            {
                InjectHostByte((byte)c);
            }
        }

        /// <summary>
        /// Read a byte from the TX FIFO (BMC TX → host RX).
        /// Returns -1 if empty.
        /// </summary>
        public int ReadHostByte()
        {
            if(txFifo.Count > 0)
                return txFifo.Dequeue();
            return -1;
        }

        /// <summary>
        /// Read all available bytes from TX FIFO as a string.
        /// </summary>
        public string ReadHostString()
        {
            var chars = new char[txFifo.Count];
            for(int i = 0; i < chars.Length; i++)
                chars[i] = (char)txFifo.Dequeue();
            return new string(chars);
        }

        private uint GetLSR()
        {
            uint lsr = 0x60; // THRE + TEMT (TX empty)
            if(rxFifo.Count > 0)
                lsr |= 0x01; // Data Ready
            return lsr;
        }

        private uint GetIIR()
        {
            if((ier & 0x01) != 0 && rxFifo.Count > 0)
                return 0x04; // RX data available
            if((ier & 0x02) != 0 && txFifo.Count == 0)
                return 0x02; // TX holding empty
            return 0x01; // No interrupt pending
        }

        private void UpdateIRQ()
        {
            bool pending = false;
            if((ier & 0x01) != 0 && rxFifo.Count > 0)
                pending = true;
            if((ier & 0x02) != 0 && txFifo.Count == 0)
                pending = true;
            IRQ.Set(pending);
        }

        private readonly Queue<byte> rxFifo;
        private readonly Queue<byte> txFifo;
        private uint ier;
        private uint lcr;
        private uint mcr;
        private uint scratchPad;
        private uint addlLow;
        private uint addlHigh;
        private uint hostIoPortLow;
        private bool txDisable;
        private bool rxDisable;
        private uint divisorLow;
        private uint divisorHigh;

        private const uint R_RBRTHR  = 0x00;
        private const uint R_IER     = 0x04;
        private const uint R_IIR     = 0x08;
        private const uint R_LCR     = 0x0C;
        private const uint R_MCR     = 0x10;
        private const uint R_LSR     = 0x14;
        private const uint R_MSR     = 0x18;
        private const uint R_SPR     = 0x1C;
        private const uint R_ADDL    = 0x20;
        private const uint R_ADDH    = 0x24;
        private const uint R_TX_CNT  = 0x28;
        private const uint R_RX_CNT  = 0x2C;
    }
}