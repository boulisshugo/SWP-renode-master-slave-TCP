//
// SWPSlave.cs — the SWP slave (UICC side).
//
// Passive on voltage, exactly as the wire is: it never decides the link state,
// it only observes what the master drives and answers by modulating current
// (S2). Its two obligations under ETSI TS 102 613 are deadlines, not decisions:
// answer an ACTIVATE inside P6/P7, and answer a RESUME inside P3.
//
// Chip-agnostic and protocol-free. Received bytes are handed to whoever is
// listening — a firmware-facing peripheral, a Robot test, an optional TCP
// client — and nothing here inspects them. No SOF/EOF, no CRC, no HCI.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Antmicro.Migrant;
using Antmicro.Migrant.Hooks;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    /// <summary>
    /// A chip-agnostic SWP slave, and the base class for proprietary ones.
    /// Register it on a controller:
    ///
    ///     swp:  SWP.SWPController @ sysbus
    ///     uicc: SWP.SWPSlave @ swp
    ///
    /// Hook <see cref="DataReceived"/> to feed a firmware-facing peripheral, or
    /// give it a <c>port</c> to attach a second host client as the UICC end.
    ///
    /// EXTENDING THIS. Everything the master calls is virtual, and the seams a
    /// subclass needs are protected: <see cref="Master"/> to answer on,
    /// <see cref="SetState"/> to move this end's state, <see cref="Timer"/> to
    /// schedule on virtual time, and <see cref="FlushPendingToMaster"/> to drain
    /// what queued while the link was down. The pieces most derivatives replace
    /// are <see cref="AnswerActivation"/> and <see cref="AnswerResume"/> — the
    /// two points where a real part runs its own handshake — and
    /// <see cref="ReceiveFromMaster"/>.
    ///
    /// Override, never hide. The controller holds this object as an
    /// <see cref="ISWPSlave"/> and calls through the interface, so a `new`
    /// method would compile, read correctly and never run. See integrate.md.
    /// </summary>
    public class SWPSlave : ISWPSlave, IDisposable
    {
        /// <param name="port">
        /// Optional TCP port for the slave end of the link, so a host program
        /// can play the UICC. 0 (the default) means no socket: drive the slave
        /// from the Monitor, from Robot, or from a firmware-facing peripheral.
        /// </param>
        public SWPSlave(IMachine machine, int port = 0, bool autoStart = true)
        {
            Machine = machine;
            this.port = port;
            Timings = new SWPTimings();
            timer = new SWPTimer(machine, this, "swpSlave");
            state = SWPState.Deactivated;

            if(port != 0 && autoStart)
            {
                StartSocket();
            }
        }

        // ==============================================================
        //  Configuration
        // ==============================================================

        /// <summary>
        /// The slave's own view of P1..P7. Only the deadlines it must meet
        /// matter here (P3, P6, P7); the rest are kept so a test can read one
        /// consistent set of numbers off either end of the link.
        /// </summary>
        public SWPTimings Timings { get; }

        public ulong P1 { get => (ulong)Timings.P1.TotalMicroseconds; set => Timings.P1 = TimeInterval.FromMicroseconds(value); }
        public ulong P2 { get => (ulong)Timings.P2.TotalMicroseconds; set => Timings.P2 = TimeInterval.FromMicroseconds(value); }
        public ulong P3 { get => (ulong)Timings.P3.TotalMicroseconds; set => Timings.P3 = TimeInterval.FromMicroseconds(value); }
        public ulong P4 { get => (ulong)Timings.P4.TotalMicroseconds; set => Timings.P4 = TimeInterval.FromMicroseconds(value); }
        public ulong P5 { get => (ulong)Timings.P5.TotalMicroseconds; set => Timings.P5 = TimeInterval.FromMicroseconds(value); }
        public ulong P6 { get => (ulong)Timings.P6.TotalMicroseconds; set => Timings.P6 = TimeInterval.FromMicroseconds(value); }
        public ulong P7 { get => (ulong)Timings.P7.TotalMicroseconds; set => Timings.P7 = TimeInterval.FromMicroseconds(value); }

        /// <summary>
        /// How long this slave takes to answer an ACTIVATE, measured from the
        /// moment the master addresses it (the end of the master's P5). Must be
        /// under P6 for the link to come up — set it above P6 to test that the
        /// master fails the activation as it should.
        /// </summary>
        public ulong ActivationResponseTimeMicroseconds { get; set; } = 200;

        /// <summary>
        /// How long this slave takes to answer a RESUME with its transition
        /// sequence. Must be under P3. Set it above P3 to test the master's
        /// P3max handling.
        /// </summary>
        public ulong ResumeResponseTimeMicroseconds { get; set; } = 50;

        /// <summary>
        /// Keep received bursts in <see cref="ReceivedBursts"/> so a test can
        /// assert on them after the fact.
        /// </summary>
        public bool RecordTraffic { get; set; } = true;

        /// <summary>Cap on recorded bursts, so a long run cannot grow without bound.</summary>
        public int MaxRecordedBursts { get; set; } = 1024;

        // ==============================================================
        //  Link state
        // ==============================================================

        public SWPState State => state;

        /// <summary>Raised on the emulation thread when this end observes a new link state.</summary>
        public event Action<SWPStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Every burst arriving from the master, raw. This is the hook a
        /// firmware-facing peripheral uses; nothing here interprets the bytes.
        /// </summary>
        public event Action<byte[]> DataReceived;

        // ==============================================================
        //  ISWPSlave — driven by the master
        // ==============================================================

        public virtual void AttachMaster(ISWPMaster master)
        {
            this.master = master;
            this.Log(LogLevel.Debug, "Attached to an SWP master");
        }

        /// <summary>
        /// The master has settled the line and is addressing us (end of P5).
        /// We must answer inside P6, and inside the master's overall P7.
        /// </summary>
        public virtual void OnActivate()
        {
            timer.CancelAll();
            this.Log(LogLevel.Debug, "ACTIVATE observed; answering in {0}us (P6 is {1}us)",
                ActivationResponseTimeMicroseconds, Timings.P6.TotalMicroseconds);

            timer.Schedule(TimeInterval.FromMicroseconds(ActivationResponseTimeMicroseconds), AnswerActivation);
        }

        /// <summary>
        /// The moment this end answers an ACTIVATE, reached
        /// <see cref="ActivationResponseTimeMicroseconds"/> after the master
        /// addressed it. This is where a proprietary part runs whatever it
        /// really exchanges — ACT_SYNC, ACT_POWER_MODE, a bit-rate negotiation —
        /// before declaring itself ready.
        ///
        /// Whatever an override does, it must reach
        /// <c>Master.NotifySlaveActivated()</c> inside the master's P6 and P7 or
        /// the master will fail the activation, which is the behaviour under
        /// test. Delay by scheduling further steps on <see cref="Timer"/>; never
        /// block here, this runs on the emulation thread.
        /// </summary>
        protected virtual void AnswerActivation()
        {
            SetState(SWPState.Activated, SWPTransition.Activate);

            var m = master;
            if(m == null)
            {
                this.Log(LogLevel.Error, "No master attached; cannot answer the ACTIVATE");
                return;
            }
            m.NotifySlaveActivated();
            FlushPendingToMaster();
        }

        public virtual void OnDeactivate()
        {
            timer.CancelAll();
            lock(pendingLock)
            {
                pendingToMaster.Clear();
            }
            SetState(SWPState.Deactivated, SWPTransition.Deactivate);
        }

        public virtual void OnSuspend()
        {
            timer.CancelAll();
            SetState(SWPState.Suspended, SWPTransition.Suspend);
        }

        /// <summary>
        /// The master's P2 idle bits have ended and the link is ACTIVATED. We
        /// answer with our transition sequence, which must land inside P3max.
        /// </summary>
        public virtual void OnResume()
        {
            timer.CancelAll();
            SetState(SWPState.Activated, SWPTransition.Resume);

            this.Log(LogLevel.Debug, "RESUME observed; answering in {0}us (P3max is {1}us)",
                ResumeResponseTimeMicroseconds, Timings.P3.TotalMicroseconds);

            timer.Schedule(TimeInterval.FromMicroseconds(ResumeResponseTimeMicroseconds), AnswerResume);
        }

        /// <summary>
        /// The moment this end answers a RESUME with its transition sequence,
        /// reached <see cref="ResumeResponseTimeMicroseconds"/> after the
        /// master's P2 idle bits ended. An override must reach
        /// <c>Master.NotifySlaveResumed()</c> inside P3max, or the master
        /// reports the miss — the link stays ACTIVATED either way, since the
        /// master owns the state.
        /// </summary>
        protected virtual void AnswerResume()
        {
            var m = master;
            if(m == null)
            {
                this.Log(LogLevel.Error, "No master attached; cannot answer the RESUME");
                return;
            }
            m.NotifySlaveResumed();
            FlushPendingToMaster();
        }

        /// <summary>S1 voltage modulation, master -> slave. Opaque bytes.</summary>
        public virtual void ReceiveFromMaster(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            BytesFromMaster += (ulong)data.Length;
            this.Log(LogLevel.Noisy, "S1 master -> slave: {0}", SWPBytes.Hex(data));

            if(RecordTraffic)
            {
                lock(recordLock)
                {
                    received.Add(data);
                    while(received.Count > MaxRecordedBursts)
                    {
                        received.RemoveAt(0);
                    }
                }
            }

            var handler = DataReceived;
            handler?.Invoke(data);
        }

        // ==============================================================
        //  Slave -> master data
        // ==============================================================

        /// <summary>
        /// S2 current modulation, slave -> master. Full duplex, so this does
        /// not wait on anything travelling the other way.
        ///
        /// On a SUSPENDED link this asks the master to resume — which is what a
        /// UICC does on the wire when it has something to say — and the data
        /// goes out once the link is back. On a DEACTIVATED link it is dropped.
        /// </summary>
        public virtual void SendToMaster(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            var m = master;
            if(m == null)
            {
                this.Log(LogLevel.Warning, "Dropping {0} byte(s): no master attached", data.Length);
                Overruns++;
                return;
            }

            if(state == SWPState.Deactivated)
            {
                this.Log(LogLevel.Warning, "Dropping {0} byte(s) towards the master: link is DEACTIVATED", data.Length);
                Overruns++;
                return;
            }

            if(state == SWPState.Suspended)
            {
                this.Log(LogLevel.Debug, "Data to send on a SUSPENDED link; asking the master to resume");
                lock(pendingLock)
                {
                    pendingToMaster.Enqueue(data);
                }
                m.RequestResume();
                return;
            }

            BytesToMaster += (ulong)data.Length;
            m.ReceiveFromSlave(data);
        }

        /// <summary>
        /// Drain what queued while the link was down. The base calls this after
        /// answering an ACTIVATE or a RESUME; an override that replaces those
        /// answers is responsible for calling it.
        /// </summary>
        protected void FlushPendingToMaster()
        {
            List<byte[]> bursts;
            lock(pendingLock)
            {
                if(pendingToMaster.Count == 0)
                {
                    return;
                }
                bursts = pendingToMaster.ToList();
                pendingToMaster.Clear();
            }

            var m = master;
            if(m == null)
            {
                return;
            }

            foreach(var burst in bursts)
            {
                BytesToMaster += (ulong)burst.Length;
                m.ReceiveFromSlave(burst);
            }
        }

        // ==============================================================
        //  Monitor / test surface
        // ==============================================================

        /// <summary>
        /// <see cref="ISWPEndpoint.TransmitToPeer"/>: for the slave, the peer is
        /// the master.
        /// </summary>
        public void TransmitToPeer(byte[] data) => SendToMaster(data);

        /// <summary>
        /// Ask the master for the bit clock back. On a SUSPENDED link this is
        /// the S2 modulation a UICC uses to wake the CLF; in any other state the
        /// master ignores it, which is its call to make.
        /// </summary>
        public virtual void RequestResumeFromMaster()
        {
            var m = master;
            if(m == null)
            {
                this.Log(LogLevel.Warning, "Cannot request a RESUME: no master attached");
                return;
            }
            m.RequestResume();
        }

        /// <summary>`sysbus.uicc Send "6F 1A 84"` — modulate bytes back to the master.</summary>
        public void Send(string hexBytes)
        {
            var data = SWPBytes.ParseHex(hexBytes);
            if(data.Length == 0)
            {
                throw new RecoverableException("No bytes parsed from '" + hexBytes + "'");
            }
            Machine.HandleTimeDomainEvent<byte[]>(SendToMaster, data, false);
        }

        /// <summary>Bursts received from the master, oldest first, as hex strings.</summary>
        public IReadOnlyList<string> ReceivedBursts
        {
            get
            {
                lock(recordLock)
                {
                    return received.Select(SWPBytes.Hex).ToList();
                }
            }
        }

        /// <summary>`sysbus.uicc LastReceived` — the most recent burst, as hex. Empty if none.</summary>
        public string LastReceived()
        {
            lock(recordLock)
            {
                return received.Count == 0 ? string.Empty : SWPBytes.Hex(received[received.Count - 1]);
            }
        }

        /// <summary>`sysbus.uicc AllReceived` — every recorded burst, one per line.</summary>
        public string AllReceived()
        {
            lock(recordLock)
            {
                return string.Join(Environment.NewLine, received.Select(SWPBytes.Hex));
            }
        }

        public void ClearReceived()
        {
            lock(recordLock)
            {
                received.Clear();
            }
        }

        /// <summary>`sysbus.swp.uicc LinkState` — the state as a bare name, for scripts.</summary>
        public string LinkState() => state.ToString();

        /// <summary>`sysbus.uicc Status`</summary>
        public string Status()
        {
            var builder = new StringBuilder();
            builder.AppendFormat("state:      {0}{1}", state, Environment.NewLine);
            builder.AppendFormat("master:     {0}{1}", master == null ? "<not attached>" : "attached",
                Environment.NewLine);
            builder.AppendFormat("tcp port:   {0} ({1}){2}", port == 0 ? "disabled" : port.ToString(),
                clientConnected ? "client connected" : "no client", Environment.NewLine);
            builder.AppendFormat("timings:    {0}{1}", Timings, Environment.NewLine);
            builder.AppendFormat("answers:    activate in {0}us, resume in {1}us{2}",
                ActivationResponseTimeMicroseconds, ResumeResponseTimeMicroseconds, Environment.NewLine);
            builder.AppendFormat("bytes:      {0} -> master, {1} <- master, {2} dropped{3}",
                BytesToMaster, BytesFromMaster, Overruns, Environment.NewLine);
            return builder.ToString();
        }

        public ulong BytesToMaster { get; private set; }
        public ulong BytesFromMaster { get; private set; }
        public ulong Overruns { get; private set; }

        public virtual void Reset()
        {
            timer.CancelAll();
            lock(pendingLock)
            {
                pendingToMaster.Clear();
            }
            ClearReceived();
            BytesToMaster = 0;
            BytesFromMaster = 0;
            Overruns = 0;
            SetState(SWPState.Deactivated, SWPTransition.Deactivate);
        }

        // ==============================================================
        //  Optional TCP transport for the UICC end
        // ==============================================================

        public int Port => port;

        public bool IsClientConnected => clientConnected;

        public void StartSocket()
        {
            lock(socketLock)
            {
                if(socket != null)
                {
                    this.Log(LogLevel.Warning, "SWP slave socket is already listening on port {0}", port);
                    return;
                }
                if(port == 0)
                {
                    throw new RecoverableException("This slave was created with port 0; there is no socket to start");
                }

                var provider = new SocketServerProvider(telnetMode: false, flushOnConnect: false,
                    serverName: "swp-slave-" + port);
                provider.BufferSize = 4096;
                provider.ConnectionAccepted += _ =>
                {
                    clientConnected = true;
                    this.Log(LogLevel.Info, "Host client connected to the SWP slave on port {0}", port);
                };
                provider.ConnectionClosed += () =>
                {
                    clientConnected = false;
                    this.Log(LogLevel.Info, "Host client disconnected from the SWP slave on port {0}", port);
                };
                provider.DataBlockReceived += data =>
                {
                    if(data == null || data.Length == 0)
                    {
                        return;
                    }
                    var copy = new byte[data.Length];
                    Array.Copy(data, copy, data.Length);
                    Machine.HandleTimeDomainEvent<byte[]>(SendToMaster, copy, false);
                };
                provider.Start(port);

                socket = provider;
                DataReceived += TransmitToHost;
                this.Log(LogLevel.Info, "SWP slave listening for raw link data on TCP port {0}", port);
            }
        }

        public void StopSocket()
        {
            lock(socketLock)
            {
                if(socket == null)
                {
                    return;
                }
                DataReceived -= TransmitToHost;
                socket.Dispose();
                socket = null;
                clientConnected = false;
            }
        }

        private void TransmitToHost(byte[] data)
        {
            var provider = socket;
            if(provider == null || !clientConnected)
            {
                return;
            }
            provider.Send(data);
        }

        public virtual void Dispose()
        {
            timer.CancelAll();
            StopSocket();
        }

        // ==============================================================
        //  Internals
        // ==============================================================

        /// <summary>
        /// Move this end's view of the link and raise
        /// <see cref="StateChanged"/>. The slave never *decides* a state — the
        /// master does — so a subclass calls this to mirror what it observed,
        /// not to drive the wire.
        /// </summary>
        protected virtual void SetState(SWPState next, SWPTransition transition)
        {
            var previous = state;
            state = next;

            if(previous == next)
            {
                return;
            }

            this.Log(LogLevel.Info, "Slave observed link {0} -> {1} ({2})", previous, next, transition);

            var handler = StateChanged;
            handler?.Invoke(new SWPStateChangedEventArgs(previous, next, transition));
        }

        /// <summary>Schedule virtual-time steps from an override. Never block.</summary>
        public SWPTimer Timer => timer;

        /// <summary>
        /// The master, once attached, for a subclass to answer on:
        /// NotifySlaveActivated, NotifySlaveResumed, ReceiveFromSlave,
        /// RequestResume. Null before the first ACTIVATE.
        /// </summary>
        protected ISWPMaster Master => master;

        protected readonly IMachine Machine;

        private readonly int port;
        private readonly SWPTimer timer;
        private readonly List<byte[]> received = new List<byte[]>();
        private readonly Queue<byte[]> pendingToMaster = new Queue<byte[]>();
        private readonly object recordLock = new object();
        private readonly object pendingLock = new object();
        private readonly object socketLock = new object();

        private volatile SWPState state;
        private volatile ISWPMaster master;
        private volatile bool clientConnected;

        /// <summary>
        /// Sockets and threads do not serialize, so the field is dropped on
        /// save and rebuilt on load — otherwise `Load state` would restore a
        /// slave whose port silently no longer listens.
        /// </summary>
        [PostDeserialization]
        private void AfterLoad()
        {
            if(port != 0)
            {
                StartSocket();
            }
        }

        [Transient]
        private SocketServerProvider socket;
    }
}
