//
// SWPRegisterInterface.cs — a memory-mapped front-end over one end of an SWP link.
//
// The link model itself has no registers on purpose: SWPController and SWPSlave
// are pure link-layer and know nothing about any CPU. This peripheral is the
// chip-specific half — the thing firmware actually pokes — and it is kept
// separate so the link stays reusable across parts that expose it differently.
//
// It fronts *either* end: give it a controller and firmware plays the CLF, give
// it a slave and firmware plays the UICC. On an STM32H5 the real block is
// SWPMI, a master interface; the register layout here is deliberately its own
// simple thing rather than a claim to be register-compatible with any vendor IP.
//
// It binds to ISWPLinkControl / ISWPSlave, never to the shipped classes, so a
// proprietary slave keeps this block whether it derives from SWPSlave or
// implements ISWPSlave from scratch.
//
// Still no protocol layer. TDR takes a byte and puts it on the wire; RDR hands
// back a byte that came off it. Framing — SOF/EOF, CRC, bit stuffing, HCI — is
// the firmware's business at this end and the host client's at the other.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SWP
{
    /// <summary>
    /// Register map (all 32-bit):
    ///
    ///   0x00  CR       W  bit0 ACTIVATE, bit1 DEACTIVATE, bit2 SUSPEND, bit3 RESUME
    ///                     (write 1 to request; they are self-clearing)
    ///                  RW bit8 RXNEIE, bit9 LINKIE  — interrupt enables
    ///   0x04  SR       R  bits1:0 link state (0 DEACTIVATED, 1 SUSPENDED, 2 ACTIVATED)
    ///                     bit2 RXNE   — at least one byte waiting in RDR
    ///                     bit3 TXE    — always 1: the wire takes a byte whenever
    ///                                   the link is up
    ///                     bit4 LINKCH — link state changed since last cleared (w1c)
    ///                     bit5 OVR    — a received byte was dropped, RX FIFO full (w1c)
    ///   0x08  TDR      W  write one byte to transmit
    ///   0x0C  RDR      R  read one byte; pops the RX FIFO
    ///   0x10  RXLEVEL  R  bytes currently queued in the RX FIFO
    ///
    /// IRQ is asserted while (RXNE and RXNEIE) or (LINKCH and LINKIE).
    /// </summary>
    public class SWPRegisterInterface : IDoubleWordPeripheral, IKnownSize
    {
        /// <param name="controller">Front the master end. Mutually exclusive with <paramref name="slave"/>.</param>
        /// <param name="slave">
        /// Front the slave end. Mutually exclusive with <paramref name="controller"/>.
        /// Any ISWPSlave will do — SWPSlave, a subclass of it, or your own
        /// implementation.
        /// </param>
        /// <param name="rxFifoDepth">Bytes buffered before OVR is raised.</param>
        /// <remarks>
        /// The <paramref name="machine"/> parameter is unused but kept: Renode
        /// fills it in automatically when a peripheral is declared in a .repl,
        /// and dropping it would change how this one has to be written there.
        /// </remarks>
        public SWPRegisterInterface(IMachine machine, ISWPLinkControl controller = null, ISWPSlave slave = null,
            int rxFifoDepth = 256)
        {
            if((controller == null) == (slave == null))
            {
                throw new ConstructionException(
                    "SWPRegisterInterface fronts exactly one end of the link: pass either 'controller' or 'slave'");
            }

            this.controller = controller;
            this.slave = slave;
            this.endpoint = (ISWPEndpoint)controller ?? slave;
            this.rxFifoDepth = Math.Max(1, rxFifoDepth);

            // Both directions look the same from up here, which is the point of
            // ISWPEndpoint: one register block, either end of the link.
            endpoint.DataReceived += OnBytesReceived;
            endpoint.StateChanged += OnLinkStateChanged;

            registers = BuildRegisters();
        }

        public GPIO IRQ { get; } = new GPIO();

        public long Size => 0x400;

        public uint ReadDoubleWord(long offset) => registers.Read(offset);

        public void WriteDoubleWord(long offset, uint value) => registers.Write(offset, value);

        public void Reset()
        {
            lock(fifoLock)
            {
                rxFifo.Clear();
            }
            overrun = false;
            linkChanged = false;
            rxInterruptEnabled = false;
            linkInterruptEnabled = false;
            registers.Reset();
            UpdateInterrupt();
        }

        // --- registers ----------------------------------------------------------

        private DoubleWordRegisterCollection BuildRegisters()
        {
            var collection = new DoubleWordRegisterCollection(this);

            Registers.Control.Define(collection)
                .WithFlag(0, FieldMode.Write, name: "ACTIVATE",
                    writeCallback: (_, value) => { if(value) RequestTransition(SWPTransition.Activate); })
                .WithFlag(1, FieldMode.Write, name: "DEACTIVATE",
                    writeCallback: (_, value) => { if(value) RequestTransition(SWPTransition.Deactivate); })
                .WithFlag(2, FieldMode.Write, name: "SUSPEND",
                    writeCallback: (_, value) => { if(value) RequestTransition(SWPTransition.Suspend); })
                .WithFlag(3, FieldMode.Write, name: "RESUME",
                    writeCallback: (_, value) => { if(value) RequestTransition(SWPTransition.Resume); })
                .WithReservedBits(4, 4)
                .WithFlag(8, name: "RXNEIE",
                    valueProviderCallback: _ => rxInterruptEnabled,
                    writeCallback: (_, value) => { rxInterruptEnabled = value; UpdateInterrupt(); })
                .WithFlag(9, name: "LINKIE",
                    valueProviderCallback: _ => linkInterruptEnabled,
                    writeCallback: (_, value) => { linkInterruptEnabled = value; UpdateInterrupt(); })
                .WithReservedBits(10, 22);

            Registers.Status.Define(collection)
                .WithValueField(0, 2, FieldMode.Read, name: "STATE",
                    valueProviderCallback: _ => (ulong)CurrentState)
                .WithFlag(2, FieldMode.Read, name: "RXNE",
                    valueProviderCallback: _ => RxLevel > 0)
                // The link takes a byte whenever it is up, so there is no
                // transmit-side backpressure to report here.
                .WithFlag(3, FieldMode.Read, name: "TXE", valueProviderCallback: _ => true)
                .WithFlag(4, FieldMode.WriteOneToClear | FieldMode.Read, name: "LINKCH",
                    valueProviderCallback: _ => linkChanged,
                    writeCallback: (_, value) => { if(value) { linkChanged = false; UpdateInterrupt(); } })
                .WithFlag(5, FieldMode.WriteOneToClear | FieldMode.Read, name: "OVR",
                    valueProviderCallback: _ => overrun,
                    writeCallback: (_, value) => { if(value) overrun = false; })
                .WithReservedBits(6, 26);

            Registers.TransmitData.Define(collection)
                .WithValueField(0, 8, FieldMode.Write, name: "TDR",
                    writeCallback: (_, value) => Transmit((byte)value))
                .WithReservedBits(8, 24);

            Registers.ReceiveData.Define(collection)
                .WithValueField(0, 8, FieldMode.Read, name: "RDR",
                    valueProviderCallback: _ => PopReceived())
                .WithReservedBits(8, 24);

            Registers.ReceiveLevel.Define(collection)
                .WithValueField(0, 16, FieldMode.Read, name: "RXLEVEL",
                    valueProviderCallback: _ => (ulong)RxLevel)
                .WithReservedBits(16, 16);

            return collection;
        }

        // --- link plumbing --------------------------------------------------------

        private SWPState CurrentState => endpoint.State;

        private void RequestTransition(SWPTransition transition)
        {
            this.Log(LogLevel.Debug, "Firmware requested {0}", transition);

            if(controller == null)
            {
                // The slave is passive on voltage: it cannot decide the link
                // state. The one thing it may do is ask for a resume, which on
                // the wire is a current modulation while SUSPENDED.
                if(transition == SWPTransition.Resume)
                {
                    this.Log(LogLevel.Debug, "Slave end: forwarding the resume request to the master");
                    slave.RequestResumeFromMaster();
                }
                else
                {
                    this.Log(LogLevel.Warning,
                        "{0} ignored: this interface fronts the slave end, which does not drive the link state",
                        transition);
                }
                return;
            }

            switch(transition)
            {
                case SWPTransition.Activate:   controller.Activate();   break;
                case SWPTransition.Deactivate: controller.Deactivate(); break;
                case SWPTransition.Suspend:    controller.Suspend();    break;
                case SWPTransition.Resume:     controller.Resume();     break;
            }
        }

        private void Transmit(byte value)
        {
            endpoint.TransmitToPeer(new byte[] { value });
        }

        /// <summary>Runs on the emulation thread, from the link's data event.</summary>
        private void OnBytesReceived(byte[] data)
        {
            lock(fifoLock)
            {
                foreach(var b in data)
                {
                    if(rxFifo.Count >= rxFifoDepth)
                    {
                        overrun = true;
                        this.Log(LogLevel.Warning, "RX FIFO full ({0} bytes); dropping the rest of the burst",
                            rxFifoDepth);
                        break;
                    }
                    rxFifo.Enqueue(b);
                }
            }
            UpdateInterrupt();
        }

        private void OnLinkStateChanged(SWPStateChangedEventArgs args)
        {
            linkChanged = true;
            this.Log(LogLevel.Debug, "Link {0}; raising LINKCH", args);
            UpdateInterrupt();
        }

        private int RxLevel
        {
            get
            {
                lock(fifoLock)
                {
                    return rxFifo.Count;
                }
            }
        }

        private ulong PopReceived()
        {
            byte value;
            lock(fifoLock)
            {
                if(rxFifo.Count == 0)
                {
                    this.Log(LogLevel.Warning, "Read from RDR with an empty RX FIFO; returning 0");
                    return 0;
                }
                value = rxFifo.Dequeue();
            }

            // Outside the lock: draining the last byte clears RXNE, which can
            // drop the IRQ line.
            UpdateInterrupt();
            return value;
        }

        private void UpdateInterrupt()
        {
            var pending = (RxLevel > 0 && rxInterruptEnabled) || (linkChanged && linkInterruptEnabled);
            IRQ.Set(pending);
        }

        private enum Registers : long
        {
            Control = 0x00,
            Status = 0x04,
            TransmitData = 0x08,
            ReceiveData = 0x0C,
            ReceiveLevel = 0x10,
        }

        private readonly ISWPLinkControl controller;
        private readonly ISWPSlave slave;
        private readonly ISWPEndpoint endpoint;
        private readonly int rxFifoDepth;
        private readonly Queue<byte> rxFifo = new Queue<byte>();
        private readonly object fifoLock = new object();
        private readonly DoubleWordRegisterCollection registers;

        private bool rxInterruptEnabled;
        private bool linkInterruptEnabled;
        private volatile bool overrun;
        private volatile bool linkChanged;
    }
}
