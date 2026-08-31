//
// SWP.cs — chip-agnostic Single Wire Protocol (ETSI TS 102 613) link model for Renode.
//
// What this file models, and what it deliberately does not:
//
//   MODELLED   the physical/link layer of SWP: the three interface states
//              (ACTIVATED / SUSPENDED / DEACTIVATED), the four transitions
//              (ACTIVATE / DEACTIVATE / SUSPEND / RESUME), the ETSI timing
//              parameters P1..P7 that govern them, and full-duplex raw byte
//              transport between exactly one master (CLF) and one slave (UICC).
//
//   NOT        the protocol layer. No SOF/EOF, no CRC, no bit stuffing, no HCI
//   MODELLED   (TS 102 622), no gates, no pipes, no ACT_* frame parsing. Frames
//              are generated and consumed by whatever sits at the two ends —
//              the host TCP client on the master side, and the firmware (or a
//              test) on the slave side. These peripherals move opaque bytes.
//
// The split mirrors the real wire: the master drives voltage (S1) and owns all
// timing; the slave is passive on voltage and answers by modulating current
// (S2). Both directions are live at once, which is why SendToSlave and
// SendToMaster are independent and neither waits for the other.
//
// Everything chip-specific is a property, so one pair of classes serves any
// platform. In particular every P1..P7 value is settable from the .repl — see
// SWPTimings for what each one means and where its default comes from.
//
// Usable two ways, unchanged:
//   * compiled into the Renode tree under Peripherals/SWP/, or
//   * loaded at runtime by the Monitor:   include @peripherals/SWP.cs
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SWP
{
    // ==================================================================
    //  Vocabulary
    // ==================================================================

    /// <summary>
    /// The three SWP interface states of ETSI TS 102 613. The state is a
    /// property of the *link*, driven by the master; the slave follows.
    ///
    ///   DEACTIVATED  S1 held constantly low. Nothing flows. The slave's other
    ///                contacts (ISO 7816 on C7) are unaffected.
    ///   SUSPENDED    S1 held constantly high. No bit clock, so no data, but
    ///                the link is alive and can be resumed quickly.
    ///   ACTIVATED    a bit clock runs on S1. Data flows in both directions.
    /// </summary>
    public enum SWPState
    {
        Deactivated = 0,
        Suspended = 1,
        Activated = 2,
    }

    /// <summary>The four transitions between those states.</summary>
    public enum SWPTransition
    {
        Activate,    // DEACTIVATED -> ACTIVATED
        Deactivate,  // any         -> DEACTIVATED
        Suspend,     // ACTIVATED   -> SUSPENDED
        Resume,      // SUSPENDED   -> ACTIVATED
    }

    /// <summary>
    /// The ETSI TS 102 613 link-management timing parameters, all settable from
    /// a .repl so no value is baked into the logic.
    ///
    /// IMPORTANT — the defaults below are engineering defaults, not quoted spec
    /// figures. The *semantics* of P1..P4 follow TS 102 613 clause 8 directly;
    /// P5..P7 cover the activation sequence, where this model assigns the
    /// commonly-used meanings listed under each property. ETSI publishes the
    /// normative numbers in the "SWP management timings" table of clause 8 —
    /// set the properties from your copy of the spec for a timing-exact model.
    /// The state machine reads these values at the moment it needs them, so
    /// overriding one in a .repl or from the Monitor changes behaviour with no
    /// code change.
    /// </summary>
    public sealed class SWPTimings
    {
        /// <summary>
        /// P1 — inactivity window in ACTIVATED. When nothing but idle bits has
        /// been on the wire for P1, the master may switch the link to
        /// SUSPENDED. Drives <see cref="SWPController.AutoSuspend"/>.
        /// </summary>
        public TimeInterval P1 { get; set; } = TimeInterval.FromMilliseconds(1);

        /// <summary>
        /// P2 — during RESUME the master sends a transition sequence followed
        /// by P2 consecutive idle bits; the link enters ACTIVATED at the end of
        /// the last of those bits. Expressed here as the resulting duration.
        /// </summary>
        public TimeInterval P2 { get; set; } = TimeInterval.FromMicroseconds(100);

        /// <summary>
        /// P3 — P3max: the deadline for the slave to answer a resume with its
        /// own transition sequence. Missing it is a link fault, reported by
        /// <see cref="SWPController.ResumeTimedOut"/>.
        /// </summary>
        public TimeInterval P3 { get; set; } = TimeInterval.FromMicroseconds(500);

        /// <summary>
        /// P4 — the master switches the link to DEACTIVATED by holding SWIO in
        /// state L for longer than P4. Modelled as the settling time before the
        /// link is observably DEACTIVATED.
        /// </summary>
        public TimeInterval P4 { get; set; } = TimeInterval.FromMilliseconds(1);

        /// <summary>
        /// P5 — activation: time from the master starting to drive the wire
        /// (leaving state L) until the line is electrically settled and the
        /// slave is powered enough to be addressed. The slave is told to
        /// activate at the end of P5.
        /// </summary>
        public TimeInterval P5 { get; set; } = TimeInterval.FromMilliseconds(1);

        /// <summary>
        /// P6 — activation: the deadline for the slave to answer that it is
        /// present and ready, measured from the end of P5. A slave that misses
        /// it leaves the link DEACTIVATED and raises
        /// <see cref="SWPController.ActivationFailed"/>.
        /// </summary>
        public TimeInterval P6 { get; set; } = TimeInterval.FromMilliseconds(10);

        /// <summary>
        /// P7 — activation: an overall budget for the whole ACTIVATE sequence
        /// (P5 + the slave's answer). Whichever of P6 and P7 expires first
        /// fails the activation, so P7 also guards a slave that answers late
        /// after an unusually long P5.
        /// </summary>
        public TimeInterval P7 { get; set; } = TimeInterval.FromMilliseconds(20);

        /// <summary>Copy, so a controller and its slave can be tuned apart.</summary>
        public SWPTimings Clone()
        {
            return new SWPTimings { P1 = P1, P2 = P2, P3 = P3, P4 = P4, P5 = P5, P6 = P6, P7 = P7 };
        }

        public override string ToString()
        {
            return string.Format(
                "P1={0}us P2={1}us P3={2}us P4={3}us P5={4}us P6={5}us P7={6}us",
                P1.TotalMicroseconds, P2.TotalMicroseconds, P3.TotalMicroseconds, P4.TotalMicroseconds,
                P5.TotalMicroseconds, P6.TotalMicroseconds, P7.TotalMicroseconds);
        }
    }

    // ==================================================================
    //  The two sides of the wire
    // ==================================================================

    /// <summary>
    /// Anything sitting on an SWP link. Both ends expose their view of the
    /// link state; they agree once a transition has settled.
    /// </summary>
    public interface ISWPEndpoint : IPeripheral
    {
        SWPState State { get; }
    }

    /// <summary>
    /// The master (CLF) side, as the slave sees it. The slave never drives
    /// voltage, so everything here is either a current-modulated answer or a
    /// request for the master to act.
    /// </summary>
    public interface ISWPMaster : ISWPEndpoint
    {
        /// <summary>S2 current modulation: raw bytes slave -> master. Never framed.</summary>
        void ReceiveFromSlave(byte[] data);

        /// <summary>Answer to ACTIVATE: the slave is present and ready. Must land inside P6/P7.</summary>
        void NotifySlaveActivated();

        /// <summary>Answer to RESUME: the slave's transition sequence. Must land inside P3.</summary>
        void NotifySlaveResumed();

        /// <summary>
        /// Slave-initiated RESUME. On real hardware the UICC modulates S2 while
        /// the link is SUSPENDED to ask the CLF for the bit clock back — that is
        /// how a card-emulation event wakes the link.
        /// </summary>
        void RequestResume();
    }

    /// <summary>
    /// The slave (UICC) side, as the master sees it. The master calls these on
    /// the emulation thread; implementations must not block.
    ///
    /// Each transition method is invoked at the point in virtual time where the
    /// spec says the slave first observes it: OnActivate after P5, OnResume at
    /// the end of the master's P2 idle bits, and so on. A slave that needs to
    /// answer (OnActivate, OnResume) does so through <see cref="ISWPMaster"/>,
    /// and is responsible for landing inside P6/P7 and P3 respectively.
    /// </summary>
    public interface ISWPSlave : ISWPEndpoint
    {
        /// <summary>Called once when the master attaches. Keep the reference to answer on.</summary>
        void AttachMaster(ISWPMaster master);

        void OnActivate();
        void OnDeactivate();
        void OnSuspend();
        void OnResume();

        /// <summary>S1 voltage modulation: raw bytes master -> slave. Never framed.</summary>
        void ReceiveFromMaster(byte[] data);
    }

    // ==================================================================
    //  Shared plumbing
    // ==================================================================

    /// <summary>
    /// Delayed work on virtual time, with cancellation. Each endpoint owns one.
    ///
    /// The steps posted here are independent deadlines running side by side,
    /// not a sequence: an ACTIVATE arms P5, P6 and P7 at once and whichever
    /// expires first decides the outcome. That is why this schedules straight
    /// onto the clock source and offers no hook to redirect it — routing these
    /// onto a serial queue would run P7 before P5 and fail every activation.
    /// A host that wants to know when a transition has settled should watch
    /// <see cref="SWPController.StateChanged"/> instead.
    /// </summary>
    public sealed class SWPTimer
    {
        public SWPTimer(IMachine machine, IEmulationElement owner, string name)
        {
            this.machine = machine;
            this.owner = owner;
            this.name = name;
        }

        /// <summary>Run <paramref name="action"/> after <paramref name="delay"/> of virtual time.</summary>
        public void Schedule(TimeInterval delay, Action action)
        {
            var expected = Volatile.Read(ref generation);
            Action guarded = () =>
            {
                // A transition that superseded this one bumped the generation;
                // firing now would apply a decision the link has moved past.
                if(Volatile.Read(ref generation) != expected)
                {
                    owner.Log(LogLevel.Noisy, "{0}: dropping superseded delayed step", name);
                    return;
                }
                try
                {
                    action();
                }
                catch(Exception e)
                {
                    owner.Log(LogLevel.Error, "{0}: delayed step threw: {1}", name, e.Message);
                }
            };

            machine.ScheduleAction(Clamp(delay), _ => guarded(), name);
        }

        /// <summary>
        /// Invalidate everything scheduled so far. Already-queued callbacks
        /// still fire on the clock source but become no-ops.
        /// </summary>
        public void CancelAll()
        {
            Interlocked.Increment(ref generation);
        }

        private static TimeInterval Clamp(TimeInterval delay)
        {
            return delay < Minimum ? Minimum : delay;
        }

        private static readonly TimeInterval Minimum = TimeInterval.FromMicroseconds(1);

        private readonly IMachine machine;
        private readonly IEmulationElement owner;
        private readonly string name;

        private long generation;
    }

    /// <summary>Arguments for <see cref="SWPController.StateChanged"/> and the slave's equivalent.</summary>
    public sealed class SWPStateChangedEventArgs : EventArgs
    {
        public SWPStateChangedEventArgs(SWPState previous, SWPState current, SWPTransition transition)
        {
            Previous = previous;
            Current = current;
            Transition = transition;
        }

        public SWPState Previous { get; }
        public SWPState Current { get; }
        public SWPTransition Transition { get; }

        public override string ToString() => $"{Previous} -> {Current} ({Transition})";
    }

    /// <summary>
    /// Hex helpers shared by both ends of the link. Public and in this file on
    /// purpose: the Monitor compiles each included .cs into its own assembly, so
    /// anything two files share has to be public and defined in the one loaded
    /// first.
    /// </summary>
    public static class SWPBytes
    {
        public static string Hex(byte[] data)
        {
            return string.Join(" ", data.Select(b => b.ToString("X2")));
        }

        public static byte[] ParseHex(string text)
        {
            if(string.IsNullOrWhiteSpace(text))
            {
                return new byte[0];
            }

            var cleaned = text.Replace("0x", " ").Replace("0X", " ")
                .Replace(",", " ").Replace("-", " ").Replace(":", " ");
            var tokens = cleaned.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var result = new List<byte>();
            foreach(var token in tokens)
            {
                // A bare run of hex digits is a byte string, e.g. "00A40400".
                if(token.Length > 2)
                {
                    if(token.Length % 2 != 0)
                    {
                        throw new RecoverableException("Odd number of hex digits in '" + token + "'");
                    }
                    for(var i = 0; i < token.Length; i += 2)
                    {
                        result.Add(ParseByte(token.Substring(i, 2)));
                    }
                    continue;
                }
                result.Add(ParseByte(token));
            }
            return result.ToArray();
        }

        private static byte ParseByte(string token)
        {
            byte value;
            if(!byte.TryParse(token, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                throw new RecoverableException("'" + token + "' is not a hex byte");
            }
            return value;
        }
    }
}
