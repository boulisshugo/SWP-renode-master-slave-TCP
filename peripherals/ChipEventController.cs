//
// ChipEventController.cs
//
// External-stimulus injection for Renode. Opcodes arriving over TCP become
// events on the simulated chip - a button press, a host bringing one of the
// chip's interfaces in or out of reset.
//
// The split:
//
//   IChipEventController      chip-agnostic contract. Ten events, nothing else.
//   ChipEventServer           all the TCP/session/dispatch plumbing. Reusable,
//                             knows nothing about any particular chip.
//   DelayedSequencer          runs (delay, action) scripts on virtual time.
//   ChipEventControllerBase   the three glued together. Subclass this and
//                             override only the events your chip actually has.
//   GenericChipEventController a worked example that drives one GPIO per
//                             interface. Use it directly or copy it.
//
// There is no memory-mapped register block: this peripheral is pure stimulus.
//
// Acknowledgement timing: an event is acknowledged only once the chip has
// finished reacting to it, including any virtual-time delays the chip schedules
// on the DelayedSequencer. The socket thread is the one that waits - never the
// emulation thread - so the simulation keeps running throughout.
//
// Keep everything in one file so the Monitor can compile it at runtime:
//
//     include @ChipEventController.cs
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;

using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

using Dynamitey;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // ==================================================================
    //  Protocol vocabulary
    // ==================================================================

    /// <summary>
    /// Event opcodes. One byte in, two bytes out: [status, interfaceBitmask].
    /// Values are fixed by the wire protocol - do not renumber.
    /// </summary>
    public enum ChipEventOpcode : byte
    {
        Ping           = 0x00,
        PowerUpIso     = 0x01,
        PowerDownIso   = 0x02,
        PowerUpSpi     = 0x03,
        PowerDownSpi   = 0x04,
        PowerUpI2C     = 0x05,
        PowerDownI2C   = 0x06,
        PowerUpSWP     = 0x07,
        PowerDownSWP   = 0x08,
        PowerUpI3C     = 0x09,
        PowerDownI3C   = 0x0A,
        QueryStatus    = 0xFF,
    }

    /// <summary>
    /// Control opcodes, valid only on the control port. Two bytes in
    /// ([opcode, argument]), four bytes out ([status, id, portHi, portLo]).
    /// The 0xC0-0xCF range is reserved so the first byte is self-describing.
    /// </summary>
    public enum ChipControlOpcode : byte
    {
        RequestSession = 0xC0,
        CloseSession   = 0xC1,
        ListSessions   = 0xC2,
        ControlPing    = 0xC3,
    }

    /// <summary>
    /// The interfaces the protocol can address. The numeric value is the bit
    /// position inside the status bitmask.
    /// </summary>
    public enum ChipInterface
    {
        Iso = 0,
        Spi = 1,
        I2C = 2,
        SWP = 3,
        I3C = 4,
    }

    /// <summary>First byte of every reply.</summary>
    public enum ChipEventStatus : byte
    {
        Ok              = 0x00,
        NoChange        = 0x01,  // valid request, interface already in that state
        UnknownOpcode   = 0xE0,
        NoFreeSession   = 0xE1,
        InternalError   = 0xE2,
        NoSuchSession   = 0xE3,
        Unsupported     = 0xE4,  // this chip does not have that interface
        Timeout         = 0xE5,  // chip did not finish reacting in time
    }

    // ==================================================================
    //  The chip-agnostic contract
    // ==================================================================

    /// <summary>
    /// What a chip has to provide. Nothing about sockets, ports, framing or
    /// bookkeeping appears here - implement this and the transport is handled
    /// for you by <see cref="ChipEventServer"/>.
    ///
    /// Two ways to use it:
    ///   * derive from <see cref="ChipEventControllerBase"/> and override the
    ///     events your chip has (easiest, the server is built in), or
    ///   * implement this interface on your own class and hand it to a
    ///     <see cref="ChipEventServer"/> yourself (composition, if your class
    ///     already has a base class it must keep).
    ///
    /// Implementations may assume they are called on the emulation thread, so
    /// touching GPIOs, IRQs or other peripherals from inside these methods is
    /// safe. The server does the marshalling.
    /// </summary>
    public interface IChipEventController : IPeripheral
    {
        void PowerUpIso();
        void PowerDownIso();
        void PowerUpSpi();
        void PowerDownSpi();
        void PowerUpI2C();
        void PowerDownI2C();
        void PowerUpSWP();
        void PowerDownSWP();
        void PowerUpI3C();
        void PowerDownI3C();

        /// <summary>
        /// Declare which interfaces this chip actually has. Return false and the
        /// server answers <see cref="ChipEventStatus.Unsupported"/> without ever
        /// calling the corresponding method.
        /// </summary>
        bool SupportsInterface(ChipInterface iface);
    }

    /// <summary>
    /// One outstanding "has the chip finished reacting yet?" question. Created on
    /// the requesting thread before the event is handed to the emulation thread,
    /// which is what makes the handshake race-free: by the time anyone could
    /// signal it, the waiter already holds it.
    /// </summary>
    public sealed class ChipEventTicket
    {
        /// <summary>Called on the emulation thread when the chip is done.</summary>
        public void Complete()
        {
            handle.Set();
        }

        /// <summary>Called on the socket thread. False means it timed out.</summary>
        public bool Wait(TimeSpan timeout)
        {
            try
            {
                return handle.Wait(timeout);
            }
            catch(Exception)
            {
                return false;
            }
        }

        private readonly ManualResetEventSlim handle = new ManualResetEventSlim(false);
    }

    /// <summary>
    /// Optional add-on to <see cref="IChipEventController"/>. Implement it - or
    /// just derive from <see cref="ChipEventControllerBase"/>, which does - and
    /// the server will hold the acknowledgement back until the chip says it has
    /// finished. Chips that do not implement it are acknowledged immediately,
    /// exactly as before.
    /// </summary>
    public interface IChipEventCompletionSource
    {
        /// <summary>
        /// Called on the requesting thread, before the event reaches the
        /// emulation thread. Return null to opt out of waiting for this event.
        /// </summary>
        ChipEventTicket BeginEvent(ChipInterface iface, bool powerOn);

        /// <summary>
        /// Called on the emulation thread as soon as the event handler returns.
        /// Complete the ticket now if the chip is already settled, or hold it and
        /// complete it later when the scheduled work drains.
        /// </summary>
        void EndEvent(ChipEventTicket ticket);
    }

    // ==================================================================
    //  Transport: one control port, N dynamically bound session ports
    // ==================================================================

    /// <summary>
    /// A privately-bound port serving exactly one client. The listener closes as
    /// soon as that client disconnects, so ids and ports are recycled rather
    /// than accumulating over a long simulation.
    /// </summary>
    public sealed class ChipEventSession
    {
        public ChipEventSession(byte id, TcpListener listener)
        {
            Id = id;
            Listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            CreatedAt = DateTime.UtcNow;
        }

        public byte Id { get; }
        public int Port { get; }
        public DateTime CreatedAt { get; }
        public TcpListener Listener { get; }

        public volatile bool Connected;
        public TcpClient Client;

        public void Shutdown()
        {
            try { Listener.Stop(); }
            catch(Exception) { /* best effort */ }

            var client = Client;
            if(client != null)
            {
                try { client.Close(); }
                catch(Exception) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Everything generic: listening, session brokering, framing, opcode decode,
    /// state bookkeeping and thread marshalling. Point it at any
    /// <see cref="IChipEventController"/> and it works.
    ///
    /// Raw binary throughout - no Telnet/IAC negotiation, so the stream is pure
    /// protocol bytes from the very first one.
    /// </summary>
    public sealed class ChipEventServer : IDisposable
    {
        public ChipEventServer(IMachine machine, IChipEventController target, IEmulationElement owner,
            int controlPort = 3456, int sessionPortBase = 0, int maxSessions = 16,
            int unclaimedSessionTimeout = 60, int completionTimeout = 5)
        {
            this.machine = machine;
            this.target = target;
            this.owner = owner;
            this.controlPort = controlPort;
            this.sessionPortBase = sessionPortBase;
            this.maxSessions = Math.Max(1, Math.Min(maxSessions, 255));
            this.unclaimedSessionTimeout = unclaimedSessionTimeout;
            this.completionTimeout = TimeSpan.FromSeconds(Math.Max(1, completionTimeout));
            this.sessions = new Dictionary<byte, ChipEventSession>();
        }

        public int ControlPort => controlPort;

        public bool IsRunning => running;

        /// <summary>Bitmask of interfaces currently powered, indexed by <see cref="ChipInterface"/>.</summary>
        public uint InterfaceStatus => interfaceStatus;

        /// <summary>Raised on the emulation thread after an interface changes state.</summary>
        public event Action<ChipInterface, bool> InterfacePowerChanged;

        // --- dispatch -------------------------------------------------------

        /// <summary>
        /// Decode an opcode and drive the target. Called from socket threads, and
        /// available directly for Monitor-driven injection.
        /// </summary>
        /// <param name="waitForCompletion">
        /// True (the socket path) blocks the *calling* thread until the chip has
        /// finished reacting, so the acknowledgement and the status bitmask both
        /// reflect the finished event. The emulation thread is never blocked.
        /// Pass false from the Monitor, where blocking would be unwelcome and a
        /// paused machine would simply time out.
        /// </param>
        public ChipEventStatus Dispatch(ChipEventOpcode opcode, bool waitForCompletion = true)
        {
            if(opcode == ChipEventOpcode.Ping || opcode == ChipEventOpcode.QueryStatus)
            {
                return ChipEventStatus.Ok;
            }

            ChipInterface iface;
            bool powerOn;
            if(!TryDecode(opcode, out iface, out powerOn))
            {
                owner.Log(LogLevel.Warning, "Rejected unknown chip event opcode 0x{0:X2}", (byte)opcode);
                return ChipEventStatus.UnknownOpcode;
            }

            if(!target.SupportsInterface(iface))
            {
                owner.Log(LogLevel.Warning, "{0} is not present on this chip, ignoring {1}", iface, opcode);
                return ChipEventStatus.Unsupported;
            }

            // The ticket is taken out *before* the hand-off. Doing it afterwards
            // would race: the emulation thread can run the whole event, delays and
            // all, before this thread gets scheduled again.
            var completionSource = waitForCompletion ? target as IChipEventCompletionSource : null;
            var ticket = completionSource != null ? completionSource.BeginEvent(iface, powerOn) : null;

            // Socket threads live outside the emulation's time domain. Handing the
            // call over means the chip implementation always runs on the emulation
            // thread and never has to think about locking.
            machine.HandleTimeDomainEvent(Apply, new PowerRequest(iface, powerOn, ticket), false);

            if(ticket != null && !ticket.Wait(completionTimeout))
            {
                owner.Log(LogLevel.Warning,
                    "{0} power {1} did not complete within {2}s - acknowledging anyway. Is the machine paused, "
                    + "or is a scheduled step never running?",
                    iface, powerOn ? "up" : "down", completionTimeout.TotalSeconds);
                return ChipEventStatus.Timeout;
            }

            return ChipEventStatus.Ok;
        }

        public void ResetState()
        {
            interfaceStatus = 0;
        }

        private static bool TryDecode(ChipEventOpcode opcode, out ChipInterface iface, out bool powerOn)
        {
            var raw = (byte)opcode;
            if(raw < 0x01 || raw > 0x0A)
            {
                iface = ChipInterface.Iso;
                powerOn = false;
                return false;
            }

            // 0x01/0x02 -> Iso, 0x03/0x04 -> Spi, ... odd is up, even is down.
            iface = (ChipInterface)((raw - 1) / 2);
            powerOn = (raw % 2) == 1;
            return true;
        }

        private void Apply(PowerRequest request)
        {
            try
            {
                InvokeTarget(request.Interface, request.PowerOn);
            }
            catch(Exception e)
            {
                owner.Log(LogLevel.Error, "Chip implementation threw handling {0} power {1}: {2}",
                    request.Interface, request.PowerOn ? "up" : "down", e.Message);

                // Never leave a socket thread hanging on a ticket nobody will
                // complete - it would sit there until the timeout for nothing.
                if(request.Ticket != null)
                {
                    request.Ticket.Complete();
                }
                return;  // state is left untouched: the event did not take effect
            }

            var mask = 1u << (int)request.Interface;
            interfaceStatus = request.PowerOn ? interfaceStatus | mask : interfaceStatus & ~mask;

            owner.Log(LogLevel.Debug, "{0} powered {1} (status now 0x{2:X2})",
                request.Interface, request.PowerOn ? "up" : "down", interfaceStatus);

            InterfacePowerChanged?.Invoke(request.Interface, request.PowerOn);

            if(request.Ticket != null)
            {
                // The handler has returned, so anything the chip scheduled is
                // already queued and the "still busy?" answer is trustworthy.
                var completionSource = target as IChipEventCompletionSource;
                if(completionSource != null)
                {
                    completionSource.EndEvent(request.Ticket);
                }
                else
                {
                    request.Ticket.Complete();
                }
            }
        }

        private void InvokeTarget(ChipInterface iface, bool powerOn)
        {
            switch(iface)
            {
                case ChipInterface.Iso:
                    if(powerOn) target.PowerUpIso(); else target.PowerDownIso();
                    break;
                case ChipInterface.Spi:
                    if(powerOn) target.PowerUpSpi(); else target.PowerDownSpi();
                    break;
                case ChipInterface.I2C:
                    if(powerOn) target.PowerUpI2C(); else target.PowerDownI2C();
                    break;
                case ChipInterface.SWP:
                    if(powerOn) target.PowerUpSWP(); else target.PowerDownSWP();
                    break;
                case ChipInterface.I3C:
                    if(powerOn) target.PowerUpI3C(); else target.PowerDownI3C();
                    break;
                default:
                    throw new ArgumentOutOfRangeException("iface");
            }
        }

        // --- lifecycle -------------------------------------------------------

        public void Start()
        {
            lock(lifecycleLock)
            {
                if(controlListener != null)
                {
                    owner.Log(LogLevel.Warning, "Chip event server is already listening on port {0}", controlPort);
                    return;
                }

                controlListener = new TcpListener(IPAddress.Loopback, controlPort);
                controlListener.Start();
                running = true;

                StartThread(ControlAcceptLoop, "control");
                StartThread(ReaperLoop, "reaper");

                owner.Log(LogLevel.Debug, "Chip event control port listening on 127.0.0.1:{0} (up to {1} sessions)",
                    controlPort, maxSessions);
            }
        }

        public void Stop()
        {
            lock(lifecycleLock)
            {
                if(controlListener == null)
                {
                    return;
                }

                running = false;

                try { controlListener.Stop(); }
                catch(Exception) { }
                controlListener = null;

                lock(controlClients)
                {
                    foreach(var client in controlClients)
                    {
                        try { client.Close(); }
                        catch(Exception) { }
                    }
                    controlClients.Clear();
                }

                List<ChipEventSession> toClose;
                lock(sessionsLock)
                {
                    toClose = sessions.Values.ToList();
                    sessions.Clear();
                }
                foreach(var session in toClose)
                {
                    session.Shutdown();
                }

                owner.Log(LogLevel.Debug, "Chip event server on port {0} stopped", controlPort);
            }
        }

        public void Dispose() => Stop();

        public IList<ChipEventSession> GetSessions()
        {
            lock(sessionsLock)
            {
                return sessions.Values.OrderBy(s => s.Id).ToList();
            }
        }

        public bool CloseSession(byte id)
        {
            ChipEventSession session;
            lock(sessionsLock)
            {
                if(!sessions.TryGetValue(id, out session))
                {
                    return false;
                }
                sessions.Remove(id);
            }

            session.Shutdown();
            owner.Log(LogLevel.Debug, "Chip event session {0} on port {1} closed", session.Id, session.Port);
            return true;
        }

        // --- session allocation ------------------------------------------------

        private ChipEventSession CreateSession()
        {
            lock(sessionsLock)
            {
                if(sessions.Count >= maxSessions)
                {
                    owner.Log(LogLevel.Warning, "Refusing session request: {0} sessions already open", sessions.Count);
                    return null;
                }

                byte id = 0;
                for(var candidate = 1; candidate <= 255; candidate++)
                {
                    if(!sessions.ContainsKey((byte)candidate))
                    {
                        id = (byte)candidate;
                        break;
                    }
                }
                if(id == 0)
                {
                    return null;
                }

                var listener = BindSessionListener();
                if(listener == null)
                {
                    return null;
                }

                var session = new ChipEventSession(id, listener);
                sessions[id] = session;

                var thread = new Thread(() => SessionAcceptLoop(session))
                {
                    IsBackground = true,
                    Name = "ChipEventServer:session:" + session.Port
                };
                thread.Start();

                owner.Log(LogLevel.Debug, "Chip event session {0} allocated on 127.0.0.1:{1}", session.Id, session.Port);
                return session;
            }
        }

        /// <summary>
        /// sessionPortBase == 0 means "let the OS pick an ephemeral port", which
        /// never collides. A non-zero base scans upwards for the first free port,
        /// for setups where the range must be predictable.
        /// </summary>
        private TcpListener BindSessionListener()
        {
            if(sessionPortBase == 0)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    return listener;
                }
                catch(SocketException e)
                {
                    owner.Log(LogLevel.Error, "Could not bind an ephemeral session port: {0}", e.Message);
                    return null;
                }
            }

            for(var port = sessionPortBase; port < sessionPortBase + maxSessions * 4 && port <= 65535; port++)
            {
                if(port == controlPort)
                {
                    continue;
                }
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    return listener;
                }
                catch(SocketException)
                {
                    // in use, try the next one
                }
            }

            owner.Log(LogLevel.Error, "No free port in range {0}-{1} for a new session",
                sessionPortBase, sessionPortBase + maxSessions * 4);
            return null;
        }

        /// <summary>
        /// Closes sessions handed out but never connected to. Without this, a
        /// client that dies between the handshake and the reconnect leaks a bound
        /// listener for the rest of the simulation.
        /// </summary>
        private void ReaperLoop()
        {
            while(running)
            {
                Thread.Sleep(1000);
                if(unclaimedSessionTimeout <= 0)
                {
                    continue;
                }

                var deadline = DateTime.UtcNow.AddSeconds(-unclaimedSessionTimeout);
                List<ChipEventSession> stale;
                lock(sessionsLock)
                {
                    stale = sessions.Values.Where(s => !s.Connected && s.CreatedAt < deadline).ToList();
                    foreach(var session in stale)
                    {
                        sessions.Remove(session.Id);
                    }
                }

                foreach(var session in stale)
                {
                    owner.Log(LogLevel.Warning, "Reclaiming session {0} on port {1}: never connected within {2}s",
                        session.Id, session.Port, unclaimedSessionTimeout);
                    session.Shutdown();
                }
            }
        }

        // --- accept loops --------------------------------------------------------

        private void StartThread(ThreadStart body, string role)
        {
            var thread = new Thread(body)
            {
                IsBackground = true,
                Name = "ChipEventServer:" + role + ":" + controlPort
            };
            thread.Start();
        }

        private void ControlAcceptLoop()
        {
            while(running)
            {
                TcpClient client;
                try
                {
                    client = controlListener.AcceptTcpClient();
                }
                catch(Exception)
                {
                    break;  // listener stopped or disposed
                }

                lock(controlClients)
                {
                    controlClients.Add(client);
                }

                var thread = new Thread(() => ControlClientLoop(client))
                {
                    IsBackground = true,
                    Name = "ChipEventServer:controlClient:" + controlPort
                };
                thread.Start();
            }
        }

        private void SessionAcceptLoop(ChipEventSession session)
        {
            TcpClient client;
            try
            {
                client = session.Listener.AcceptTcpClient();
            }
            catch(Exception)
            {
                return;  // reaped or closed before anyone connected
            }

            session.Connected = true;
            session.Client = client;

            // One client per session: stop listening so nobody else lands here.
            try { session.Listener.Stop(); }
            catch(Exception) { }

            owner.Log(LogLevel.Debug, "Session {0} claimed by {1}", session.Id, SafeEndpoint(client));

            try
            {
                ServeEvents(client, "session " + session.Id);
            }
            finally
            {
                lock(sessionsLock)
                {
                    sessions.Remove(session.Id);
                }
                session.Shutdown();
                owner.Log(LogLevel.Debug, "Chip event session {0} on port {1} ended", session.Id, session.Port);
            }
        }

        // --- per-connection protocol -------------------------------------------------

        private void ControlClientLoop(TcpClient client)
        {
            owner.Log(LogLevel.Debug, "Control client connected from {0}", SafeEndpoint(client));

            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var first = new byte[1];

                while(running)
                {
                    if(!ReadExactly(stream, first, 1))
                    {
                        break;
                    }

                    var opcode = first[0];
                    byte[] reply;

                    if(opcode >= ControlRangeStart && opcode <= ControlRangeEnd)
                    {
                        var argument = new byte[1];
                        if(!ReadExactly(stream, argument, 1))
                        {
                            break;
                        }
                        reply = HandleControl((ChipControlOpcode)opcode, argument[0]);
                    }
                    else
                    {
                        // Plain event opcode on the control port - the simple path,
                        // for clients that do not care about sessions.
                        reply = EventReply((ChipEventOpcode)opcode);
                    }
                    owner.Log(LogLevel.Debug, "Reply : {0}", Misc.PrettyPrintCollectionHex(reply));
                    if(!Write(stream, reply))
                    {
                        break;
                    }
                }
            }
            finally
            {
                lock(controlClients)
                {
                    controlClients.Remove(client);
                }
                try { client.Close(); }
                catch(Exception) { }
                owner.Log(LogLevel.Debug, "Control client disconnected");
            }
        }

        private void ServeEvents(TcpClient client, string label)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = new byte[1];

                while(running)
                {
                    if(!ReadExactly(stream, request, 1))
                    {
                        break;
                    }

                    byte[] data = EventReply((ChipEventOpcode)request[0]);
                    owner.Log(LogLevel.Debug, "Reply to the event : {0} for the request : 0x{1:X2}", Misc.PrettyPrintCollectionHex(data), request[0]);
                    if(!Write(stream, data))
                    {
                        break;
                    }
                }
            }
            catch(Exception e)
            {
                owner.Log(LogLevel.Warning, "{0} terminated: {1}", label, e.Message);
            }
            finally
            {
                try { client.Close(); }
                catch(Exception) { }
            }
        }

        /// <summary>
        /// Runs on a socket thread. Dispatch does not return until the chip has
        /// settled, so the status byte below is the post-event bitmask.
        /// </summary>
        private byte[] EventReply(ChipEventOpcode opcode)
        {
            ChipEventStatus status;
            try
            {
                status = Dispatch(opcode);
            }
            catch(Exception e)
            {
                owner.Log(LogLevel.Error, "Dispatch threw for opcode 0x{0:X2}: {1}", (byte)opcode, e.Message);
                status = ChipEventStatus.InternalError;
            }
            return new byte[] { (byte)status, (byte)(interfaceStatus & 0xFF) };
        }

        private byte[] HandleControl(ChipControlOpcode opcode, byte argument)
        {
            switch(opcode)
            {
                case ChipControlOpcode.ControlPing:
                    return ControlReply(ChipEventStatus.Ok, 0, 0);

                case ChipControlOpcode.RequestSession:
                {
                    var session = CreateSession();
                    return session == null
                        ? ControlReply(ChipEventStatus.NoFreeSession, 0, 0)
                        : ControlReply(ChipEventStatus.Ok, session.Id, session.Port);
                }

                case ChipControlOpcode.CloseSession:
                    return CloseSession(argument)
                        ? ControlReply(ChipEventStatus.Ok, argument, 0)
                        : ControlReply(ChipEventStatus.NoSuchSession, argument, 0);

                case ChipControlOpcode.ListSessions:
                {
                    var open = GetSessions();
                    var payload = new List<byte>(ControlReply(ChipEventStatus.Ok, (byte)open.Count, 0));
                    foreach(var session in open)
                    {
                        payload.Add(session.Id);
                        payload.Add((byte)(session.Port >> 8));
                        payload.Add((byte)(session.Port & 0xFF));
                        payload.Add((byte)(session.Connected ? 1 : 0));
                    }
                    return payload.ToArray();
                }

                default:
                    owner.Log(LogLevel.Warning, "Unknown control opcode 0x{0:X2}", (byte)opcode);
                    return ControlReply(ChipEventStatus.UnknownOpcode, 0, 0);
            }
        }

        private static byte[] ControlReply(ChipEventStatus status, byte id, int port)
        {
            return new byte[] { (byte)status, id, (byte)(port >> 8), (byte)(port & 0xFF) };
        }

        private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while(offset < count)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, offset, count - offset);
                }
                catch(Exception)
                {
                    return false;
                }
                if(read <= 0)
                {
                    return false;  // peer closed
                }
                offset += read;
            }
            return true;
        }

        private static bool Write(NetworkStream stream, byte[] data)
        {
            try
            {
                stream.Write(data, 0, data.Length);
                stream.Flush();
                return true;
            }
            catch(Exception)
            {
                return false;
            }
        }

        private static string SafeEndpoint(TcpClient client)
        {
            try { return client.Client.RemoteEndPoint.ToString(); }
            catch(Exception) { return "<unknown>"; }
        }

        private struct PowerRequest
        {
            public PowerRequest(ChipInterface iface, bool powerOn, ChipEventTicket ticket)
            {
                Interface = iface;
                PowerOn = powerOn;
                Ticket = ticket;
            }

            public ChipInterface Interface { get; }
            public bool PowerOn { get; }
            public ChipEventTicket Ticket { get; }
        }

        private const byte ControlRangeStart = 0xC0;
        private const byte ControlRangeEnd = 0xCF;

        private readonly IMachine machine;
        private readonly IChipEventController target;
        private readonly IEmulationElement owner;
        private readonly int controlPort;
        private readonly int sessionPortBase;
        private readonly int maxSessions;
        private readonly int unclaimedSessionTimeout;
        private readonly TimeSpan completionTimeout;
        private readonly Dictionary<byte, ChipEventSession> sessions;
        private readonly List<TcpClient> controlClients = new List<TcpClient>();
        private readonly object sessionsLock = new object();
        private readonly object lifecycleLock = new object();

        private TcpListener controlListener;
        private volatile bool running;
        private volatile uint interfaceStatus;
    }

    // ==================================================================
    //  Delayed actions on virtual time
    // ==================================================================

    /// <summary>One queued action and how long to wait before running it.</summary>
    public sealed class DelayedStep
    {
        /// <param name="delay">
        /// Virtual time to wait *after the previous step* before running this
        /// one. Clamped to a minimum of one microsecond.
        /// </param>
        public static DelayedStep After(TimeInterval delay, Action action)
        {
            return new DelayedStep(delay, action);
        }

        public static DelayedStep After(ulong delayMicroseconds, Action action)
        {
            return new DelayedStep(TimeInterval.FromMicroseconds(delayMicroseconds), action);
        }

        public static DelayedStep AfterMilliseconds(float delayMilliseconds, Action action)
        {
            return new DelayedStep(TimeInterval.FromMilliseconds(delayMilliseconds), action);
        }

        /// <summary>Run as soon as the queue reaches it (one microsecond later).</summary>
        public static DelayedStep Now(Action action)
        {
            return new DelayedStep(MinimumDelay, action);
        }

        private DelayedStep(TimeInterval delay, Action action)
        {
            if(action == null)
            {
                throw new ArgumentNullException("action");
            }
            Delay = delay < MinimumDelay ? MinimumDelay : delay;
            Action = action;
        }

        public TimeInterval Delay { get; }
        public Action Action { get; }

        private static readonly TimeInterval MinimumDelay = TimeInterval.FromMicroseconds(1);
    }

    /// <summary>
    /// Executes <see cref="DelayedStep"/>s one after another in virtual time,
    /// never blocking a thread. Each step schedules the next one, so an idle
    /// sequencer holds no timer and costs nothing.
    ///
    /// Steps run on the emulation thread: driving GPIOs, IRQ status registers or
    /// other peripherals from inside them is safe.
    /// </summary>
    public sealed class DelayedSequencer
    {
        public DelayedSequencer(IMachine machine, IEmulationElement owner, string name = "chipEventDelay")
        {
            this.machine = machine;
            this.owner = owner;
            this.name = name;
        }

        /// <summary>True while there is still something queued or in flight.</summary>
        public bool Busy
        {
            get
            {
                lock(sync)
                {
                    return armed || inCallback || pending.Count > 0;
                }
            }
        }

        /// <summary>Raised on the emulation thread when the queue drains.</summary>
        public event Action Idle;

        /// <summary>Queue a script. Returns immediately; nothing blocks.</summary>
        public void Post(params DelayedStep[] steps)
        {
            if(steps == null || steps.Length == 0)
            {
                return;
            }

            lock(sync)
            {
                foreach(var step in steps)
                {
                    pending.Enqueue(step);
                }

                // If a step is currently executing, it re-arms on its way out;
                // arming here as well would run two steps at once.
                if(!armed && !inCallback)
                {
                    ArmNext();
                }
            }
        }

        /// <summary>
        /// Drop everything still queued (e.g. on reset, or when a power-down
        /// arrives while a power-up sequence is still playing out). An action
        /// already scheduled on the clock source still fires, but the generation
        /// check makes it a no-op.
        /// </summary>
        public void Cancel()
        {
            bool wasBusy;
            lock(sync)
            {
                wasBusy = armed || pending.Count > 0;
                pending.Clear();
                generation++;
                armed = false;
            }

            // Anyone waiting on this work must be released, or they sit there
            // until their timeout for a sequence that will never finish.
            if(wasBusy && !inCallback)
            {
                RaiseIdle();
            }
        }

        // --- internals ------------------------------------------------------

        // Must be called with `sync` held.
        private void ArmNext()
        {
            if(pending.Count == 0)
            {
                armed = false;
                return;
            }

            var expected = generation;
            var delay = pending.Peek().Delay;

            machine.ScheduleAction(delay, _ => OnElapsed(expected), name);
            armed = true;
        }

        private void OnElapsed(ulong expected)
        {
            DelayedStep step = null;

            lock(sync)
            {
                if(expected != generation)
                {
                    return;  // cancelled after this callback was scheduled
                }

                armed = false;
                inCallback = true;
                if(pending.Count > 0)
                {
                    step = pending.Dequeue();
                }
            }

            try
            {
                if(step != null)
                {
                    step.Action();
                }
            }
            catch(Exception e)
            {
                owner.Log(LogLevel.Error, "Delayed chip event step threw: {0}", e.Message);
            }
            finally
            {
                bool drained;
                lock(sync)
                {
                    inCallback = false;
                    ArmNext();
                    drained = !armed && pending.Count == 0;
                }

                if(drained)
                {
                    RaiseIdle();
                }
            }
        }

        private void RaiseIdle()
        {
            var idle = Idle;
            if(idle != null)
            {
                idle();
            }
        }

        private readonly IMachine machine;
        private readonly IEmulationElement owner;
        private readonly string name;
        private readonly Queue<DelayedStep> pending = new Queue<DelayedStep>();
        private readonly object sync = new object();

        private ulong generation;
        private bool armed;
        private volatile bool inCallback;
    }

    // ==================================================================
    //  The base class you subclass per chip
    // ==================================================================

    /// <summary>
    /// TCP already wired up. Derive from this and override the events your chip
    /// has; override <see cref="SupportsInterface"/> to declare the ones it does
    /// not. Anything you do not touch logs a warning and is otherwise harmless.
    ///
    ///     public class AcmeSecureElement : ChipEventControllerBase
    ///     {
    ///         public AcmeSecureElement(IMachine machine, int controlPort = 3456)
    ///             : base(machine, controlPort) { }
    ///
    ///         public override bool SupportsInterface(ChipInterface iface)
    ///             => iface != ChipInterface.SWP;      // no SWP on this part
    ///
    ///         public override void PowerUpIso()
    ///         {
    ///             Sequencer.Post(
    ///                 DelayedStep.Now(() => /* drive your pins here */),
    ///                 DelayedStep.After(400, () => /* ...and again, 400us later */));
    ///         }
    ///     }
    ///
    /// Overrides run on the emulation thread, so they may safely touch GPIOs,
    /// IRQs and other peripherals - but they must never block. Anything that
    /// needs to happen later goes on <see cref="Sequencer"/>, and the client's
    /// acknowledgement is automatically held back until the queue drains.
    /// </summary>
    public abstract class ChipEventControllerBase : IChipEventController, IChipEventCompletionSource, IDisposable
    {
        protected ChipEventControllerBase(IMachine machine, int controlPort = 3456, int sessionPortBase = 0,
            int maxSessions = 16, int unclaimedSessionTimeout = 60, bool autoStart = true,
            int completionTimeout = 5)
        {
            Machine = machine;
            Sequencer = new DelayedSequencer(machine, this, GetType().Name);
            Sequencer.Idle += CompletePendingTickets;

            Server = new ChipEventServer(machine, this, this, controlPort, sessionPortBase,
                maxSessions, unclaimedSessionTimeout, completionTimeout);

            if(autoStart)
            {
                Server.Start();
            }
        }

        // --- override these ---------------------------------------------------

        public virtual void PowerUpIso()    => NotImplemented(ChipEventOpcode.PowerUpIso);
        public virtual void PowerDownIso()  => NotImplemented(ChipEventOpcode.PowerDownIso);
        public virtual void PowerUpSpi()    => NotImplemented(ChipEventOpcode.PowerUpSpi);
        public virtual void PowerDownSpi()  => NotImplemented(ChipEventOpcode.PowerDownSpi);
        public virtual void PowerUpI2C()    => NotImplemented(ChipEventOpcode.PowerUpI2C);
        public virtual void PowerDownI2C()  => NotImplemented(ChipEventOpcode.PowerDownI2C);
        public virtual void PowerUpSWP()    => NotImplemented(ChipEventOpcode.PowerUpSWP);
        public virtual void PowerDownSWP()  => NotImplemented(ChipEventOpcode.PowerDownSWP);
        public virtual void PowerUpI3C()    => NotImplemented(ChipEventOpcode.PowerUpI3C);
        public virtual void PowerDownI3C()  => NotImplemented(ChipEventOpcode.PowerDownI3C);

        /// <summary>Every interface is present unless a subclass says otherwise.</summary>
        public virtual bool SupportsInterface(ChipInterface iface) => true;

        public virtual void Reset()
        {
            Sequencer.Cancel();
            CompletePendingTickets();
            Server.ResetState();
        }

        // --- acknowledgement handshake ------------------------------------------

        /// <summary>Called on the socket thread, before the event is handed over.</summary>
        public ChipEventTicket BeginEvent(ChipInterface iface, bool powerOn)
        {
            return new ChipEventTicket();
        }

        /// <summary>
        /// Called on the emulation thread once the override has returned. If the
        /// override queued delayed work, the ticket rides along until the queue
        /// drains; otherwise the event is already done.
        /// </summary>
        public void EndEvent(ChipEventTicket ticket)
        {
            if(ticket == null)
            {
                return;
            }

            if(!Sequencer.Busy)
            {
                ticket.Complete();
                return;
            }

            lock(ticketsLock)
            {
                pendingTickets.Add(ticket);
            }

            // Guard against the queue having drained in the moment between the
            // Busy check and the Add - better a slightly early ack than a stall.
            if(!Sequencer.Busy)
            {
                CompletePendingTickets();
            }
        }

        private void CompletePendingTickets()
        {
            List<ChipEventTicket> tickets;
            lock(ticketsLock)
            {
                if(pendingTickets.Count == 0)
                {
                    return;
                }
                tickets = pendingTickets.ToList();
                pendingTickets.Clear();
            }

            foreach(var ticket in tickets)
            {
                ticket.Complete();
            }
        }

        // --- provided for you ---------------------------------------------------

        protected IMachine Machine { get; }

        protected ChipEventServer Server { get; }

        /// <summary>Queue delayed work from your overrides. See <see cref="DelayedStep"/>.</summary>
        public DelayedSequencer Sequencer { get; }

        /// <summary>Bitmask of interfaces currently powered up.</summary>
        public uint InterfaceStatus => Server.InterfaceStatus;

        public event Action<ChipInterface, bool> InterfacePowerChanged
        {
            add { Server.InterfacePowerChanged += value; }
            remove { Server.InterfacePowerChanged -= value; }
        }

        public void StartServer() => Server.Start();

        public void StopServer() => Server.Stop();

        /// <summary>`sysbus.chipEvents Inject 0x01` - same path a socket client takes,
        /// except the Monitor is not made to wait for the chip to settle.</summary>
        public string Inject(int opcode)
        {
            return Server.Dispatch((ChipEventOpcode)(byte)opcode, waitForCompletion: false).ToString();
        }

        /// <summary>`sysbus.chipEvents PowerUp Iso`</summary>
        public string PowerUp(string interfaceName) => Power(interfaceName, true);

        /// <summary>`sysbus.chipEvents PowerDown Spi`</summary>
        public string PowerDown(string interfaceName) => Power(interfaceName, false);

        /// <summary>`sysbus.chipEvents ShowSessions`</summary>
        public string ShowSessions()
        {
            var builder = new StringBuilder();
            builder.AppendFormat("control port: {0}{1}", Server.ControlPort, Environment.NewLine);

            var sessions = Server.GetSessions();
            if(sessions.Count == 0)
            {
                builder.Append("no sessions open");
                return builder.ToString();
            }

            builder.AppendLine("  id   port    state");
            foreach(var session in sessions)
            {
                builder.AppendFormat("  {0,-4} {1,-7} {2}{3}", session.Id, session.Port,
                    session.Connected ? "connected" : "awaiting client", Environment.NewLine);
            }
            return builder.ToString();
        }

        public bool CloseSession(int id) => Server.CloseSession((byte)id);

        public virtual void Dispose()
        {
            Sequencer.Cancel();
            CompletePendingTickets();
            Server.Dispose();
        }

        // --- internals ------------------------------------------------------------

        private string Power(string interfaceName, bool powerOn)
        {
            ChipInterface iface;
            try
            {
                iface = (ChipInterface)Enum.Parse(typeof(ChipInterface), interfaceName, true);
            }
            catch(Exception)
            {
                return "unknown interface '" + interfaceName + "'; expected one of "
                    + string.Join(", ", Enum.GetNames(typeof(ChipInterface)));
            }

            var opcode = (ChipEventOpcode)(byte)(((int)iface * 2) + (powerOn ? 1 : 2));
            return Server.Dispatch(opcode, waitForCompletion: false).ToString();
        }

        private void NotImplemented(ChipEventOpcode opcode)
        {
            this.Log(LogLevel.Warning,
                "{0} reached the base class - override it in your chip, or return false from SupportsInterface",
                opcode);
        }

        private readonly List<ChipEventTicket> pendingTickets = new List<ChipEventTicket>();
        private readonly object ticketsLock = new object();
    }

    // ==================================================================
    //  A worked example / usable default
    // ==================================================================

    /// <summary>
    /// Reference implementation: one GPIO output per interface, high when
    /// powered. Wire the lines to whatever your firmware watches. Use it as-is
    /// for a chip whose only observable is a pin, or copy it as a starting point.
    /// </summary>
    public class GenericChipEventController : ChipEventControllerBase, INumberedGPIOOutput
    {
        public GenericChipEventController(IMachine machine, int controlPort = 3456, int sessionPortBase = 0,
            int maxSessions = 16, int unclaimedSessionTimeout = 60, bool autoStart = true)
            : base(machine, controlPort, sessionPortBase, maxSessions, unclaimedSessionTimeout, autoStart)
        {
            var connections = new Dictionary<int, IGPIO>();
            foreach(ChipInterface iface in Enum.GetValues(typeof(ChipInterface)))
            {
                connections[(int)iface] = new GPIO();
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
        }

        public override void PowerUpIso()   => Drive(ChipInterface.Iso, true);
        public override void PowerDownIso() => Drive(ChipInterface.Iso, false);
        public override void PowerUpSpi()   => Drive(ChipInterface.Spi, true);
        public override void PowerDownSpi() => Drive(ChipInterface.Spi, false);
        public override void PowerUpI2C()   => Drive(ChipInterface.I2C, true);
        public override void PowerDownI2C() => Drive(ChipInterface.I2C, false);
        public override void PowerUpSWP()   => Drive(ChipInterface.SWP, true);
        public override void PowerDownSWP() => Drive(ChipInterface.SWP, false);
        public override void PowerUpI3C()   => Drive(ChipInterface.I3C, true);
        public override void PowerDownI3C() => Drive(ChipInterface.I3C, false);

        public override void Reset()
        {
            base.Reset();
            foreach(var connection in Connections.Values)
            {
                connection.Unset();
            }
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void Drive(ChipInterface iface, bool level)
        {
            Connections[(int)iface].Set(level);
        }
    }
}