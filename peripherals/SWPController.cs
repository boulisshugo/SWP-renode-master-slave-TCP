//
// SWPController.cs — the SWP master (CLF side), with a raw TCP socket.
//
// Owns the link: it is the only side that drives voltage, so it is the only
// side that decides when the link is ACTIVATED, SUSPENDED or DEACTIVATED, and
// every P1..P7 deadline is measured here.
//
// The socket carries raw bytes in both directions and nothing else. Whatever
// the host client writes is transmitted to the slave verbatim; whatever the
// slave modulates back is written to the host verbatim. No SOF/EOF, no CRC, no
// bit stuffing, no HCI — framing belongs to the host client and to the slave's
// firmware, not to this peripheral.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Antmicro.Migrant;
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
    /// A chip-agnostic SWP master. Register exactly one slave on it:
    ///
    ///     swp:      SWP.SWPController @ sysbus
    ///         port: 3460
    ///     uicc:     SWP.SWPSlave @ swp
    ///
    /// and connect any host program to 127.0.0.1:3460 to drive the link.
    /// </summary>
    public class SWPController :
        NullRegistrationPointPeripheralContainer<ISWPSlave>,
        ISWPMaster,
        IDisposable
    {
        /// <param name="port">
        /// TCP port for raw link data. 0 disables the socket entirely, for
        /// tests that drive the link from the Monitor or from Robot only.
        /// </param>
        /// <param name="autoStart">Open the socket in the constructor.</param>
        public SWPController(IMachine machine, int port = 3460, bool autoStart = true)
            : base(machine)
        {
            this.port = port;
            Timings = new SWPTimings();
            timer = new SWPTimer(machine, this, "swpController");
            state = SWPState.Deactivated;

            if(port != 0 && autoStart)
            {
                StartSocket();
            }
        }

        // ==============================================================
        //  Configuration — everything chip-specific lives here
        // ==============================================================

        /// <summary>P1..P7. Settable from a .repl; read at the moment each deadline is armed.</summary>
        public SWPTimings Timings { get; }

        // Individual accessors, because .repl can only set flat properties.
        // Values are in microseconds.
        public ulong P1 { get => (ulong)Timings.P1.TotalMicroseconds; set => Timings.P1 = TimeInterval.FromMicroseconds(value); }
        public ulong P2 { get => (ulong)Timings.P2.TotalMicroseconds; set => Timings.P2 = TimeInterval.FromMicroseconds(value); }
        public ulong P3 { get => (ulong)Timings.P3.TotalMicroseconds; set => Timings.P3 = TimeInterval.FromMicroseconds(value); }
        public ulong P4 { get => (ulong)Timings.P4.TotalMicroseconds; set => Timings.P4 = TimeInterval.FromMicroseconds(value); }
        public ulong P5 { get => (ulong)Timings.P5.TotalMicroseconds; set => Timings.P5 = TimeInterval.FromMicroseconds(value); }
        public ulong P6 { get => (ulong)Timings.P6.TotalMicroseconds; set => Timings.P6 = TimeInterval.FromMicroseconds(value); }
        public ulong P7 { get => (ulong)Timings.P7.TotalMicroseconds; set => Timings.P7 = TimeInterval.FromMicroseconds(value); }

        /// <summary>
        /// Link bit rate, used to charge transmission time to a byte burst so
        /// data lands at a plausible point in virtual time rather than
        /// instantly. SWP negotiates up to roughly 1.7 Mbit/s at activation.
        /// Set 0 to deliver with no transmission delay.
        /// </summary>
        public uint BitRate { get; set; } = 1695000;

        /// <summary>
        /// Switch the link to SUSPENDED once nothing but idle bits has been on
        /// the wire for P1, as the spec permits the master to do. Off means the
        /// link stays ACTIVATED until something suspends it explicitly.
        /// </summary>
        public bool AutoSuspend { get; set; } = true;

        /// <summary>
        /// Resume a SUSPENDED link automatically when there is data to send,
        /// instead of dropping the data. This is what a CLF does in practice;
        /// turn it off to test that a client respects the link state itself.
        /// </summary>
        public bool AutoResumeOnTransmit { get; set; } = true;

        // ==============================================================
        //  Link state
        // ==============================================================

        public SWPState State => state;

        /// <summary>Raised on the emulation thread after the link settles in a new state.</summary>
        public event Action<SWPStateChangedEventArgs> StateChanged;

        /// <summary>Raised when the slave misses P6/P7 during ACTIVATE. The link stays DEACTIVATED.</summary>
        public event Action ActivationFailed;

        /// <summary>Raised when the slave misses P3 during RESUME.</summary>
        public event Action ResumeTimedOut;

        /// <summary>Raised for every burst modulated back by the slave, before it reaches the socket.</summary>
        public event Action<byte[]> DataFromSlave;

        // ==============================================================
        //  Transitions
        // ==============================================================

        /// <summary>
        /// ACTIVATE: DEACTIVATED -> ACTIVATED.
        ///
        ///   t0        the master starts driving the wire, leaving state L
        ///   t0 + P5   the line has settled and the slave is told to activate
        ///   ...       the slave must answer inside P6 (from t0+P5) and inside
        ///             P7 (from t0); whichever expires first fails the sequence
        /// </summary>
        public void Activate()
        {
            if(state == SWPState.Activated)
            {
                this.Log(LogLevel.Debug, "ACTIVATE ignored: link is already ACTIVATED");
                return;
            }
            if(activating)
            {
                this.Log(LogLevel.Debug, "ACTIVATE ignored: an activation is already in progress");
                return;
            }
            if(state == SWPState.Suspended)
            {
                // Nothing to activate — the link is already up, just idle.
                this.Log(LogLevel.Debug, "ACTIVATE on a SUSPENDED link is a RESUME");
                Resume();
                return;
            }

            var slave = RegisteredPeripheral;
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "ACTIVATE with no slave registered on the link; staying DEACTIVATED");
                RaiseActivationFailed();
                return;
            }

            timer.CancelAll();
            activating = true;
            slaveAnswered = false;
            this.Log(LogLevel.Debug, "ACTIVATE started ({0})", Timings);

            // P7 is the overall budget, armed from t0 so a long P5 cannot hide
            // a slave that never answers.
            timer.Schedule(Timings.P7, () =>
            {
                if(activating && !slaveAnswered)
                {
                    this.Log(LogLevel.Warning, "ACTIVATE failed: P7 ({0}us) expired before the slave answered",
                        Timings.P7.TotalMicroseconds);
                    AbortActivation();
                }
            });

            timer.Schedule(Timings.P5, () =>
            {
                if(!activating)
                {
                    return;
                }
                this.Log(LogLevel.Noisy, "ACTIVATE: P5 elapsed, line settled; addressing the slave");
                AttachSlave(slave);
                slave.OnActivate();

                timer.Schedule(Timings.P6, () =>
                {
                    if(activating && !slaveAnswered)
                    {
                        this.Log(LogLevel.Warning, "ACTIVATE failed: P6 ({0}us) expired with no answer from the slave",
                            Timings.P6.TotalMicroseconds);
                        AbortActivation();
                    }
                });
            });
        }

        /// <summary>
        /// DEACTIVATE: any state -> DEACTIVATED. The master holds SWIO in state
        /// L; after P4 the link is observably DEACTIVATED and the slave is told.
        /// </summary>
        public void Deactivate()
        {
            if(state == SWPState.Deactivated && !activating)
            {
                this.Log(LogLevel.Debug, "DEACTIVATE ignored: link is already DEACTIVATED");
                return;
            }

            timer.CancelAll();
            activating = false;
            slaveAnswered = false;
            CancelIdleWatch();

            this.Log(LogLevel.Debug, "DEACTIVATE started: holding SWIO low for P4 ({0}us)",
                Timings.P4.TotalMicroseconds);

            timer.Schedule(Timings.P4, () =>
            {
                var slave = RegisteredPeripheral;
                if(slave != null)
                {
                    slave.OnDeactivate();
                }
                SetState(SWPState.Deactivated, SWPTransition.Deactivate);
            });
        }

        /// <summary>
        /// SUSPEND: ACTIVATED -> SUSPENDED. The master stops the bit clock and
        /// holds S1 high. Called automatically after P1 of inactivity when
        /// <see cref="AutoSuspend"/> is set, or explicitly at any time.
        /// </summary>
        public void Suspend()
        {
            if(state != SWPState.Activated)
            {
                this.Log(LogLevel.Debug, "SUSPEND ignored: link is {0}, not ACTIVATED", state);
                return;
            }

            timer.CancelAll();
            CancelIdleWatch();

            var slave = RegisteredPeripheral;
            if(slave != null)
            {
                slave.OnSuspend();
            }
            SetState(SWPState.Suspended, SWPTransition.Suspend);
        }

        /// <summary>
        /// RESUME: SUSPENDED -> ACTIVATED. The master sends a transition
        /// sequence followed by P2 idle bits; the link is ACTIVATED at the end
        /// of the last of them. The slave answers with its own transition
        /// sequence, which must arrive inside P3.
        /// </summary>
        public void Resume()
        {
            if(state == SWPState.Activated)
            {
                this.Log(LogLevel.Debug, "RESUME ignored: link is already ACTIVATED");
                return;
            }
            if(state == SWPState.Deactivated)
            {
                this.Log(LogLevel.Warning, "RESUME on a DEACTIVATED link is not possible; ACTIVATE it first");
                return;
            }

            timer.CancelAll();
            slaveAnswered = false;
            this.Log(LogLevel.Debug, "RESUME started: transition sequence + P2 ({0}us) idle bits",
                Timings.P2.TotalMicroseconds);

            timer.Schedule(Timings.P2, () =>
            {
                // End of the last idle bit: the link is ACTIVATED here, whether
                // or not the slave has answered yet.
                SetState(SWPState.Activated, SWPTransition.Resume);

                var slave = RegisteredPeripheral;
                if(slave != null)
                {
                    slave.OnResume();
                }

                timer.Schedule(Timings.P3, () =>
                {
                    if(!slaveAnswered)
                    {
                        this.Log(LogLevel.Warning,
                            "RESUME: P3max ({0}us) expired with no transition sequence from the slave",
                            Timings.P3.TotalMicroseconds);
                        var handler = ResumeTimedOut;
                        handler?.Invoke();
                    }
                });

                ArmIdleWatch();
                FlushPendingToSlave();
            });
        }

        // ==============================================================
        //  ISWPMaster — what the slave calls back
        // ==============================================================

        public void NotifySlaveActivated()
        {
            if(!activating)
            {
                this.Log(LogLevel.Debug, "Slave reported activation outside an ACTIVATE sequence; ignoring");
                return;
            }

            slaveAnswered = true;
            activating = false;
            timer.CancelAll();
            this.Log(LogLevel.Debug, "ACTIVATE complete: slave answered inside P6/P7");

            SetState(SWPState.Activated, SWPTransition.Activate);
            ArmIdleWatch();
            FlushPendingToSlave();
        }

        public void NotifySlaveResumed()
        {
            slaveAnswered = true;
            this.Log(LogLevel.Noisy, "RESUME: slave answered with its transition sequence inside P3");
        }

        public void RequestResume()
        {
            if(state != SWPState.Suspended)
            {
                this.Log(LogLevel.Debug, "Slave asked to resume a link that is {0}; ignoring", state);
                return;
            }
            this.Log(LogLevel.Debug, "Slave requested RESUME by modulating S2 while SUSPENDED");
            Resume();
        }

        /// <summary>
        /// S2 current modulation, slave -> master. Full duplex: this is
        /// independent of anything travelling the other way, and is passed
        /// through to the host client byte for byte.
        /// </summary>
        public void ReceiveFromSlave(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }
            if(state != SWPState.Activated)
            {
                this.Log(LogLevel.Warning, "Discarding {0} byte(s) from the slave: link is {1}", data.Length, state);
                return;
            }

            BytesFromSlave += (ulong)data.Length;
            NoteActivity();

            this.Log(LogLevel.Noisy, "S2 slave -> master: {0}", SWPBytes.Hex(data));

            var handler = DataFromSlave;
            handler?.Invoke(data);

            TransmitToHost(data);
        }

        // ==============================================================
        //  Master -> slave data
        // ==============================================================

        /// <summary>
        /// S1 voltage modulation, master -> slave. Bytes are opaque; the link
        /// charges them transmission time at <see cref="BitRate"/> and delivers
        /// the burst whole.
        ///
        /// On a SUSPENDED link this resumes first and sends afterwards when
        /// <see cref="AutoResumeOnTransmit"/> is set; on a DEACTIVATED link the
        /// data is dropped, because there is no wire to put it on.
        /// </summary>
        public void SendToSlave(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            if(state == SWPState.Deactivated)
            {
                this.Log(LogLevel.Warning, "Dropping {0} byte(s) towards the slave: link is DEACTIVATED", data.Length);
                Overruns++;
                return;
            }

            if(state == SWPState.Suspended)
            {
                if(!AutoResumeOnTransmit)
                {
                    this.Log(LogLevel.Warning, "Dropping {0} byte(s): link is SUSPENDED and AutoResumeOnTransmit is off",
                        data.Length);
                    Overruns++;
                    return;
                }
                this.Log(LogLevel.Debug, "Data to send on a SUSPENDED link; resuming first");
                lock(pendingLock)
                {
                    pendingToSlave.Enqueue(data);
                }
                Resume();
                return;
            }

            DeliverToSlave(data);
        }

        /// <summary>`sysbus.swp Transmit "00 A4 04 00"` — inject master -> slave data by hand.</summary>
        public void Transmit(string hexBytes)
        {
            var data = SWPBytes.ParseHex(hexBytes);
            if(data.Length == 0)
            {
                throw new RecoverableException("No bytes parsed from '" + hexBytes + "'");
            }
            Machine.HandleTimeDomainEvent<byte[]>(SendToSlave, data, false);
        }

        private void DeliverToSlave(byte[] data)
        {
            var slave = RegisteredPeripheral;
            if(slave == null)
            {
                this.Log(LogLevel.Warning, "Dropping {0} byte(s): no slave registered", data.Length);
                Overruns++;
                return;
            }

            BytesToSlave += (ulong)data.Length;
            NoteActivity();

            this.Log(LogLevel.Noisy, "S1 master -> slave: {0}", SWPBytes.Hex(data));

            var flightTime = TransmissionTime(data.Length);
            if(flightTime == TimeInterval.Empty)
            {
                slave.ReceiveFromMaster(data);
                return;
            }

            timer.Schedule(flightTime, () =>
            {
                if(state != SWPState.Activated)
                {
                    this.Log(LogLevel.Warning, "Link left ACTIVATED mid-burst; {0} byte(s) lost", data.Length);
                    return;
                }
                slave.ReceiveFromMaster(data);
            });
        }

        private void FlushPendingToSlave()
        {
            List<byte[]> bursts;
            lock(pendingLock)
            {
                if(pendingToSlave.Count == 0)
                {
                    return;
                }
                bursts = pendingToSlave.ToList();
                pendingToSlave.Clear();
            }

            foreach(var burst in bursts)
            {
                DeliverToSlave(burst);
            }
        }

        /// <summary>Time on the wire for a burst, from the negotiated bit rate.</summary>
        private TimeInterval TransmissionTime(int byteCount)
        {
            var rate = BitRate;
            if(rate == 0)
            {
                return TimeInterval.Empty;
            }
            // Charge 10 bit periods per byte: SWP's pulse-position encoding plus
            // framing overhead costs meaningfully more than 8 bare bits.
            var microseconds = (ulong)Math.Max(1.0, (byteCount * 10.0 * 1000000.0) / rate);
            return TimeInterval.FromMicroseconds(microseconds);
        }

        // ==============================================================
        //  P1 inactivity watch
        // ==============================================================

        private void NoteActivity()
        {
            Interlocked.Increment(ref activityCounter);
            ArmIdleWatch();
        }

        private void ArmIdleWatch()
        {
            if(!AutoSuspend || state != SWPState.Activated)
            {
                return;
            }

            var expected = Interlocked.Read(ref activityCounter);
            idleWatchArmed = true;

            // Deliberately not on `timer`: a transition cancels `timer`, and the
            // idle watch must survive data transfers that reschedule it.
            Machine.ScheduleAction(Timings.P1, _ =>
            {
                if(!idleWatchArmed || state != SWPState.Activated)
                {
                    return;
                }
                if(Interlocked.Read(ref activityCounter) != expected)
                {
                    return;  // something moved on the wire; a later watch is armed
                }
                this.Log(LogLevel.Debug, "P1 ({0}us) of idle bits elapsed; suspending the link",
                    Timings.P1.TotalMicroseconds);
                Suspend();
            }, "swpIdleWatch");
        }

        private void CancelIdleWatch()
        {
            idleWatchArmed = false;
            Interlocked.Increment(ref activityCounter);
        }

        // ==============================================================
        //  Registration
        // ==============================================================

        public override void Register(ISWPSlave peripheral, NullRegistrationPoint registrationPoint)
        {
            base.Register(peripheral, registrationPoint);
            AttachSlave(peripheral);
            this.Log(LogLevel.Debug, "Slave registered on the SWP link");
        }

        public override void Unregister(ISWPSlave peripheral)
        {
            base.Unregister(peripheral);
            attachedSlave = null;
        }

        private void AttachSlave(ISWPSlave slave)
        {
            if(ReferenceEquals(attachedSlave, slave))
            {
                return;
            }
            slave.AttachMaster(this);
            attachedSlave = slave;
        }

        // ==============================================================
        //  TCP transport
        // ==============================================================

        public int Port => port;

        public bool IsClientConnected => clientConnected;

        /// <summary>Bytes dropped because the link was down or the host queue was full.</summary>
        public ulong Overruns { get; private set; }

        public ulong BytesToSlave { get; private set; }
        public ulong BytesFromSlave { get; private set; }

        public void StartSocket()
        {
            lock(socketLock)
            {
                if(socket != null)
                {
                    this.Log(LogLevel.Warning, "SWP socket is already listening on port {0}", port);
                    return;
                }
                if(port == 0)
                {
                    throw new RecoverableException("This controller was created with port 0; there is no socket to start");
                }

                // telnetMode: false — the stream is raw link data, and Telnet
                // would escape 0xFF and corrupt it.
                var provider = new SocketServerProvider(telnetMode: false, flushOnConnect: false,
                    serverName: "swp-" + port);
                provider.BufferSize = 4096;
                provider.ConnectionAccepted += OnClientConnected;
                provider.ConnectionClosed += OnClientDisconnected;
                provider.DataBlockReceived += OnHostData;
                provider.Start(port);

                socket = provider;
                this.Log(LogLevel.Info, "SWP controller listening for raw link data on TCP port {0}", port);
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
                socket.Dispose();
                socket = null;
                clientConnected = false;
                this.Log(LogLevel.Info, "SWP controller socket on port {0} closed", port);
            }
        }

        private void OnClientConnected(Stream stream)
        {
            clientConnected = true;
            this.Log(LogLevel.Info, "Host client connected to the SWP controller on port {0}", port);
        }

        private void OnClientDisconnected()
        {
            clientConnected = false;
            this.Log(LogLevel.Info, "Host client disconnected from the SWP controller on port {0}", port);
        }

        /// <summary>
        /// Runs on the socket reader thread. It does no work beyond copying the
        /// buffer and handing it to the emulation thread — the provider reuses
        /// its read buffer, so the copy is not optional.
        /// </summary>
        private void OnHostData(byte[] data)
        {
            if(data == null || data.Length == 0)
            {
                return;
            }

            var copy = new byte[data.Length];
            Array.Copy(data, copy, data.Length);

            Machine.HandleTimeDomainEvent<byte[]>(SendToSlave, copy, false);
        }

        /// <summary>
        /// Emulation thread. Never blocks — Send only enqueues.
        ///
        /// The queue behind Send is unbounded, so the one case that would leak
        /// for a whole simulation is a chatty link with nobody attached; that
        /// is what the check below prevents, and it is the case that actually
        /// happens. A peer that stays connected but stops reading still backs
        /// up behind the writer thread, and there is no honest guard for that
        /// from here: SocketServerProvider exposes no hook for when its writer
        /// drains, so any byte counter kept on this side would be a guess
        /// dressed up as backpressure. Dropped bytes are counted in
        /// <see cref="Overruns"/> either way.
        /// </summary>
        private void TransmitToHost(byte[] data)
        {
            var provider = socket;
            if(provider == null)
            {
                return;
            }
            if(!clientConnected)
            {
                this.Log(LogLevel.Debug, "No host client attached; dropping {0} byte(s) from the slave", data.Length);
                Overruns++;
                return;
            }

            // One Send per burst — a SendByte loop would become one write() per
            // byte on the host side.
            provider.Send(data);
        }

        // ==============================================================
        //  Monitor surface
        // ==============================================================

        /// <summary>
        /// `sysbus.swp LinkState` — the state as a bare name. Reading the State
        /// property from the Monitor appends the enum's help text, which is
        /// useful interactively and useless to a script.
        /// </summary>
        public string LinkState() => state.ToString();

        /// <summary>`sysbus.swp Status`</summary>
        public string Status()
        {
            var builder = new StringBuilder();
            builder.AppendFormat("state:      {0}{1}", state, Environment.NewLine);
            builder.AppendFormat("slave:      {0}{1}",
                RegisteredPeripheral == null ? "<none registered>" : RegisteredPeripheral.GetType().Name,
                Environment.NewLine);
            builder.AppendFormat("tcp port:   {0} ({1}){2}", port == 0 ? "disabled" : port.ToString(),
                clientConnected ? "client connected" : "no client", Environment.NewLine);
            builder.AppendFormat("bit rate:   {0} bit/s{1}", BitRate, Environment.NewLine);
            builder.AppendFormat("timings:    {0}{1}", Timings, Environment.NewLine);
            builder.AppendFormat("autoSuspend {0}, autoResumeOnTransmit {1}{2}",
                AutoSuspend, AutoResumeOnTransmit, Environment.NewLine);
            builder.AppendFormat("bytes:      {0} -> slave, {1} <- slave, {2} dropped{3}",
                BytesToSlave, BytesFromSlave, Overruns, Environment.NewLine);
            return builder.ToString();
        }

        public override void Reset()
        {
            timer.CancelAll();
            CancelIdleWatch();
            activating = false;
            slaveAnswered = false;
            Overruns = 0;
            BytesToSlave = 0;
            BytesFromSlave = 0;
            lock(pendingLock)
            {
                pendingToSlave.Clear();
            }
            // A reset must not disconnect the host, matching how socket
            // terminals behave — only the link goes down.
            SetState(SWPState.Deactivated, SWPTransition.Deactivate);
        }

        public virtual void Dispose()
        {
            timer.CancelAll();
            StopSocket();
        }

        // ==============================================================
        //  Internals
        // ==============================================================

        private void AbortActivation()
        {
            activating = false;
            slaveAnswered = false;
            timer.CancelAll();

            var slave = RegisteredPeripheral;
            if(slave != null)
            {
                slave.OnDeactivate();
            }
            SetState(SWPState.Deactivated, SWPTransition.Deactivate);
            RaiseActivationFailed();
        }

        private void RaiseActivationFailed()
        {
            var handler = ActivationFailed;
            handler?.Invoke();
        }

        private void SetState(SWPState next, SWPTransition transition)
        {
            var previous = state;
            state = next;

            if(previous == next)
            {
                return;
            }

            this.Log(LogLevel.Info, "Link {0} -> {1} ({2})", previous, next, transition);

            var handler = StateChanged;
            handler?.Invoke(new SWPStateChangedEventArgs(previous, next, transition));
        }

        /// <summary>Exposed so a host can route SWP's virtual-time delays onto its own sequencer.</summary>
        public SWPTimer Timer => timer;

        private readonly int port;
        private readonly SWPTimer timer;
        private readonly Queue<byte[]> pendingToSlave = new Queue<byte[]>();
        private readonly object pendingLock = new object();
        private readonly object socketLock = new object();

        private volatile SWPState state;
        private volatile bool activating;
        private volatile bool slaveAnswered;
        private volatile bool idleWatchArmed;
        private volatile bool clientConnected;
        private volatile ISWPSlave attachedSlave;

        private long activityCounter;

        [Transient]
        private SocketServerProvider socket;
    }
}
